# FollowUp Officer — HTTP API contract

Base URL: `http://localhost:5860` (the API also serves the Angular build). Angular dev server: `http://localhost:4860` with a proxy for `/api` and `/hubs`.

**Conventions**

- JSON is camelCase. Enums are **strings** (e.g. `"InProgress"`, `"Critical"`). Flags enums are comma lists (`"Email, Teams"`).
- `DateOnly` is `"yyyy-MM-dd"`. `TimeOnly` is `"HH:mm:ss"`. `DateTime` is ISO-8601 UTC with `Z`.
- DTO shapes are the C# records in `src/FollowUp.Core/Contracts/*.cs`. The property names map 1:1 to camelCase.
- Errors use RFC 7807 problem+json `{ status, title, detail, code }`. 400 means validation (`code` is stable), 401 means not signed in, 403 means not allowed, 404 means not found or not visible, and 409 means a conflict or an invalid transition.
- Auth: an HttpOnly cookie `fu.auth` is set by `POST /api/auth/login`. An Entra ID bearer token in `Authorization: Bearer …` is also accepted when configured. Mutating requests must send the header `X-Requested-With: FollowUp` (a CSRF guard).
- Real time: SignalR hub `/hubs/events`. The server sends `event` with `{ topic, taskId?, payload? }`. Topics are `task.changed`, `task.created`, `attention.changed`, `communication.imported`, `notification.queued`, `inbox.changed`, `sync.status` and `job.status`.

## Auth
| Method | Route | Body → Response |
|---|---|---|
| GET | `/api/auth/config` | → `{ entraEnabled, tenantId?, clientId?, apiScope?, demoMode, demoUsers: [{userName, name, title, role}] }` |
| POST | `/api/auth/login` | `{ userName, password }` → `MeDto` |
| POST | `/api/auth/logout` | → 204 |
| GET | `/api/auth/me` | → `MeDto` |

## Lookups, search, views
| GET | `/api/lookups` | → `Lookups` |
|---|---|---|
| GET | `/api/search?q=` | → `SearchResult`. Covers task number, title, description, people, teams, departments, comments, emails, Teams messages, commitments, projects, tags and attachments. |
| GET/POST | `/api/views` | `SavedViewInput` → `SavedViewDto[]` / `SavedViewDto` |
| PUT/DELETE | `/api/views/{id}` | |

## Tasks
| Method | Route | Body → Response |
|---|---|---|
| POST | `/api/tasks/query` | `TaskFilter` → `PagedResult<TaskListItem>` |
| POST | `/api/tasks` | `CreateTaskRequest` → `TaskDetail` |
| GET | `/api/tasks/{id}` | → `TaskDetail` |
| PUT | `/api/tasks/{id}` | `UpdateTaskRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/subtasks` | `CreateTaskRequest` → `TaskDetail` (the new subtask) |
| POST | `/api/tasks/{id}/reassign` | `ReassignRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/assignees` | `AssigneeChange` → `TaskDetail` |
| DELETE | `/api/tasks/{id}/assignees/{assigneeId}` | → `TaskDetail` |
| POST/DELETE | `/api/tasks/{id}/followers/{employeeId}` | → 204 |
| POST | `/api/tasks/{id}/acknowledge` | `AcknowledgeRequest` → `TaskDetail` |
| POST | `/api/tasks/{id}/status` | `StatusChangeRequest` → `TaskDetail` |
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
| POST | `/api/attachments` | multipart `file`, optional `taskId` and `isEvidence` → `AttachmentDto` |
| GET | `/api/attachments/{id}` | → file download |
| GET | `/api/workload?employeeIds=a,b&teamId=` | → `WorkloadDto[]` |
| POST | `/api/tasks/parse` | `ParseCommandRequest` → `ResolvedDraft`. Natural-language and voice delegation. The UI shows the draft and the user confirms. Then the UI posts `/api/tasks` or `/api/recurring`. |

## Executive views
| GET | `/api/workbench` | → `CeoWorkbench` (tiles from §38 and sections from §79) |
|---|---|---|
| GET | `/api/dashboard/me` | → `MyDashboard` |
| GET | `/api/dashboard/teams` | → `NamedRef[]` (teams the user leads or manages; all teams for executives) |
| GET | `/api/dashboard/team/{teamId}` | → `TeamDashboard` |
| GET | `/api/attention?status=Open` | → `AttentionItemDto[]` (`status`: `Open`, `Snoozed`, `Resolved`, `Dismissed` or `All`) |
| POST | `/api/attention/{id}/decision` | `AttentionDecision` (`action`: `resolve`, `dismiss`, `snooze` or `reopen`) → `AttentionItemDto` |
| GET | `/api/summary` | → `ExecutiveSummary` |
| GET | `/api/digest/preview` | → `DigestDto` |
| POST | `/api/digest/send` | → `{ queued }` |
| GET | `/api/review/weekly?weekStart=` | → `WeeklyReviewDto` |
| POST | `/api/review/weekly/send` | → `{ queued }` |
| POST | `/api/ask` | `AskRequest` → `AskAnswer` |
| GET | `/api/commitments?status=&toMe=&employeeId=&from=&to=` | → `CommitmentDto[]` |
| GET | `/api/reports?from=&to=&departmentId=&teamId=` | → `ReportDto` |
| GET | `/api/inbox` | → `InboxItem[]` (in-app notifications) |
| GET | `/api/inbox/unread-count` | → `{ count }` |
| POST | `/api/inbox/{id}/read` and `/api/inbox/read-all` | → 204 |

## Recurring tasks
| GET | `/api/recurring?includeEnded=false` | → `RecurringDefinitionDto[]` |
|---|---|---|
| GET | `/api/recurring/{id}` | → `RecurringDefinitionDto` |
| POST | `/api/recurring` | `RecurringDefinitionInput` → `RecurringDefinitionDto` |
| PUT | `/api/recurring/{id}` | `RecurringUpdateRequest` (scope `ThisInstance`, `ThisAndFuture` or `EntireSeries`) → `RecurringDefinitionDto` |
| POST | `/api/recurring/{id}/pause` | `RecurringPauseRequest` → `RecurringDefinitionDto` |
| POST | `/api/recurring/{id}/resume` | → `RecurringDefinitionDto` |
| POST | `/api/recurring/{id}/end` | `{ reason }` → `RecurringDefinitionDto` |
| POST | `/api/recurring/{id}/skip` | `SkipOccurrenceRequest` → `RecurringInstanceDto` |
| POST | `/api/recurring/preview` | `PreviewRequest` → `OccurrencePreview[]` |
| GET | `/api/recurring/instances?definitionId=&outcome=&from=&to=&missedOnly=` | → `RecurringInstanceDto[]` |

## Communications (email and Teams)
| GET | `/api/messages?text=&channel=&status=&unlinked=&from=&to=&page=` | → `PagedResult<MessageDto>` |
|---|---|---|
| GET | `/api/messages/{id}` | → `MessageDto` |
| GET | `/api/messages/review` | → `MessageDto[]` (the low-confidence review queue) |
| POST | `/api/messages/links/{linkId}/confirm` and `/reject` | → 204 |
| POST | `/api/messages/{id}/reprocess` | → 204 |

## Projects, meetings, templates
| GET | `/api/projects` | → `ProjectSummary[]` |
|---|---|---|
| GET | `/api/projects/{id}` | → `ProjectDetail` |
| POST/PUT | `/api/projects`, `/api/projects/{id}` | `ProjectInput` → `ProjectSummary` |
| GET | `/api/meetings` and `/api/meetings/{id}` | → `MeetingDto[]` / `MeetingDto` |
| POST/PUT | `/api/meetings`, `/api/meetings/{id}` | `MeetingInput` → `MeetingDto` |
| POST | `/api/meetings/{id}/create-tasks` | `CreateActionTasksRequest` → `MeetingDto` |
| GET | `/api/templates` and `/api/templates/{id}` | → `TemplateDto[]` / `TemplateDto` |
| POST/PUT | `/api/templates`, `/api/templates/{id}` | `TemplateInput` → `TemplateDto` |
| POST | `/api/templates/{id}/instantiate` | `InstantiateTemplateRequest` → `TaskDetail` |

## Administration (role Administrator; monitor also for Executive)
| GET/POST/PUT | `/api/admin/departments[/{id}]` | `DepartmentInput` → `DepartmentDto` |
|---|---|---|
| GET/POST/PUT | `/api/admin/teams[/{id}]` | `TeamInput` → `TeamDto` |
| GET/POST/PUT | `/api/admin/employees[/{id}]` | `EmployeeInput` → `EmployeeDto` |
| PUT | `/api/admin/employees/{id}/account` | `AccountInput` → `EmployeeDto` |
| GET/POST/PUT | `/api/admin/calendars[/{id}]` | `CalendarInput` → `CalendarDto` |
| POST/DELETE | `/api/admin/calendars/{id}/holidays[/{holidayId}]` | `HolidayInput` → `CalendarDto` |
| GET/POST/PUT | `/api/admin/escalation-policies[/{id}]` | `EscalationPolicyInput` → `EscalationPolicyDto` |
| GET/PUT | `/api/admin/settings` | `RuntimeSettings` |
| GET | `/api/admin/monitor` | → `MonitorOverview` (spec §67) |
| GET | `/api/admin/jobs/{key}/executions` | → `JobExecutionDto[]` |
| POST | `/api/admin/jobs/{key}/run` | → 204 |
| PUT | `/api/admin/jobs/{key}` | `{ enabled }` → `JobDto` |
| POST | `/api/admin/checkpoints/{id}/reset` | `{ fromScratch }` → 204 |
| PUT | `/api/admin/checkpoints/{id}` | `{ enabled }` → 204 |
| POST | `/api/admin/sync/run-now` | → 204 |
| GET | `/api/admin/notifications?status=` | → `NotificationRow[]` |
| POST | `/api/admin/notifications/retry` | `{ ids }` → `{ retried }` |
| GET | `/api/admin/errors` | → `IntegrationErrorDto[]` |
| GET | `/api/admin/audit?taskId=&entityType=&action=&from=&to=&page=` | → `AuditEntryDto[]` |
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
| GET | `/api/health` | → `{ status, database, mode, jobsEnabled, serverTime }` (anonymous) |
|---|---|---|
| GET | `/api/system/info` | → `{ organisation, timeZone, mode, aiProvider, hosts[], version }` |
