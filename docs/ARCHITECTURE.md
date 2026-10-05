# FollowUp Officer — architecture

> The CEO manages exceptions; the system chases people for updates.

FollowUp Officer is a "digital executive follow-up officer". It captures delegated work. It follows up on a schedule. It reads related email and Microsoft Teams conversations. It detects commitments, delays and silence. It escalates, verifies completion, and tells the CEO only when intervention is needed. It runs continuously in the background, starts with the server, and resumes exactly where it stopped after any interruption.

## Solution layout

| Project | Responsibility |
|---|---|
| `FollowUp.Domain` | Entities, enums and **pure rule engines**: recurrence (`RecurrenceEngine`), working-day calendar (`BusinessCalendar`), follow-up schedules and the intelligent follow-up decision (`FollowUpDecisionEngine`), health and attention scoring (`TaskScoring`), escalation ladders (`EscalationEvaluator`), deadline wording, status transitions, task numbers, and task visibility (`TaskAccess`). It has no dependencies. |
| `FollowUp.Core` | EF Core model and migrations; task services; the durable **job engine**; follow-up, escalation, commitments, recurrence, the communication pipeline, sync checkpoints and the notification queue and dispatcher; dashboards, reports, digest and Ask; seeding. It depends only on **ports** for Microsoft Graph and AI. |
| `FollowUp.Integrations` | Microsoft Graph REST client (tokens, retry/Retry-After, paging, delta); Outlook, Teams, calendar and directory services; a **Graph-compatible emulator tenant** used for demos and tests. |
| `FollowUp.AI` | Provider-independent AI. A deterministic heuristic engine always works offline. Optional LLM providers (Anthropic Claude, OpenAI-compatible) sit behind a circuit breaker and fall back automatically. |
| `FollowUp.Api` | ASP.NET Core minimal APIs, cookie/Entra ID auth, SignalR, and hosting of the Angular build. It can run the job engine in-process and installs as a Windows Service. |
| `FollowUp.Worker` | The always-on background processor (Windows Service / systemd / container). It needs no browser and no web host. |
| `client/` | Angular 21 UI (standalone, signals, zoneless). |
| `deploy/` | Windows service installer with crash-recovery actions, a Dockerfile, and docker-compose with `restart: unless-stopped`. |

Ports (spec §77) live in `FollowUp.Core/Abstractions`. They are `IEmailService`, `ITeamsService`, `IMailSynchronizationService`, `ITeamsSynchronizationService`, `ICalendarService`, `IUserDirectoryService`, `IIntegrationHealth`, `IAIClassificationService`, `ICommitmentExtractionService` and `INaturalLanguageService`. The service abstractions named in the spec are in `FollowUp.Core/Services/Interfaces.cs`: `ITaskService`, `IRecurringTaskService`, `IReminderService`, `IEscalationService`, `INotificationService` and `ISynchronizationCheckpointService`. Task logic never references Microsoft Graph.

## Reliability design (spec §58–§66, §78)

**Everything that must survive a restart is in SQL Server.** That covers schedules, leases, checkpoints, queues and per-item processing state. Nothing the processor needs to resume is kept only in memory.

| Concern | Mechanism |
|---|---|
| Start with the machine | The Worker and API are Windows Services (`UseWindowsService`). The installer sets them to Automatic (Delayed Start), with recovery actions to restart after 30 s / 60 s / 120 s. Containers use `restart: unless-stopped`. Startup retries the database connection for about 2 minutes, so a boot before SQL Server is ready is harmless. |
| Durable scheduling | `ScheduledJobs` holds each job's schedule (interval, daily-at or weekly-at in the business time zone), `NextRunAt`, lease owner and expiry, last success and failure, consecutive failures and a job checkpoint. Every run writes a row to `BackgroundJobExecutions`. |
| One runner per job | A job is claimed with a conditional `UPDATE … WHERE LeaseExpiresAt < now`. Several hosts (Worker + API) can run the engine safely. Long runs extend the lease with a heartbeat. |
| Timeouts, retries, dead letters | Each run has a timeout. Failures back off exponentially and the job state goes `Failed` after `MaxRetries`. Work items have their own retry and dead-letter handling: notifications become `DeadLetter` after repeated failure, and messages become `Failed` after 6 attempts. |
| Crash recovery | On start, executions left `Running` by a dead process are marked `Abandoned`, and leases held by dead processes on this machine are released. Misfire policies then apply to runs missed during downtime: `RunOnce` (catch up once), `SkipToNext`, or `RunIfWithinWindow` (e.g. a digest more than 4 hours late is skipped and recorded). |
| Resume from the last position | Mail and channel sync store the delta link, next link, watermark and last id per source (`SynchronizationCheckpoints`). **Each page's messages and the new cursor commit in one transaction**, so a crash mid-sync resumes after the last committed page. An expired delta token causes a resync from the watermark, and duplicates are absorbed by the unique key. Recurring definitions store `LastProcessedOccurrence` and `NextOccurrenceAt`. Tasks store `NextFollowUpAt` and `LastFollowUpAt`. |
| Catch-up without floods | Follow-up points missed during downtime are **coalesced into one reminder**; the log records how many were folded. Reminders past `ExpiresAt` are expired, not sent. A newer reminder supersedes older pending ones for the same task and recipient. Escalation ladders fire **only the highest due step**, and lower steps are recorded as superseded. Non-critical reminders reached at night wait for the working morning. Per-recipient daily caps apply. |
| Missed recurring work | Every nominal occurrence becomes a `RecurringTaskInstance`. Each is created, created late, or skipped with a reason: holiday, paused, user, or catch-up policy (`GenerateAll`, `GenerateLatestOnly`, `SkipMissed`, `GenerateWithinWindow`). Nothing is silently lost. Missed important occurrences raise a CEO attention item. |
| Exactly-once effects | Unique indexes enforce this. They cover the message key (channel, conversation, external id), notification idempotency keys, escalation idempotency keys (task, step, cycle), follow-up points (task, scheduled-at), recurring occurrences (series, nominal date), attention dedupe keys and task numbers. Effects and their unique row commit together. |
| Delivery after a crash | Notifications are marked `Sending` before the external call. Rows stuck in `Sending` are reconciled: email looks up the `X-FollowUp-Key` extended property in Sent Items. Teams cannot be reconciled through Graph, so it is retried (at-least-once inside a small crash window). |
| External failures | Graph and AI failures are classified (Auth, Permission, Throttled, Transient, Gone, NotFound or Validation) and recorded in `IntegrationErrors`. The admin monitor shows them. AI failure always falls back to the heuristic engine. |

## Communication pipeline (spec §21–§29, §33)

1. **Sync** (`mail.sync`, `teams.sync`): checkpointed paging imports each message once. Our own reminders (the `X-FollowUp-Key` header or our signature) are ignored as a loop guard.
2. **Correlate** (`communication.process`), in order:
   - explicit task number;
   - an existing conversation thread linked to a task;
   - a reply within 72 h of a reminder about exactly one task;
   - participants, recipients and project mentions, producing candidates;
   - AI semantic ranking.
   A match at or above 0.80 attaches automatically. A match at or above 0.45 goes to the **review queue**. Lower matches are ignored.
3. **Classify and extract**: the category, blocker, commitment date, proposed due date and progress.
4. **Apply effects**: assignee activity (which clears no-response), commitments, non-closing status changes (shown as AI-applied), extension requests, CEO attention for delays and escalations on important work, and **completion suggestions**. A completion is never closed by AI; a human confirms it.

## Monitoring and attention (spec §35–§39, §79)

`MonitoringService` recomputes several things after every change and in a 5-minute sweep: the deadline instant, blocked state from dependencies (finish-to-start, start-to-start, external) or waiting statuses, health and the Attention Score (each with its contributing factors), and the **CEO attention queue**. That queue covers critical overdue work, repeated non-response, blocked critical work, unacknowledged or declined delegation, approvals and extensions awaiting the CEO, missed commitments, AI completion reports, cross-department dependency deadlocks, escalations to the CEO, critical messages and customer escalations, and missed recurring work. Items are deduplicated by key and resolve themselves when the condition clears. Items the CEO dismisses stay dismissed for that cycle.

## Security (spec §69, §70)

- Cookie authentication (HttpOnly, SameSite=Strict) with PBKDF2 password hashes, lockout and login rate limiting. Entra ID bearer tokens are accepted when configured.
- A CSRF guard header is required on mutating requests.
- Role-based access (Executive, Senior Manager, Team Leader, Team Member, Administrator) plus task-level visibility. The levels are Normal, Team Only, Management Only, CEO Only and Selected Participants. One rule (`TaskAccess`) has an equivalent SQL filter (`VisibleTo`) used by every query.
- Imported email and Teams messages are visible through the tasks they are linked to, to their sender, or to the owner of the monitored mailbox.
- Attachments are AES-256-GCM encrypted with per-file keys wrapped by ASP.NET Data Protection (DPAPI on Windows). Graph secrets are protected the same way.
- The audit trail is append-only. The DbContext guard and a SQL `INSTEAD OF UPDATE, DELETE` trigger enforce this, and each entry is hash-chained per task, so `/api/admin/audit/verify/{taskId}` detects tampering.
- Encryption in transit: deploy behind HTTPS (IIS / reverse proxy / Kestrel certificate). Encryption at rest for the database: SQL Server TDE.

## Microsoft Graph (spec §71)

- **Minimum application permissions**: Mail.ReadWrite (for the delta + extended property on Sent Items), Mail.Send, Chat.Read.All, ChannelMessage.Read.All, User.Read.All, Group.Read.All, Calendars.ReadWrite and TeamsActivity.Send.
- **Delegated permissions** for the follow-up service account: Chat.ReadWrite and ChannelMessage.Send. Graph only allows chat and channel posting with delegated tokens.
- The emulator mirrors the exact Graph endpoints and JSON shapes the client uses. That includes OAuth token issuance, `@odata.nextLink` / `@odata.deltaLink`, 410 `syncStateNotFound`, 429 with `Retry-After`, outages and auth failures. So the **real Graph client code path** runs in demos and tests. Switching to a real tenant is configuration only (`Microsoft365:Mode = Graph`).

## Spec coverage map

| § | Requirement | Where |
|---|---|---|
| 1 | Core entities, GUID keys, TASK-yyyy-nnnnn numbers | `Domain/*.cs`, `Ids.New()` (sequential GUIDs), atomic number allocation in `TaskService.AllocateNumberAsync` |
| 2 | Departments and teams (leader, deputy, members, DL, Teams team/channel) | `Organisation.cs`; Admin → Organisation |
| 3 | Roles | `AppRole` flags + `Viewer`; API policies |
| 4–5 | Fast delegation; assignment to people, teams, team leader; primary and accountable | `CreateTaskRequest`, `TaskService.CreateAsync`; Delegate dialog |
| 6 | Unlimited hierarchical subtasks | `ParentTaskId/RootTaskId/Depth`; subtask tree |
| 7 | Statuses | `WorkStatus` (+ derived "Overdue"), `TaskStatusRules` |
| 8 | Acknowledgement | `AcknowledgeAsync`; unacknowledged important tasks → attention |
| 9–10 | Follow-up schedules; intelligent follow-up | `FollowUpSchedule`, `FollowUpScheduler`, `FollowUpDecisionEngine`, `FollowUpService` |
| 11–15 | Recurring tasks, rules, completion and changes, missed occurrences | `RecurrenceRule/DueRule`, `RecurringTaskService` (scopes ThisInstance / ThisAndFuture split / EntireSeries; skip, pause, resume, end; catch-up policies) |
| 16 | Dependencies (finish-to-start, start-to-start, external) | `TaskDependency`, cycle check, "Blocked by TASK-…" |
| 17 | Deadlines and buckets | `DeadlineRules`, `DeadlineAt` |
| 18 | Priority and criticality drive channels, intervals, escalations and alerts | `NotificationService.ChannelsFor`, recent-update window, criticality-specific policies, attention rules |
| 19–20 | Email and Teams notifications (individual, group chat, channel, activity) | `NotificationService` (spec wording), `GraphEmailService`, `GraphTeamsService` |
| 21–24 | Read Teams; correlation; AI classification; threads | `GraphTeamsSyncService`, `CommunicationService`, `TaskConversation` |
| 25–27 | Outlook mail; matching; confidence and review queue | `GraphMailSyncService`, `TaskCommunicationLink` (method, confidence, signals) |
| 28–29 | Commitments; missed-commitment alerts | `Commitment`, `CommitmentService`, "commitments made to me" |
| 30–33 | Completion, approval, evidence, AI completion detection | `CompleteAsync`, `DecideApprovalAsync`, `ApprovalRules`, `TaskSuggestion` |
| 34–35 | Escalation engine; no-response detection | `EscalationEvaluator`, `EscalationService`, `NoResponseSince` |
| 36–37 | Health, Attention Score | `TaskScoring` with explained factors |
| 38–42 | CEO dashboard, Attention Required, Team, Individual, Workload | `DashboardService`, `/api/workbench`, `/api/attention`, `/api/dashboard/*`, `/api/workload` |
| 43–45 | Delegation chain, extensions, snooze | `TaskDelegation`, `ExtensionRequest`, `SnoozeAsync` |
| 46–47 | Projects (derived status), meeting action items | `Project`, `Meeting`/`MeetingActionItem` |
| 48–50 | Natural-language delegation, voice, questions | `FollowUp.AI` parsers, `/api/tasks/parse`, `/api/ask`, browser speech recognition |
| 51 | Templates | `TaskTemplate` (items, roles, offsets, dependencies, approval flow), instantiate |
| 52–53 | Search, filters, saved views | `/api/search`, `TaskFilter`, `SavedView` |
| 54–56 | Daily digest, weekly review, AI executive summary with drill-down | `DigestService`, `digest.daily`, `review.weekly` jobs |
| 57 | Quick actions | task endpoints and the task page action bar |
| 58–66 | Persistent background operation, auto-start, checkpoints, recovery, catch-up, idempotency, job engine, notification queue | see *Reliability design* |
| 67 | Integration monitoring | `/api/admin/monitor`, Admin → Integration monitor |
| 68 | Immutable audit | `AuditService` + SQL trigger + hash chain |
| 69–70 | Security; confidential tasks | see *Security* |
| 71 | Microsoft Graph (minimum permissions, throttling, paging, delta, token expiry) | `FollowUp.Integrations/Graph` |
| 72 | Outlook calendar for important deadlines, reviews and approvals | `CalendarSyncService`, `calendar.sync` |
| 73 | Working-day calendars | `WorkCalendar`, `Holiday`, `BusinessCalendar` (time zone, weekends, holidays) |
| 74 | Reports and analytics | `ReportService`, `/api/reports` |
| 75 | Data model | `Data/ModelConfiguration.cs`, `Data/Migrations` |
| 76–77 | Architecture and service abstractions | this document |
| 78 | Failure philosophy | tests in `tests/FollowUp.Tests/Core/ReliabilityTests.cs`, `BackgroundProcessingTests.cs`, `Integrations/Emulator*Tests.cs` |
| 79–80 | CEO workbench; product philosophy | Workbench page; attention-first design |

## Design decisions worth knowing

- **"Overdue" is derived, not stored as a status.** A late task stays `In Progress` or `Blocked`. The API exposes a `displayStatus` of "Overdue" and an `isOverdue` flag. This keeps the real workflow state for analytics.
- **A custom DB-backed job engine instead of Hangfire or Quartz** (§65 allows an equivalent). Domain checkpoints commit in the same transaction as the business effects. A generic scheduler would hold them in its own store and could not guarantee that.
- **Critical work and approvals.** Completion of Critical or Business Critical work, or of tasks that require approval, goes through confirmation stages (team leader, delegator, department head, CEO). AI can only *suggest* completion.
- **The original due date** is kept on the task and in extension requests (§44). Reports use it for delay analysis.
- **The demo seeder replays history through the real services** with a pinned clock. Reminders, escalations, commitments, approvals, recurrence and a simulated Friday-to-Monday outage are all genuine engine output.
