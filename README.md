# FollowUp Officer

**A CEO delegated-tasks, follow-up and executive monitoring application.** It works as a digital executive follow-up officer:

- It captures delegated work.
- It follows up on the agreed schedule, by email and Microsoft Teams.
- It reads the related emails and Teams conversations, and detects commitments, delays, blockers and silence.
- It escalates when needed and verifies completion.
- It brings matters to the CEO only when intervention is required.

It runs as always-on Windows services. It resumes exactly where it stopped after a shutdown, a crash or a Microsoft 365 outage.

| | |
|---|---|
| Backend | .NET 10, ASP.NET Core minimal APIs, EF Core 10 / SQL Server, SignalR |
| Background | Durable DB-backed job engine (leases, checkpoints, catch-up, retries, dead letters) running in a Windows Service |
| Microsoft 365 | Microsoft Graph REST client (Outlook, Teams chats and channels, calendar, directory) + a Graph-compatible emulator tenant for demos |
| AI | Offline heuristic engine; optional Anthropic Claude or an OpenAI-compatible provider with automatic fallback |
| UI | Angular 21 (standalone, signals, zoneless), responsive |

## Run it (demo)

Prerequisites: .NET 10 SDK, Node 24, and SQL Server LocalDB (`(localdb)\MSSQLLocalDB`).

Build the UI. It is served by the API from `src/FollowUp.Api/wwwroot`:

```bash
npm --prefix client ci
```

```bash
npm --prefix client run build
```

Start the API. It also runs the background processor in-process:

```bash
dotnet run --project src/FollowUp.Api --no-launch-profile -- --environment Development
```

Open **http://localhost:5860**.

On the first start the API creates the `FollowUpOfficer` database. It then seeds a demo organisation (Wings Software, 27 people, 11 teams) and about four weeks of delegation history. That history is replayed through the real engine, which takes about 25 s. Within a minute of start-up the background processor ingests the demo emails and Teams messages from the emulated Microsoft 365 tenant. It correlates and classifies them, then updates statuses, commitments, extension requests and the CEO attention queue.

**Demo sign-in.** The password is `FollowUp@2026` for every user.

| User | Role |
|---|---|
| `anand` | CEO (Executive): workbench, attention queue, approvals |
| `vikram` | CTO (Senior Manager) |
| `deepak` | Development Manager (Team Leader) |
| `rahul` | Senior Developer / CRM & Mobile lead |
| `priya` | Finance Manager |
| `sanjay`, `aisha`, `imran`, `kavya`, `neha` … | Team members |
| `ravi` | IT Administrator (Administration, integration monitor) |

For UI development, run the Angular dev server: `npm --prefix client start`. It serves http://localhost:4860 and proxies `/api` and `/hubs` to port 5860.

To reset the demo, stop the API, drop the database, and start the API again:

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "ALTER DATABASE FollowUpOfficer SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE FollowUpOfficer"
```

### Things to try

- **Workbench** (as `anand`): the exception-oriented executive summary. For example: "Development is waiting for an external party on 1 task (API credentials…)".
- **Attention**: overdue critical work, missed commitments, declined delegations, extension requests, a customer escalation email, and CEO escalations. Each has inline decisions.
- **TASK-2026-00142 "UAE eInvoice Design"**: the delegation chain CEO → CTO → Development Manager. It shows a missed commitment and an external blocker detected from a Teams channel post ("waiting for API credentials… finish by Friday"). It also shows reminders sent and skipped, and the escalation ladder.
- **Delegate** (top bar or `N`): type or say *"Ask Rahul and the development team to finish the dealer CRM screens by Friday. Follow up every two days and tell me if it gets delayed."*
- **Ask**: *"Show Finance tasks overdue by more than five days."*, *"Which commitments made to me have been missed?"*, *"What is blocking the eInvoice project?"*
- **Recurring**: the daily support review shows a simulated Friday-to-Monday server outage. The missed occurrences were recorded with reasons, and the latest one was generated late (policy *latest only*).
- **Microsoft 365 Simulator**: send an email or Teams message as any employee, e.g. `TASK-2026-00142 will be completed by Thursday`. You can also inject faults (outage, throttling, auth failure, delta-token expiry) and watch the integration monitor recover.
- **Admin → Integration monitor**: sync checkpoints, job schedules, catch-up runs after a restart, the notification queue and integration errors.

## Deploy

- **Windows (auto-start, restart on failure).** Run `deploy/install-windows-services.ps1` from an elevated PowerShell. It installs `FollowUpOfficer.Worker` (the background processor) and `FollowUpOfficer.Api` as Automatic (Delayed Start) services with recovery actions.
- **IIS.** Run `deploy/install-iis.ps1` from an elevated PowerShell. It hosts the UI and API in IIS (in-process, at a site root or as an application under a path such as `http://localhost/followupofficer`) and installs the Worker as a Windows service. See [docs/DEPLOY-IIS.md](docs/DEPLOY-IIS.md).
- **Containers.** Run `docker compose -f deploy/docker-compose.yml up -d --build`. It includes SQL Server, with `restart: unless-stopped` everywhere.
- **Real Microsoft 365.** Set `Microsoft365:Mode=Graph` and the tenant, client id and secret, then grant the permissions listed in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#microsoft-graph-spec-71).
- **LLM.** Set `Ai:Provider=Anthropic` (or `OpenAICompatible`) and `Ai:ApiKey`. Without them the heuristic engine is used. Any LLM failure falls back to it.
- **Entra ID SSO.** Set `Entra:TenantId`, `Entra:ClientId`, `Entra:Audience` and `Entra:ApiScope`.

## Test

```bash
dotnet test tests/FollowUp.Tests
```

The suite covers the domain rules (recurrence, calendars, follow-up decisions, escalation, scoring, visibility). It covers the core services and reliability on SQLite: exactly-once follow-ups, downtime coalescing, catch-up policies, checkpoint resume, retry and dead letters, crash reconciliation, and audit immutability. It also covers the Graph client and emulator, the AI layer, and the HTTP API.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): design, the reliability model, security, and the spec coverage map (§1–§80).
- [docs/API.md](docs/API.md): the HTTP contract.
- [client/README.md](client/README.md): the Angular client.
