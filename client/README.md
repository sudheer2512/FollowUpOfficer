# FollowUp Officer — web client

Angular 21.2 single-page app for **FollowUp Officer**, the CEO delegated-tasks, follow-up and executive-monitoring
application. Philosophy: *the CEO should manage exceptions, not chase people for updates.*

The HTTP contract lives in [`../docs/API.md`](../docs/API.md) and the C# records in `../src/FollowUp.Core/Contracts/*.cs`.
Their TypeScript mirror is `src/app/core/models/`.

## Run

```bash
cd client
npm install
npm start            # ng serve on http://localhost:4860, proxying /api and /hubs (WebSocket) to http://localhost:5860
npm run build        # production build straight into ../src/FollowUp.Api/wwwroot (served by the API)
npm run check        # fast type-check incl. templates: ngc -p tsconfig.app.json --noEmit
npm test             # unit tests (Vitest + jsdom, headless): ng test --watch=false
```

Start the API (`dotnet run --project ../src/FollowUp.Api`, port 5860) first. In demo mode the login page shows persona
cards; the demo password is `FollowUp@2026`.

## Stack

- Standalone components, **signals**, zoneless change detection (`provideZonelessChangeDetection`), new control flow
  (`@if` / `@for` / `@switch` / `@defer`), `inject()`, typed reactive forms, strict TypeScript + strict templates.
- `HttpClient` with `withFetch()` and one functional interceptor.
- `@microsoft/signalr` for real time (lazy-imported after sign-in).
- `echarts` 6 via a tiny `<fu-chart [options]>` wrapper (tree-shaken core, lazy-imported only where a chart renders).
- No UI library: a small design system in plain CSS (`src/styles.css`) with light/dark tokens.

## Structure

```
src/app/
  app.config.ts          providers: zoneless, router (component input binding), http + interceptor, session restore
  app.routes.ts          route map — every feature page is lazy-loaded
  core/
    models/              TS mirror of the API contract (enums as string unions, DateOnly = "yyyy-MM-dd")
    api/                 one injectable client per area: TasksApi, ExecutiveApi, RecurringApi, MessagesApi,
                         PlanningApi (projects/meetings/templates), AdminApi, LookupsApi (lookups/search/views)
    auth/                SessionState (signals), AuthService (cookie login/logout/restore), route guards
    http/                apiInterceptor + problem+json helpers (SILENT_ERRORS / SKIP_AUTH_REDIRECT context tokens)
    realtime/            RealtimeService (SignalR /hubs/events) + refreshOn() helper
    state/               LookupsStore, InboxStore, ThemeService, DelegateLauncher, SystemStore
    util/                dates, humanize, tone, flags, loader(), TaskFilter <-> query string, schedule descriptions
  shared/
    ui/                  icon, modal, drawer, tabs, toast, dialog service (confirm/prompt), empty state, skeleton,
                         chart, tile, paginator, dropdown
    forms/               person picker, team picker, lookup select, date input, tag input, follow-up schedule picker
    domain/              indicators (health dot, criticality/priority chips, status badge, score bar, deadline text,
                         avatar, person), task table, compact task list
    pipes/               humanize, dateOnly, dateTime, ago, confidence, fileSize, money
  layout/                shell (nav, top bar, search, shortcuts), inbox drawer, role-aware nav
  features/<area>/       routed pages (*.page.ts) and their private components
```

## Routes

| Route | Page | Access |
|---|---|---|
| `/login` | Sign in (demo personas, Microsoft button when Entra is enabled) | guests |
| `/` | CEO Workbench — summary, 13 KPI tiles, §79 sections | executives (others → `/me`) |
| `/attention` | CEO Attention Required — ranked items with inline actions | executives |
| `/me` | My dashboard | everyone |
| `/team`, `/team/:id` | Team dashboard (member load table + chart) | team leaders / managers / executives |
| `/tasks` | Task list — filters synced to the URL, saved views | everyone |
| `/tasks/:id` | Task detail — quick actions, Overview / Subtasks / Timeline / Communications / Completion / Audit | everyone (server enforces visibility) |
| `/commitments` | Commitments (Due soon / Open / Missed / Met / All, "made to me") | everyone |
| `/recurring`, `/recurring/new`, `/recurring/:id`, `/recurring/:id/edit` | Recurring definitions, rule builder with live preview, instance history | everyone |
| `/projects`, `/projects/:id` | Projects with derived status | everyone |
| `/meetings`, `/meetings/new`, `/meetings/:id` | Meetings, action items → tasks | everyone |
| `/communications` | Messages, review queue, failed (`?tab=`, `?message=` opens the drawer) | everyone |
| `/ask` | Conversational Q&A | everyone |
| `/review` | Weekly executive review + daily digest preview | executives |
| `/reports` | Metrics, charts and performance tables | executives |
| `/templates`, `/templates/new`, `/templates/:id` | Task templates + instantiate | everyone |
| `/admin/organisation`, `/admin/calendars`, `/admin/escalation`, `/admin/settings` | Administration | administrators |
| `/admin/monitor`, `/admin/audit` | Integration monitor (§67), audit log | executives + administrators |
| `/simulator` | Microsoft 365 simulator (shown in the nav only in Emulator mode) | executives + administrators |
| `/search?q=` | Global search results grouped by kind | everyone |

The Delegate dialog is global (top bar, shortcut `N`, microphone button, "Create subtask") and is lazy-loaded with
`@defer`. The inbox drawer opens from the bell.

## Conventions

- **Wire formats.** Enums are strings; `[Flags]` enums are comma lists (`"TeamLeader, Ceo"`) — use
  `core/util/flags.ts`. `DateOnly` stays a `"yyyy-MM-dd"` string end to end (never parse it through `new Date(str)`);
  `TimeOnly` is `"HH:mm:ss"`; `DateTime` is ISO UTC.
- **Headers & auth.** The interceptor adds `X-Requested-With: FollowUp` and `withCredentials` to every `/api` call.
  A 401 clears the session and redirects to `/login?returnUrl=…`. Errors become a friendly toast built from the
  problem+json `detail`/`title`; pass `context: new HttpContext().set(SILENT_ERRORS, true)` to handle errors yourself.
- **Async state.** Use `loader<T>()` (`core/util/loader.ts`) for page data: `value()`, `loading()`, `error()`,
  `load(obs, { silent })`. Show `<fu-skeleton>` while loading and `<fu-empty>` when there is nothing.
- **Real time.** `refreshOn(['task.changed', 'attention.changed'], () => this.reload(true))` in a constructor re-fetches
  (debounced, silently) when relevant hub topics arrive. The shell refreshes the inbox badge and shows a subtle toast
  on `attention.changed`.
- **Drill-downs.** Server `Drilldown {route, query}` objects map to `[routerLink]="d.route" [queryParams]="d.query"`.
  The task list reads exactly the `TaskFilter` property names from the query string (`core/util/task-filter-query.ts`),
  so tiles, summary points and report buckets land on a filtered list.
- **Dialogs.** Templated forms use `<fu-modal>` rendered under `@if`; quick confirmations use
  `DialogService.confirm()` / `.prompt()`. Side panels use `<fu-drawer>`.
- **Accessibility.** Rows are links or `tabindex="0"` with Enter handlers, icon-only buttons have `aria-label`,
  focus is visible (`--focus` ring), modals trap focus and restore it on close.
- **Shortcuts.** `N` opens the Delegate dialog, `Ctrl/Cmd+K` focuses global search, `Esc` closes menus and dialogs.
- **Styling.** Prefer the global design-system classes (`.card`, `.btn`, `.chip.tone-*`, `.badge`, `.table`, `.kpi-grid`,
  `.field`, `.form-grid`, `.list-row`, `.tabs`, `.alert`, …) and CSS tokens (`var(--accent)`, `var(--danger)`…). Keep
  component styles small. Colour carries meaning only: health, criticality, deadlines.

## Tests

Pure helpers have unit tests (`*.spec.ts` next to them): TaskFilter ↔ query string mapping, follow-up / recurrence /
due-rule descriptions, flags, dates and tones (`core/util`), plus feature helpers — attention action inference,
task action permissions, timeline kinds, filter chips, delegate/recurring form mapping, template tree validation,
report periods and formats, search highlighting and Ask history. They run headless with Vitest + jsdom (`npm test`).
