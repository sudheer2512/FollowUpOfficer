# FollowUp Officer — HTTP API contract

Base URL: `http://localhost:5860` (the API also serves the Angular build). Angular dev server: `http://localhost:4860` with a proxy for `/api` and `/hubs`.

**Conventions**

- JSON is camelCase. Enums are **strings** (e.g. `"InProgress"`, `"Critical"`); integers are also accepted on input. Flags enums are comma lists (`"Email, Teams"`).
- `DateOnly` is `"yyyy-MM-dd"`. `TimeOnly` is `"HH:mm:ss"`. `DateTime` is ISO-8601 UTC with `Z`.
- DTO shapes are the C# records in `src/FollowUp.Core/Contracts/*.cs`. The property names map 1:1 to camelCase. Small request/response shapes used below (`{ reason }`, `{ enabled }`, `{ queued }`, `{ count }`…) are in `Contracts/SystemContracts.cs`.
- Errors use RFC 7807 problem+json `{ type, title, status, detail, code, traceId }`. 400 means validation (`code` is stable), 401 means not signed in, 403 means not allowed, 404 means not found or not visible (an invisible task is always 404, never 403), 409 means a conflict or an invalid transition, 429 means rate limited. Unhandled errors are 500 with `code: "server_error"` and no internals.
- Auth: an HttpOnly cookie `fu.auth` (SameSite=Strict, 8 h sliding) is set by `POST /api/auth/login`. An Entra ID bearer token in `Authorization: Bearer …` is also accepted when `Entra:TenantId` is configured (SignalR: `?access_token=`). Every request re-validates the account (enabled, security stamp unchanged) — a password, role or enable change signs existing sessions out.
- CSRF guard: every non-GET `/api` request — including `POST /api/auth/login` — must send `X-Requested-With: FollowUp`, otherwise 403 `csrf_header_missing`.
- Real time: SignalR hub `/hubs/events` (authenticated). The server sends `event` with `{ topic, taskId?, payload? }`. Topics are `task.changed`, `task.created`, `attention.changed`, `communication.imported`, `notification.queued`, `inbox.changed` (sent only to the recipient), `sync.status` and `job.status`. Clients refetch what they show; data is re-authorised on fetch.

**Drill-downs and the task filter**

Tiles, summary points and Ask answers carry a `Drilldown { route, query }`. For `route: "/tasks"` the query keys are `TaskFilter` field names (camelCase); list values are comma-separated (`statuses=InProgress,Blocked`, `health=AtRisk,Critical`). Other routes used: `/attention`, `/commitments` (`status`, `toMe`, `employeeId`, `from`, `to`), `/recurring` (`missedOnly`, `from`, `to`), `/projects/{id}`, `/tasks/{id}`, `/`.

`TaskFilter` semantics (`POST /api/tasks/query`):

| Field | Meaning |
|---|---|
| `scope` | `mine` (assigned to me), `delegated` (I delegated it to someone else), `following`, `team` (my teams' tasks), anything else = all visible |
| `open` | `true` = active work (not draft, deferred, completed or cancelled); `false` = completed or cancelled |
| `teamId` | owned by the team, assigned to the team, or a team member is the primary responsible person |
| `bucket` | `DueToday`, `DueTomorrow`, `DueThisWeek` (today to Sunday, not yet overdue), `Later`, `Overdue` (overdue but less than the severe threshold), `SeverelyOverdue` (≥ `SeverelyOverdueDays`, default 7), `None` (no due date or closed). Work awaiting completion confirmation is not in the due buckets |
| `overdue`, `minDaysOverdue` | overdue = deadline passed and the work is still with the assignee; `minDaysOverdue: n` = at least n calendar days overdue |
| `waitingForCeo` | the current approval stage, a pending extension request, or a declined / clarification acknowledgement is waiting for an executive |
| `noResponse` | a reminder went unanswered (cleared by any update from the responsible people) |
| `awaitingApproval` | `CompletedByAssignee` or `CompletionAwaitingApproval` |
| `text` | task number, title, description or primary assignee name (case-insensitive) |
| `completedFrom` / `completedTo` | local business dates, inclusive |
| `sort` | `attention` (default), `due`, `updated`, `number`, `priority`, `completed` |
| `page`, `pageSize` | 1-based; `pageSize` 1–500 (default 50) |

## Auth
| Method | Route | Body → Response |
|---|---|---|
| GET | `/api/auth/config` | → `AuthConfigDto { entraEnabled, tenantId?, clientId?, apiScope?, demoMode, demoUsers: [{userName, name, title, role}] }` (anonymous). `demoMode` = `FollowUp:SeedDemoData` in Development, or `FollowUp:ShowDemoUsers=true`. The demo password is shown by the UI only. |
| POST | `/api/auth/login` | `{ userName, password }` → `MeDto`. 401 codes: `invalid_credentials`, `account_locked` (5 failures lock the account for 15 min), `account_disabled`. Rate limited to 20 attempts per minute per IP (429 `rate_limited`). |
| POST | `/api/auth/logout` | → 204 |
| GET | `/api/auth/me` | → `MeDto` |

## Lookups, search, views
| Method | Route | Body → Response |
|---|---|---|
| GET | `/api/lookups` | → `Lookups` (active people with `isExecutive`, teams with member ids, departments, visible projects, categories, escalation policies, tags, templates) |
| GET | `/api/search?q=` | → `SearchResult` (at least 2 characters; up to 10 hits per kind). Hit `kind` / `route`: `task` `/tasks/{id}`, `comment` `/tasks/{taskId}`, `email` and `teams` `/messages/{id}`, `commitment` `/tasks/{taskId}`, `project` `/projects/{id}`, `person` `/people/{id}`, `team` `/teams/{id}`, `department` `/tasks?departmentId={id}`, `tag` `/tasks?tag={name}`, `attachment` `/tasks/{taskId}` (download via `/api/attachments/{id}`). Only visible tasks and messages are searched. |
| GET/POST | `/api/views` | `SavedViewInput` → `SavedViewDto[]` (mine and shared) / `SavedViewDto` |
| PUT/DELETE | `/api/views/{id}` | `SavedViewInput` → `SavedViewDto` / 204 (owner only) |

## Tasks
| Method | Route | Body → Response |
|---|---|---|
| POST | `/api/tasks/query` | `TaskFilter` → `PagedResult<TaskListItem>` |
| POST | `/api/tasks` | `CreateTaskRequest` → `TaskDetail` |
| GET | `/api/tasks/{id}` | → `TaskDetail` (timeline newest first; `dependencies[].direction` is `DependsOn` — "Blocked by TASK-…" when `isBlocking` — or `Blocks`; subtask `depth` is relative, 1 = direct child, up to 6 levels) |
| PUT | `/api/tasks/{id}` | `UpdateTaskRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/subtasks` | `CreateTaskRequest` → `TaskDetail` (the new subtask) |
| POST | `/api/tasks/{id}/reassign` | `ReassignRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/assignees` | `AssigneeChange` → `TaskDetail` |
| DELETE | `/api/tasks/{id}/assignees/{assigneeId}` | → `TaskDetail` (`assigneeId` is the `AssigneeDto.id` row id) |
| POST/DELETE | `/api/tasks/{id}/followers/{employeeId}` | → 204 |
| POST | `/api/tasks/{id}/acknowledge` | `AcknowledgeRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/status` | `StatusChangeRequest` → `TaskDetail` (400 `use_complete` for completion statuses; 409 `invalid_transition`) |
| POST | `/api/tasks/{id}/progress` | `ProgressRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/comments` | `CommentRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/commitments` | `CommitmentInput` → `TaskDetail` |
| POST | `/api/tasks/{id}/complete` | `CompleteRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/approval` | `ApprovalRequest` (`action`: `Confirm`, `Reopen` or `RequestMoreWork`) → `TaskDetail` |
| POST | `/api/tasks/{id}/reopen` | `{ reason }` → `TaskDetail` |
| POST | `/api/tasks/{id}/extensions` | `ExtensionRequestInput` → `TaskDetail` |
| POST | `/api/extensions/{id}/decision` | `ExtensionDecisionRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/snooze` | `SnoozeRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/resume-followup` | → `TaskDetail` |
| POST | `/api/tasks/{id}/dependencies` | `DependencyInput` → `TaskDetail` |
| DELETE | `/api/tasks/{id}/dependencies/{depId}` | → `TaskDetail` |
| POST | `/api/tasks/{id}/dependencies/{depId}/resolve` | → `TaskDetail` |
| POST | `/api/tasks/{id}/remind` | `ReminderRequest` → `{ queued }` (covers the Send Reminder, Request Update, Send Email and Send Teams Message quick actions) |
| POST | `/api/tasks/{id}/escalate` | `EscalateRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/links` | `LinkMessageRequest` → `TaskDetail` (manually link an email or Teams message) |
| GET | `/api/tasks/{id}/audit` | → `AuditEntryDto[]` |
| POST | `/api/suggestions/{id}/accept` and `/dismiss` | → `TaskDetail` |
| POST | `/api/attachments` | multipart `file`, optional `taskId` and `isEvidence` → `AttachmentDto`. Max 25 MB; executable and script types are refused (400 `file_type_blocked`). The task must be visible. |
| GET | `/api/attachments/{id}` | → file download (`Content-Disposition: attachment`; visibility checked through the task; unattached files only for the uploader, executives and administrators) |
| GET | `/api/workload?employeeIds=a,b&teamId=` | → `WorkloadDto[]` (`load`: `Light`, `Moderate`, `Heavy` or `Overloaded`; `teamId` adds the leader, deputy and members) |
| POST | `/api/tasks/parse` | `ParseCommandRequest` → `ResolvedDraft`. Natural-language and voice delegation. The UI shows the draft and the user confirms. Then the UI posts `/api/tasks` or `/api/recurring`. 400 `parse_failed` when the text cannot be understood. |

## Executive views
| Method | Route | Body → Response |
|---|---|---|
| GET | `/api/workbench` | → `CeoWorkbench` (tiles from §38 and sections from §79; data scoped to what the caller may see) |
| GET | `/api/dashboard/me` | → `MyDashboard` |
| GET | `/api/dashboard/teams` | → `NamedRef[]` (teams the user leads or deputises, or in departments they head; all teams for executives) |
| GET | `/api/dashboard/team/{teamId}` | → `TeamDashboard` (403 for people who neither lead the team nor head its department) |
| GET | `/api/attention?status=Open` | → `AttentionItemDto[]` (`status`: `Open` (includes snoozed items whose time has come), `Snoozed`, `Resolved`, `Dismissed` or `All`). Executives see every item; others see items addressed to them or about tasks they delegated. |
| POST | `/api/attention/{id}/decision` | `AttentionDecision` (`action`: `resolve`, `dismiss`, `snooze` or `reopen`; `snoozeUntil` defaults to +1 day) → `AttentionItemDto` |
| GET | `/api/summary` | → `ExecutiveSummary` |
| GET | `/api/digest/preview` | → `DigestDto` (managers: TeamLeader, SeniorManager or Executive) |
| POST | `/api/digest/send` | → `{ queued }` (Executive or Administrator; idempotent per local day — a second send queues 0) |
| GET | `/api/review/weekly?weekStart=` | → `WeeklyReviewDto` (managers; default week: the previous week on Mondays, otherwise the current week) |
| POST | `/api/review/weekly/send?weekStart=` | → `{ queued }` (Executive or Administrator; idempotent per week) |
| POST | `/api/ask` | `AskRequest` → `AskAnswer` |
| GET | `/api/commitments?status=&toMe=&employeeId=&from=&to=` | → `CommitmentDto[]` |
| GET | `/api/reports?from=&to=&departmentId=&teamId=` | → `ReportDto` (default: the last 30 days; at most two years) |
| GET | `/api/inbox` | → `InboxItem[]` (in-app notifications, latest 50) |
| GET | `/api/inbox/unread-count` | → `{ count }` |
| POST | `/api/inbox/{id}/read` and `/api/inbox/read-all` | → 204 |

## Recurring tasks
| Method | Route | Body → Response |
|---|---|---|
| GET | `/api/recurring?includeEnded=false` | → `RecurringDefinitionDto[]` |
| GET | `/api/recurring/{id}` | → `RecurringDefinitionDto` |
| POST | `/api/recurring` | `RecurringDefinitionInput` → `RecurringDefinitionDto` |
| PUT | `/api/recurring/{id}` | `RecurringUpdateRequest` (scope `ThisInstance`, `ThisAndFuture` or `EntireSeries`) → `RecurringDefinitionDto` (for `ThisAndFuture`, the new version) |
| POST | `/api/recurring/{id}/pause` | `RecurringPauseRequest` → `RecurringDefinitionDto` |
| POST | `/api/recurring/{id}/resume` | → `RecurringDefinitionDto` |
| POST | `/api/recurring/{id}/end` | `{ reason }` → `RecurringDefinitionDto` |
| POST | `/api/recurring/{id}/skip` | `SkipOccurrenceRequest` → `RecurringInstanceDto` |
| POST | `/api/recurring/preview` | `PreviewRequest` → `OccurrencePreview[]` |
| GET | `/api/recurring/instances?definitionId=&outcome=&from=&to=&missedOnly=` | → `RecurringInstanceDto[]` (`outcome` is a comma list of `OccurrenceOutcome`) |

## Communications (email and Teams)
| Method | Route | Body → Response |
|---|---|---|
| GET | `/api/messages?text=&channel=&status=&unlinked=&from=&to=&page=&pageSize=` | → `PagedResult<MessageDto>` |
| GET | `/api/messages/{id}` | → `MessageDto` |
| GET | `/api/messages/review` | → `MessageDto[]` (the low-confidence review queue) |
| POST | `/api/messages/links/{linkId}/confirm` and `/reject` | → 204 |
| POST | `/api/messages/{id}/reprocess` | → 204 |

## Projects, meetings, templates
| Method | Route | Body → Response |
|---|---|---|
| GET | `/api/projects` | → `ProjectSummary[]` (`status`: `Not Started`, `On Track`, `At Risk`, `Off Track` or `Completed`, derived from the visible tasks) |
| GET | `/api/projects/{id}` | → `ProjectDetail` |
| POST/PUT | `/api/projects`, `/api/projects/{id}` | `ProjectInput` → `ProjectSummary` (create: managers; change: owner, department head or executive) |
| GET | `/api/meetings` and `/api/meetings/{id}` | → `MeetingDto[]` / `MeetingDto` |
| POST/PUT | `/api/meetings`, `/api/meetings/{id}` | `MeetingInput` → `MeetingDto` (action items: send `id` to update an existing item; items left out are cancelled unless they already have a task) |
| POST | `/api/meetings/{id}/create-tasks` | `CreateActionTasksRequest` → `MeetingDto` (open items with an owner become tasks; follow-up every 2 days unless given) |
| GET | `/api/templates` and `/api/templates/{id}` | → `TemplateDto[]` / `TemplateDto` |
| POST/PUT | `/api/templates`, `/api/templates/{id}` | `TemplateInput` → `TemplateDto` (managers) |
| POST | `/api/templates/{id}/instantiate` | `InstantiateTemplateRequest` → `TaskDetail` (the parent task; items become subtasks due on working days from the anchor date; roles not mapped fall back to the parent's owner, which is the `Owner` role, else the first mapped role) |

## Administration (role Administrator; monitor, jobs and errors also for Executive)
| Method | Route | Body → Response |
|---|---|---|
| GET/POST/PUT | `/api/admin/departments[/{id}]` | `DepartmentInput` → `DepartmentDto` |
| GET/POST/PUT | `/api/admin/teams[/{id}]` | `TeamInput` → `TeamDto` (leader and deputy are kept as member rows) |
| GET/POST/PUT | `/api/admin/employees[/{id}]` | `EmployeeInput` → `EmployeeDto` |
| PUT | `/api/admin/employees/{id}/account` | `AccountInput` → `EmployeeDto` (password ≥ 8 characters; you cannot disable yourself or drop your own Administrator role) |
| GET/POST/PUT | `/api/admin/calendars[/{id}]` | `CalendarInput` → `CalendarDto` |
| POST/DELETE | `/api/admin/calendars/{id}/holidays[/{holidayId}]` | `HolidayInput` → `CalendarDto` |
| GET/POST/PUT | `/api/admin/escalation-policies[/{id}]` | `EscalationPolicyInput` → `EscalationPolicyDto` |
| GET/PUT | `/api/admin/settings` | `RuntimeSettings` (stored in AppSettings `runtime`, merged over configuration, applied live; the Worker picks changes up within a minute) |
| GET | `/api/admin/monitor` | → `MonitorOverview` (spec §67; the Microsoft 365 probe times out after 5 s) |
| GET | `/api/admin/jobs` | → `JobDto[]` |
| GET | `/api/admin/jobs/{key}/executions` | → `JobExecutionDto[]` |
| POST | `/api/admin/jobs/{key}/run` | → 204 (409 `job_disabled` for a disabled job) |
| PUT | `/api/admin/jobs/{key}` | `{ enabled }` → `JobDto` |
| POST | `/api/admin/checkpoints/{id}/reset` | `{ fromScratch }` → 204 |
| PUT | `/api/admin/checkpoints/{id}` | `{ enabled }` → 204 |
| POST | `/api/admin/sync/run-now` | → 204 |
| GET | `/api/admin/notifications?status=` | → `NotificationRow[]` |
| POST | `/api/admin/notifications/retry` | `{ ids }` → `{ retried }` |
| GET | `/api/admin/errors` | → `IntegrationErrorDto[]` |
| GET | `/api/admin/messages/failed` | → `MessageDto[]` (reprocess with `/api/messages/{id}/reprocess`) |
| GET | `/api/admin/audit?taskId=&entityType=&action=&from=&to=&page=&pageSize=` | → `AuditEntryDto[]` |
| GET | `/api/admin/audit/verify/{taskId}` | → `{ ok, brokenEntryId? }` |

## Microsoft 365 simulator (Emulator mode only; Executive or Administrator)
These routes are mounted under `/api/m365-sim`. They operate the in-process Graph emulator tenant, and the real Graph client code talks to that emulator over HTTP:

- `GET users`
- `GET mailboxes/{address}/messages?folder=inbox|sentitems`
- `GET chats`
- `GET chats/{id}/messages`
- `GET channels`
- `GET teams/{t}/channels/{c}/messages`
- `GET activity`
- `GET/PUT faults`
- `POST mail` (`{ from, to[], cc[], subject, body, inReplyToMessageId? }`)
- `POST chats/{id}/messages` (`{ fromUserId, text }`)
- `POST chats/one-on-one` (`{ userA, userB }`)
- `POST teams/{t}/channels/{c}/messages` (`{ fromUserId, text, replyToId? }`)
- `POST delta/invalidate`

## System
| Method | Route | Body → Response |
|---|---|---|
| GET | `/api/health` | → `{ status, database, mode, jobsEnabled, serverTime }` (anonymous; 503 with `status: "degraded"` when the database is unreachable) |
| GET | `/api/system/info` | → `{ organisation, timeZone, mode, aiProvider, hosts[], version }` (`hosts` are `HostHeartbeat` rows from the last 24 h of job executions plus this process) |

Any other path that is not `/api`, `/hubs` or `/graph-emulator` returns the Angular `index.html` (client-side routing).
