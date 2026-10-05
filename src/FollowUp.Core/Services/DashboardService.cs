using System.Globalization;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

/// <summary>
/// The CEO "Attention Required" queue (spec §39): listing with the linked task, and the executive's decision
/// (resolve, dismiss, snooze, reopen). Executives see every item; other people see items addressed to them or
/// about tasks they delegated.
/// </summary>
public sealed class AttentionService(FollowUpDbContext db, ActorContext actor, IClock clock, TaskQueryService queries, AuditService audit)
{
    public IQueryable<AttentionItem> VisibleItems()
    {
        var v = actor.Viewer;
        var me = actor.EmployeeId;
        if (actor.IsSystem) return db.AttentionItems;
        if (v.IsExecutive) return db.AttentionItems.Where(a => a.AudienceId == null || a.AudienceId == me);
        var delegated = db.Tasks.VisibleTo(db, v).Where(t => t.DelegatorId == me || t.OriginalDelegatorId == me).Select(t => t.Id);
        return db.AttentionItems.Where(a => a.AudienceId == me || (a.TaskId != null && delegated.Contains(a.TaskId.Value)));
    }

    private IQueryable<AttentionItem> ByStatus(string? status)
    {
        var now = clock.UtcNow;
        var q = VisibleItems().AsNoTracking();
        return (status ?? "Open").Trim().ToLowerInvariant() switch
        {
            "all" => q,
            "snoozed" => q.Where(a => a.Status == AttentionStatus.Snoozed && (a.SnoozedUntil == null || a.SnoozedUntil > now)),
            "resolved" => q.Where(a => a.Status == AttentionStatus.Resolved),
            "dismissed" => q.Where(a => a.Status == AttentionStatus.Dismissed),
            _ => q.Where(a => a.Status == AttentionStatus.Open || (a.Status == AttentionStatus.Snoozed && a.SnoozedUntil != null && a.SnoozedUntil <= now)),
        };
    }

    public Task<int> CountOpenAsync(CancellationToken ct) => ByStatus("Open").CountAsync(ct);

    public async Task<List<AttentionItemDto>> ListAsync(string? status, CancellationToken ct, int take = 200, Criticality? minCriticality = null)
    {
        var q = ByStatus(status);
        if (minCriticality is { } min) q = q.Where(a => a.Task != null && a.Task.Criticality >= min);
        var rows = await q.OrderByDescending(a => a.Score).ThenByDescending(a => a.UpdatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return await ToDtosAsync(rows, ct);
    }

    public async Task<AttentionItemDto> DecideAsync(Guid id, AttentionDecision d, CancellationToken ct)
    {
        var item = await db.AttentionItems.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Attention item");
        if (!await VisibleItems().AnyAsync(a => a.Id == id, ct)) throw new NotFoundException("Attention item");
        var now = clock.UtcNow;
        var before = item.Status;
        switch (d.Action?.Trim().ToLowerInvariant())
        {
            case "resolve":
                item.Status = AttentionStatus.Resolved;
                item.ResolvedAt = now;
                item.ResolvedById = actor.EmployeeId;
                item.Resolution = string.IsNullOrWhiteSpace(d.Note) ? $"Resolved by {actor.Name}" : d.Note;
                item.SnoozedUntil = null;
                break;
            case "dismiss":
                item.Status = AttentionStatus.Dismissed;
                item.ResolvedAt = now;
                item.ResolvedById = actor.EmployeeId;
                item.Resolution = string.IsNullOrWhiteSpace(d.Note) ? $"Dismissed by {actor.Name}" : d.Note;
                item.SnoozedUntil = null;
                break;
            case "snooze":
                var until = d.SnoozeUntil is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : now.AddDays(1);
                if (until <= now) throw new ValidationException("snooze_until", "Snooze until a time in the future.");
                item.Status = AttentionStatus.Snoozed;
                item.SnoozedUntil = until;
                item.Resolution = d.Note;
                break;
            case "reopen":
                item.Status = AttentionStatus.Open;
                item.ResolvedAt = null;
                item.ResolvedById = null;
                item.SnoozedUntil = null;
                item.Resolution = d.Note;
                break;
            default:
                throw new ValidationException("invalid_action", "Action must be resolve, dismiss, snooze or reopen.");
        }
        item.UpdatedAt = now;
        audit.Record("AttentionDecision", nameof(AttentionItem), item.Id, item.TaskId, "Status", before.ToString(), item.Status.ToString(), d.Note);
        UiEvents.Publish(db, clock, UiEvents.AttentionChanged, item.TaskId);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([item], ct))[0];
    }

    public async Task<List<AttentionItemDto>> ToDtosAsync(List<AttentionItem> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var c = await queries.ClockAsync(ct);
        var taskIds = rows.Where(r => r.TaskId is not null).Select(r => r.TaskId!.Value).Distinct().ToList();
        var tasks = taskIds.Count == 0 ? [] : (await queries.ProjectAsync(db.Tasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).VisibleTo(db, actor.Viewer), c, ct))
            .ToDictionary(t => t.Id);
        return rows.Select(a => new AttentionItemDto(a.Id, a.Type, Text.Humanize(a.Type), a.Title, a.Detail, a.Score, a.Status, a.CreatedAt, a.UpdatedAt,
            a.SnoozedUntil, a.TaskId is { } t && tasks.TryGetValue(t, out var item) ? item : null, a.CommitmentId, a.ExtensionRequestId, a.ApprovalId,
            a.MessageId, a.SuggestionId, Actions(a))).ToList();
    }

    private static List<string> Actions(AttentionItem a)
    {
        if (a.Status is AttentionStatus.Resolved or AttentionStatus.Dismissed) return ["reopen"];
        var list = new List<string>();
        if (a.TaskId is not null) list.Add("open-task");
        switch (a.Type)
        {
            case AttentionType.ApprovalRequired: list.Add("approve"); break;
            case AttentionType.ExtensionRequest: list.Add("decide-extension"); break;
            case AttentionType.AiSuggestion: list.Add("review-suggestion"); break;
            case AttentionType.CriticalOverdue or AttentionType.RepeatedNonResponse or AttentionType.CommitmentMissed
                or AttentionType.AcknowledgementPending or AttentionType.Blocked:
                list.Add("remind");
                list.Add("escalate");
                break;
        }
        if (a.Status == AttentionStatus.Snoozed) list.Add("reopen");
        list.AddRange(["resolve", "dismiss"]);
        if (a.Status == AttentionStatus.Open) list.Add("snooze");
        return list;
    }
}

/// <summary>
/// Executive and management dashboards: the CEO workbench (spec §38 tiles, §79 sections), the individual
/// dashboard (§41), team dashboards (§40) and project status (§46). Every number has a drill-down whose query
/// parameters are <see cref="TaskFilter"/> field names (list values comma-separated).
/// </summary>
public sealed class DashboardService(
    FollowUpDbContext db,
    ActorContext actor,
    TaskQueryService queries,
    AttentionService attention,
    CommitmentService commitments,
    RecurringTaskService recurring,
    DigestService digest)
{
    private const int SectionSize = 10;

    // ═══════════════════════════════ CEO workbench ═══════════════════════════════

    public async Task<CeoWorkbench> CeoWorkbenchAsync(CancellationToken ct)
    {
        var c = await queries.ClockAsync(ct);
        var v = actor.Viewer;
        var vis = db.Tasks.AsNoTracking().VisibleTo(db, v);
        var execs = await Executives.IdsAsync(db, ct);
        var visibleIds = vis.Select(t => t.Id);
        var weekStartUtc = c.Utc(c.WeekStart);
        var missedFrom = c.Today.AddDays(-30);

        var open = await vis.ActiveOnly().CountAsync(ct);
        var critical = await vis.ActiveOnly().CriticalOnly().CountAsync(ct);
        var dueToday = await vis.DueToday(c).CountAsync(ct);
        var dueWeek = await vis.DueThisWeek(c).CountAsync(ct);
        var overdue = await vis.OverdueAt(c.Now).CountAsync(ct);
        var severe = await vis.SeverelyOverdue(c).CountAsync(ct);
        var noResponse = await vis.NoResponseOnly().CountAsync(ct);
        var blocked = await vis.BlockedOnly().CountAsync(ct);
        var awaitingCeo = await vis.WaitingFor(db, execs).CountAsync(ct);
        var awaitingApproval = await vis.AwaitingConfirmation().CountAsync(ct);
        var missed = await db.Commitments.CountAsync(x => visibleIds.Contains(x.TaskId) && x.Status == CommitmentStatus.Missed && x.CommitmentDate >= missedFrom, ct);
        var completedWeek = await vis.CompletedBetween(weekStartUtc, c.Now.AddSeconds(1)).CountAsync(ct);
        var recurringDue = await vis.Where(t => t.RecurringDefinitionId != null).InProgressWork().Where(t => t.DueDate != null && t.DueDate <= c.WeekEnd).CountAsync(ct);

        var tiles = new List<Tile>
        {
            new("open", "Open", open, "neutral", Drilldown.Tasks(("open", "true")), "Active tasks (not draft, deferred or closed)"),
            new("critical", "Critical", critical, Tone(critical, "danger"), Drilldown.Tasks(("open", "true"), ("minCriticality", "Critical"))),
            new("due-today", "Due Today", dueToday, Tone(dueToday, "warning"), Drilldown.Tasks(("bucket", "DueToday"))),
            new("due-this-week", "Due This Week", dueWeek, Tone(dueWeek, "info"), Drilldown.Tasks(("bucket", "DueThisWeek")), "Due from today to Sunday"),
            new("overdue", "Overdue", overdue, Tone(overdue, "danger"), Drilldown.Tasks(("overdue", "true"), ("sort", "due"))),
            new("severely-overdue", "Severely Overdue", severe, Tone(severe, "danger"), Drilldown.Tasks(("bucket", "SeverelyOverdue"), ("sort", "due")),
                $"Overdue by {c.SevereDays} days or more"),
            new("no-response", "No Response", noResponse, Tone(noResponse, "warning"), Drilldown.Tasks(("noResponse", "true")), "A reminder went unanswered"),
            new("blocked", "Blocked", blocked, Tone(blocked, "warning"), Drilldown.Tasks(("blocked", "true"))),
            new("awaiting-ceo", "Awaiting CEO", awaitingCeo, Tone(awaitingCeo, "info"), Drilldown.Tasks(("waitingForCeo", "true")),
                "Approvals, extension requests and acknowledgement responses waiting for an executive"),
            new("awaiting-approval", "Awaiting Approval", awaitingApproval, Tone(awaitingApproval, "info"), Drilldown.Tasks(("awaitingApproval", "true"))),
            new("missed-commitments", "Missed Commitments", missed, Tone(missed, "warning"),
                Drilldown.To("/commitments", ("status", "Missed"), ("from", missedFrom.ToString("yyyy-MM-dd"))), "Last 30 days"),
            new("completed-this-week", "Completed This Week", completedWeek, completedWeek > 0 ? "success" : "neutral",
                Drilldown.Tasks(("completedFrom", c.WeekStart.ToString("yyyy-MM-dd")), ("sort", "completed"))),
            new("recurring-due", "Recurring Tasks Due", recurringDue, Tone(recurringDue, "info"),
                Drilldown.Tasks(("recurring", "true"), ("open", "true"), ("dueTo", c.WeekEnd.ToString("yyyy-MM-dd")), ("sort", "due")), "Open recurring work due by Sunday"),
        };

        var sections = new List<WorkbenchSection>();

        // 1. Needs my attention
        var attentionItems = await attention.ListAsync("Open", ct, SectionSize);
        sections.Add(new WorkbenchSection("attention", "Needs My Attention", "Open attention items ranked by attention score",
            await attention.CountOpenAsync(ct), Drilldown.To("/attention", ("status", "Open")), [], Attention: attentionItems));

        // 2. Commitments due in the next 3 days + recently missed
        var soon = c.Today.AddDays(3);
        var missedSince = c.Now.AddDays(-3);
        var commitmentRows = await commitments.ProjectAsync(db.Commitments.AsNoTracking()
            .Where(x => visibleIds.Contains(x.TaskId)
                && ((x.Status == CommitmentStatus.Open && x.CommitmentDate >= c.Today && x.CommitmentDate <= soon)
                    || (x.Status == CommitmentStatus.Missed && x.ResolvedAt != null && x.ResolvedAt >= missedSince)))
            .OrderBy(x => x.CommitmentDate).Take(25), ct);
        sections.Add(new WorkbenchSection("commitments", "Commitments Due", "Promises due in the next 3 days and commitments missed recently",
            commitmentRows.Count, Drilldown.To("/commitments", ("from", c.Today.AddDays(-3).ToString("yyyy-MM-dd")), ("to", soon.ToString("yyyy-MM-dd"))), [],
            Commitments: commitmentRows));

        // 3. No response
        sections.Add(new WorkbenchSection("no-response", "No Response", "Reminders that went unanswered", noResponse,
            Drilldown.Tasks(("noResponse", "true")), await queries.ProjectAsync(vis.NoResponseOnly().OrderBy(t => t.NoResponseSince).Take(SectionSize), c, ct)));

        // 4. Critical / at risk
        var risky = vis.OpenOnly().Where(t => t.Status != WorkStatus.Draft && (t.Criticality >= Criticality.Critical || t.Health >= TaskHealth.AtRisk));
        sections.Add(new WorkbenchSection("critical", "Critical / At Risk", "Critical work and tasks whose health is at risk", await risky.CountAsync(ct),
            Drilldown.Tasks(("open", "true"), ("health", "AtRisk,Critical")),
            await queries.ProjectAsync(TaskQueryService.Sort(risky, "attention").Take(SectionSize), c, ct)));

        // 5. Waiting for my approval
        var approvals = await ApprovalQueueAsync(c, ct);
        sections.Add(new WorkbenchSection("approvals", "Waiting for My Approval", "Completion confirmations, extension requests and AI completion suggestions",
            approvals.Count, Drilldown.Tasks(("waitingForCeo", "true")), [], Approvals: approvals));

        // 6. Overdue by severity
        var overdueRows = await queries.ProjectAsync(vis.OverdueAt(c.Now).OrderBy(t => t.DueDate).Take(300), c, ct);
        sections.Add(new WorkbenchSection("overdue", "Overdue", "Sorted by severity (days overdue × criticality)", overdue,
            Drilldown.Tasks(("overdue", "true"), ("sort", "due")),
            overdueRows.OrderByDescending(t => TaskMetrics.Severity(t.DaysOverdue, t.Criticality)).ThenByDescending(t => t.AttentionScore).Take(SectionSize).ToList()));

        // 7. Upcoming important deadlines (7 days)
        var weekAhead = c.Today.AddDays(7);
        var upcoming = vis.DueBetween(c, c.Today, weekAhead).Where(t => t.Criticality >= Criticality.Important);
        sections.Add(new WorkbenchSection("upcoming", "Upcoming Important Deadlines", "Important and critical work due in the next 7 days",
            await upcoming.CountAsync(ct),
            Drilldown.Tasks(("minCriticality", "Important"), ("dueFrom", c.Today.ToString("yyyy-MM-dd")), ("dueTo", weekAhead.ToString("yyyy-MM-dd")), ("overdue", "false"), ("open", "true"), ("sort", "due")),
            await queries.ProjectAsync(upcoming.OrderBy(t => t.DueDate).ThenByDescending(t => t.Criticality).Take(SectionSize), c, ct)));

        // 8. Recently completed (7 days, important first)
        var completed = vis.CompletedBetween(c.Now.AddDays(-7), c.Now.AddSeconds(1));
        sections.Add(new WorkbenchSection("completed", "Recently Completed", "Completed in the last 7 days, important work first", await completed.CountAsync(ct),
            Drilldown.Tasks(("completedFrom", c.Today.AddDays(-7).ToString("yyyy-MM-dd")), ("sort", "completed")),
            await queries.ProjectAsync(completed.OrderByDescending(t => t.Criticality).ThenByDescending(t => t.CompletedAt).Take(SectionSize), c, ct)));

        var summary = await digest.ExecutiveSummaryAsync(ct);
        return new CeoWorkbench(c.Now, await GreetingAsync(c, ct), tiles, sections, summary);
    }

    /// <summary>Approvals, extension requests and AI completion suggestions waiting for the current user.</summary>
    public async Task<List<ApprovalQueueItem>> ApprovalQueueAsync(QueryClock c, CancellationToken ct)
    {
        if (actor.EmployeeId is not { } me) return [];
        var vis = db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer);
        var visibleIds = vis.Select(t => t.Id);
        var approvals = await db.TaskApprovals.AsNoTracking()
            .Where(a => a.ApproverId == me && a.Decision == ApprovalDecision.Pending && visibleIds.Contains(a.TaskId)
                && db.Tasks.Any(t => t.Id == a.TaskId && t.CompletionRound == a.Round && t.Status == WorkStatus.CompletionAwaitingApproval)
                && !db.TaskApprovals.Any(b => b.TaskId == a.TaskId && b.Round == a.Round && b.Decision == ApprovalDecision.Pending && b.Stage < a.Stage))
            .OrderBy(a => a.RequestedAt).Take(50).ToListAsync(ct);
        var extensions = await db.ExtensionRequests.AsNoTracking()
            .Where(x => x.ApproverId == me && x.Status == ExtensionStatus.Pending && visibleIds.Contains(x.TaskId))
            .OrderBy(x => x.RequestedAt).Take(50).ToListAsync(ct);
        var suggestions = await db.TaskSuggestions.AsNoTracking()
            .Where(s => s.Status == SuggestionStatus.Pending && s.Kind == SuggestionKind.MarkCompletedByAssignee
                && vis.Any(t => t.Id == s.TaskId && (t.DelegatorId == me || t.OriginalDelegatorId == me) && t.Status != WorkStatus.Completed && t.Status != WorkStatus.Cancelled))
            .OrderBy(s => s.CreatedAt).Take(50).ToListAsync(ct);

        var taskIds = approvals.Select(a => a.TaskId).Concat(extensions.Select(x => x.TaskId)).Concat(suggestions.Select(s => s.TaskId)).Distinct().ToList();
        if (taskIds.Count == 0) return [];
        var tasks = (await queries.ProjectAsync(vis.Where(t => taskIds.Contains(t.Id)), c, ct)).ToDictionary(t => t.Id);
        var result = new List<ApprovalQueueItem>();
        foreach (var a in approvals)
            if (tasks.TryGetValue(a.TaskId, out var t))
                result.Add(new ApprovalQueueItem("approval", a.Id, t, $"Confirm completion: {t.Title}",
                    $"{Text.Humanize(a.ApproverRole)} approval (stage {a.Stage}, round {a.Round})", a.RequestedAt));
        foreach (var x in extensions)
            if (tasks.TryGetValue(x.TaskId, out var t))
                result.Add(new ApprovalQueueItem("extension", x.Id, t, $"Extension request: {t.Title}",
                    $"{(x.CurrentDueDate is { } d ? $"From {d.ToString("d MMM", CultureInfo.InvariantCulture)} to " : "To ")}{x.ProposedDueDate.ToString("d MMM", CultureInfo.InvariantCulture)} — {x.Reason}",
                    x.RequestedAt));
        foreach (var s in suggestions)
            if (tasks.TryGetValue(s.TaskId, out var t))
                result.Add(new ApprovalQueueItem("suggestion", s.Id, t, $"Completion reported: {t.Title}",
                    $"{s.Explanation} ({s.Confidence.ToString("P0", CultureInfo.InvariantCulture)} confidence)", s.CreatedAt));
        return [.. result.OrderBy(r => r.RequestedAt)];
    }

    private async Task<string> GreetingAsync(QueryClock c, CancellationToken ct)
    {
        var hour = c.Calendar.ToLocal(c.Now).Hour;
        var part = hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";
        if (actor.EmployeeId is not { } me) return part;
        var name = await db.Employees.AsNoTracking().Where(e => e.Id == me).Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
        var first = name?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is null ? part : $"{part}, {first}";
    }

    private static string Tone(int value, string tone) => value == 0 ? "neutral" : tone;

    // ═══════════════════════════════ My dashboard (spec §41) ═══════════════════════════════

    public async Task<MyDashboard> MyDashboardAsync(CancellationToken ct)
    {
        var me = actor.EmployeeId ?? throw new ForbiddenException("Sign in to see your dashboard.");
        var c = await queries.ClockAsync(ct);
        var person = await db.Employees.AsNoTracking().Where(e => e.Id == me).Select(e => new PersonRef(e.Id, e.DisplayName, e.JobTitle, e.Email))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Employee");
        var vis = db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer);
        var mine = vis.AssignedTo(me);
        var completedFrom = c.Today.AddDays(-30);
        var waiting = vis.ActiveOnly().Where(t => ((t.DelegatorId == me || t.OriginalDelegatorId == me) && t.PrimaryAssigneeId != me)
            || ((t.PrimaryAssigneeId == me || t.Assignees.Any(a => a.RemovedAt == null && a.EmployeeId == me)) && (t.IsBlocked || t.Status == WorkStatus.Blocked)));
        var awaitingUpdate = mine.NoResponseOnly();
        var acknowledge = mine.OpenOnly().Where(t => t.Acknowledgement == AcknowledgementState.Pending);
        var recurringMine = mine.ActiveOnly().Where(t => t.RecurringDefinitionId != null);
        var completed = mine.CompletedBetween(c.Utc(completedFrom), c.Now.AddSeconds(1));

        var counts = new
        {
            Open = await mine.ActiveOnly().CountAsync(ct),
            DueToday = await mine.DueToday(c).CountAsync(ct),
            DueWeek = await mine.DueThisWeek(c).CountAsync(ct),
            Overdue = await mine.OverdueAt(c.Now).CountAsync(ct),
            Critical = await mine.ActiveOnly().CriticalOnly().CountAsync(ct),
            Waiting = await waiting.CountAsync(ct),
            AwaitingUpdate = await awaitingUpdate.CountAsync(ct),
            Recurring = await recurringMine.CountAsync(ct),
            Completed = await completed.CountAsync(ct),
            Acknowledge = await acknowledge.CountAsync(ct),
        };
        var tiles = new List<Tile>
        {
            new("my-tasks", "My Tasks", counts.Open, "neutral", Drilldown.Tasks(("scope", "mine"), ("open", "true"))),
            new("due-today", "Due Today", counts.DueToday, Tone(counts.DueToday, "warning"), Drilldown.Tasks(("scope", "mine"), ("bucket", "DueToday"))),
            new("due-this-week", "Due This Week", counts.DueWeek, Tone(counts.DueWeek, "info"), Drilldown.Tasks(("scope", "mine"), ("bucket", "DueThisWeek"))),
            new("overdue", "Overdue", counts.Overdue, Tone(counts.Overdue, "danger"), Drilldown.Tasks(("scope", "mine"), ("overdue", "true"), ("sort", "due"))),
            new("critical", "Critical", counts.Critical, Tone(counts.Critical, "danger"), Drilldown.Tasks(("scope", "mine"), ("open", "true"), ("minCriticality", "Critical"))),
            new("waiting-for-others", "Waiting for Others", counts.Waiting, Tone(counts.Waiting, "info"), Drilldown.Tasks(("scope", "delegated"), ("open", "true")),
                "Work you delegated that is still open, plus your tasks blocked by others"),
            new("awaiting-my-update", "Awaiting My Update", counts.AwaitingUpdate, Tone(counts.AwaitingUpdate, "warning"),
                Drilldown.Tasks(("scope", "mine"), ("noResponse", "true")), "A reminder was sent and no update has been received from you"),
            new("recurring", "Recurring", counts.Recurring, "neutral", Drilldown.Tasks(("scope", "mine"), ("recurring", "true"), ("open", "true"))),
            new("completed", "Completed", counts.Completed, counts.Completed > 0 ? "success" : "neutral",
                Drilldown.Tasks(("scope", "mine"), ("completedFrom", completedFrom.ToString("yyyy-MM-dd")), ("sort", "completed")), "Last 30 days"),
        };

        var sections = new List<WorkbenchSection>
        {
            new("my-tasks", "My Tasks", "Your open work, most urgent first", counts.Open, Drilldown.Tasks(("scope", "mine"), ("open", "true")),
                await queries.ProjectAsync(TaskQueryService.Sort(mine.ActiveOnly(), "attention").Take(SectionSize), c, ct)),
            new("acknowledge", "Waiting for Your Acknowledgement", "Accept, ask for clarification or propose a different deadline", counts.Acknowledge,
                Drilldown.Tasks(("scope", "mine"), ("statuses", "AcknowledgementPending")),
                await queries.ProjectAsync(acknowledge.OrderBy(t => t.AssignedAt).Take(SectionSize), c, ct)),
            new("due-today", "Due Today", "Work due today", counts.DueToday, Drilldown.Tasks(("scope", "mine"), ("bucket", "DueToday")),
                await queries.ProjectAsync(mine.DueToday(c).OrderBy(t => t.DeadlineAt).Take(SectionSize), c, ct)),
            new("overdue", "Overdue", "Past the deadline", counts.Overdue, Drilldown.Tasks(("scope", "mine"), ("overdue", "true"), ("sort", "due")),
                await queries.ProjectAsync(mine.OverdueAt(c.Now).OrderBy(t => t.DueDate).Take(SectionSize), c, ct)),
            new("awaiting-my-update", "Tasks Awaiting My Update", "A reminder was sent and you have not responded yet", counts.AwaitingUpdate,
                Drilldown.Tasks(("scope", "mine"), ("noResponse", "true")),
                await queries.ProjectAsync(awaitingUpdate.OrderBy(t => t.NoResponseSince).Take(SectionSize), c, ct)),
            new("waiting-for-others", "Waiting for Others", "Delegated work still open and your tasks blocked by others", counts.Waiting,
                Drilldown.Tasks(("scope", "delegated"), ("open", "true")),
                await queries.ProjectAsync(TaskQueryService.Sort(waiting, "due").Take(SectionSize), c, ct)),
        };
        var approvals = await ApprovalQueueAsync(c, ct);
        sections.Add(new WorkbenchSection("approvals", "Waiting for My Approval", "Completion confirmations and extension requests assigned to you",
            approvals.Count, Drilldown.Tasks(("awaitingApproval", "true")), [], Approvals: approvals));
        sections.Add(new WorkbenchSection("recurring", "Recurring", "Your open recurring occurrences", counts.Recurring,
            Drilldown.Tasks(("scope", "mine"), ("recurring", "true"), ("open", "true")),
            await queries.ProjectAsync(TaskQueryService.Sort(recurringMine, "due").Take(SectionSize), c, ct)));
        sections.Add(new WorkbenchSection("completed", "Completed", "Completed in the last 30 days", counts.Completed,
            Drilldown.Tasks(("scope", "mine"), ("completedFrom", completedFrom.ToString("yyyy-MM-dd")), ("sort", "completed")),
            await queries.ProjectAsync(completed.OrderByDescending(t => t.CompletedAt).Take(SectionSize), c, ct)));
        return new MyDashboard(person, tiles, sections);
    }

    // ═══════════════════════════════ Team dashboards (spec §40) ═══════════════════════════════

    public async Task<List<NamedRef>> TeamsAsync(CancellationToken ct)
    {
        var v = actor.Viewer;
        var q = db.Teams.AsNoTracking().Where(t => t.IsActive);
        if (!v.IsExecutive && !actor.IsSystem)
        {
            var led = v.LedTeamIds.ToList();
            var depts = v.ManagedDepartmentIds.ToList();
            q = q.Where(t => led.Contains(t.Id) || (t.DepartmentId != null && depts.Contains(t.DepartmentId.Value)));
        }
        return await q.OrderBy(t => t.Name).Select(t => new NamedRef(t.Id, t.Name)).ToListAsync(ct);
    }

    public async Task<TeamDashboard> TeamDashboardAsync(Guid teamId, CancellationToken ct)
    {
        var team = await db.Teams.AsNoTracking().Include(t => t.Members).FirstOrDefaultAsync(t => t.Id == teamId, ct) ?? throw new NotFoundException("Team");
        var v = actor.Viewer;
        if (!actor.IsSystem && !v.IsExecutive && !v.LedTeamIds.Contains(teamId) && !(team.DepartmentId is { } d && v.ManagedDepartmentIds.Contains(d)))
            throw new ForbiddenException("Only the team's leaders, the department head and executives can open this team dashboard.");
        var c = await queries.ClockAsync(ct);
        var memberIds = team.Members.Select(m => m.EmployeeId).Concat(new[] { team.LeaderId, team.DeputyLeaderId }.Where(x => x is not null).Select(x => x!.Value))
            .Distinct().ToList();
        var people = await db.Employees.AsNoTracking().Where(e => memberIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new PersonRef(e.Id, e.DisplayName, e.JobTitle, e.Email), ct);
        var vis = db.Tasks.AsNoTracking().VisibleTo(db, v);
        var teamTasks = vis.ForTeam(db, teamId);
        var staleBefore = c.Now.AddDays(-5);
        var completedSince = c.Now.AddDays(-30);

        var rows = await vis.Where(t => (t.Status != WorkStatus.Completed && t.Status != WorkStatus.Cancelled && t.Status != WorkStatus.Draft && t.Status != WorkStatus.Deferred)
                || (t.Status == WorkStatus.Completed && t.CompletedAt >= completedSince))
            .Where(t => (t.PrimaryAssigneeId != null && memberIds.Contains(t.PrimaryAssigneeId.Value))
                || t.Assignees.Any(a => a.RemovedAt == null && a.EmployeeId != null && memberIds.Contains(a.EmployeeId.Value)))
            .Select(t => new
            {
                t.PrimaryAssigneeId,
                People = t.Assignees.Where(a => a.RemovedAt == null && a.EmployeeId != null).Select(a => a.EmployeeId!.Value).ToList(),
                t.Status, t.DueDate, t.DeadlineAt, t.Criticality, t.IsBlocked, t.LastActivityAt, t.CompletedAt,
            }).ToListAsync(ct);
        var members = new List<MemberLoad>();
        foreach (var id in memberIds)
        {
            if (!people.TryGetValue(id, out var person)) continue;
            var mine = rows.Where(r => r.PrimaryAssigneeId == id || r.People.Contains(id)).ToList();
            var active = mine.Where(r => TaskStatusRules.IsActive(r.Status)).ToList();
            bool Overdue(WorkStatus s, DateTime? deadline) => deadline is { } dl && dl < c.Now && !TaskStatusRules.IsAwaitingConfirmation(s);
            members.Add(new MemberLoad(person,
                active.Count,
                active.Count(r => r.DueDate is { } dd && dd >= c.Today && dd <= c.WeekEnd && !Overdue(r.Status, r.DeadlineAt) && !TaskStatusRules.IsAwaitingConfirmation(r.Status)),
                active.Count(r => Overdue(r.Status, r.DeadlineAt)),
                active.Count(r => r.Criticality >= Criticality.Critical),
                active.Count(r => r.IsBlocked || r.Status == WorkStatus.Blocked),
                active.Count(r => r.LastActivityAt is null || r.LastActivityAt < staleBefore),
                mine.Count(r => r.Status == WorkStatus.Completed)));
        }

        var noRecent = teamTasks.ActiveOnly().Where(t => t.LastActivityAt == null || t.LastActivityAt < staleBefore);
        var open = await teamTasks.ActiveOnly().CountAsync(ct);
        var overdue = await teamTasks.OverdueAt(c.Now).CountAsync(ct);
        var dueWeek = await teamTasks.DueThisWeek(c).CountAsync(ct);
        var blocked = await teamTasks.BlockedOnly().CountAsync(ct);
        var critical = await teamTasks.ActiveOnly().CriticalOnly().CountAsync(ct);
        var stale = await noRecent.CountAsync(ct);
        var completed = await teamTasks.CompletedBetween(completedSince, c.Now.AddSeconds(1)).CountAsync(ct);
        var tid = teamId.ToString();
        var tiles = new List<Tile>
        {
            new("open", "Open", open, "neutral", Drilldown.Tasks(("teamId", tid), ("open", "true"))),
            new("overdue", "Overdue", overdue, Tone(overdue, "danger"), Drilldown.Tasks(("teamId", tid), ("overdue", "true"), ("sort", "due"))),
            new("due-this-week", "Due This Week", dueWeek, Tone(dueWeek, "info"), Drilldown.Tasks(("teamId", tid), ("bucket", "DueThisWeek"))),
            new("blocked", "Blocked", blocked, Tone(blocked, "warning"), Drilldown.Tasks(("teamId", tid), ("blocked", "true"))),
            new("critical", "Critical", critical, Tone(critical, "danger"), Drilldown.Tasks(("teamId", tid), ("open", "true"), ("minCriticality", "Critical"))),
            new("no-recent-update", "No Recent Update", stale, Tone(stale, "warning"), Drilldown.Tasks(("teamId", tid), ("open", "true"), ("sort", "updated")),
                "No activity for 5 days or more"),
            new("completed", "Completed", completed, completed > 0 ? "success" : "neutral",
                Drilldown.Tasks(("teamId", tid), ("completedFrom", c.Today.AddDays(-30).ToString("yyyy-MM-dd")), ("sort", "completed")), "Last 30 days"),
        };

        return new TeamDashboard(
            new NamedRef(team.Id, team.Name),
            team.LeaderId is { } l && people.TryGetValue(l, out var leader) ? leader : null,
            tiles,
            [.. members.OrderByDescending(m => m.Overdue).ThenByDescending(m => m.Open).ThenBy(m => m.Person.Name)],
            await queries.ProjectAsync(teamTasks.OpenOnly().Where(t => t.AttentionScore > 0).OrderByDescending(t => t.AttentionScore).Take(SectionSize), c, ct),
            await queries.ProjectAsync(teamTasks.OverdueAt(c.Now).OrderBy(t => t.DueDate).Take(20), c, ct),
            await queries.ProjectAsync(teamTasks.BlockedOnly().OrderBy(t => t.BlockedSince).Take(20), c, ct),
            await queries.ProjectAsync(noRecent.OrderBy(t => t.LastActivityAt).Take(20), c, ct),
            await recurring.ListAsync(false, ct, teamId));
    }

    // ═══════════════════════════════ Projects (spec §46) ═══════════════════════════════

    public async Task<List<ProjectSummary>> ProjectsAsync(CancellationToken ct, bool includeInactive = true)
    {
        var q = db.Projects.AsNoTracking().VisibleProjects(actor.Viewer);
        if (!includeInactive) q = q.Where(p => p.IsActive);
        var projects = await q.OrderByDescending(p => p.IsActive).ThenBy(p => p.Name).ToListAsync(ct);
        return await SummariesAsync(projects, ct);
    }

    public async Task<ProjectSummary> ProjectSummaryAsync(Guid id, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().VisibleProjects(actor.Viewer).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Project");
        return (await SummariesAsync([project], ct))[0];
    }

    public async Task<ProjectDetail> ProjectDetailAsync(Guid id, CancellationToken ct)
    {
        var summary = await ProjectSummaryAsync(id, ct);
        var c = await queries.ClockAsync(ct);
        var projectTasks = db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer).Where(t => t.ProjectId == id);
        var taskIds = projectTasks.Select(t => t.Id);
        var missedFrom = c.Today.AddDays(-30);
        var list = await queries.ProjectAsync(projectTasks.Where(t => t.Status != WorkStatus.Cancelled)
            .OrderBy(t => t.Status == WorkStatus.Completed).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.Number).Take(500), c, ct);
        var blockers = await queries.ProjectAsync(projectTasks.BlockedOnly().OrderByDescending(t => t.Criticality).ThenBy(t => t.BlockedSince).Take(50), c, ct);
        var projectCommitments = await commitments.ProjectAsync(db.Commitments.AsNoTracking()
            .Where(x => taskIds.Contains(x.TaskId) && (x.Status == CommitmentStatus.Open || (x.Status == CommitmentStatus.Missed && x.CommitmentDate >= missedFrom)))
            .OrderBy(x => x.CommitmentDate).Take(100), ct);
        return new ProjectDetail(summary, list, blockers, projectCommitments);
    }

    private sealed record ProjectTaskRow(Guid Id, Guid ProjectId, Guid? ParentTaskId, WorkStatus Status, Criticality Criticality, TaskHealth Health,
        int ProgressPercent, DateOnly? DueDate, DateTime? DeadlineAt, bool IsBlocked);

    private async Task<List<ProjectSummary>> SummariesAsync(List<Project> projects, CancellationToken ct)
    {
        if (projects.Count == 0) return [];
        var c = await queries.ClockAsync(ct);
        var ids = projects.Select(p => p.Id).ToList();
        var rows = await db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer)
            .Where(t => t.ProjectId != null && ids.Contains(t.ProjectId.Value) && t.Status != WorkStatus.Draft)
            .Select(t => new ProjectTaskRow(t.Id, t.ProjectId!.Value, t.ParentTaskId, t.Status, t.Criticality, t.Health, t.ProgressPercent, t.DueDate,
                t.DeadlineAt, t.IsBlocked))
            .ToListAsync(ct);
        var ownerIds = projects.Where(p => p.OwnerId != null).Select(p => p.OwnerId!.Value).Distinct().ToList();
        var people = await db.Employees.AsNoTracking().Where(e => ownerIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new PersonRef(e.Id, e.DisplayName, e.JobTitle, e.Email), ct);
        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var byProject = rows.GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.ToList());

        // Next deadline per project → one projector call.
        var next = new Dictionary<Guid, Guid>();
        foreach (var (pid, list) in byProject)
            if (list.Where(r => !TaskStatusRules.IsClosed(r.Status) && r.DueDate is { } d && d >= c.Today).OrderBy(r => r.DueDate).FirstOrDefault() is { } n)
                next[pid] = n.Id;
        var nextIds = next.Values.ToList();
        var nextItems = nextIds.Count == 0 ? [] : (await queries.ProjectAsync(db.Tasks.AsNoTracking().Where(t => nextIds.Contains(t.Id)), c, ct)).ToDictionary(t => t.Id);

        return projects.Select(p =>
        {
            var list = byProject.TryGetValue(p.Id, out var l) ? l : [];
            var live = list.Where(r => r.Status != WorkStatus.Cancelled).ToList();
            var open = live.Where(r => !TaskStatusRules.IsClosed(r.Status)).ToList();
            bool IsOverdue(ProjectTaskRow r) => r.DeadlineAt is { } dl && dl < c.Now && r.Status != WorkStatus.Deferred && !TaskStatusRules.IsAwaitingConfirmation(r.Status)
                && !TaskStatusRules.IsClosed(r.Status);
            var overdue = open.Where(IsOverdue).ToList();
            var blocked = open.Count(r => r.IsBlocked || r.Status == WorkStatus.Blocked);
            var critical = open.Count(r => r.Criticality >= Criticality.Critical);
            var status = live.Count == 0 ? (list.Count == 0 ? "Not Started" : "Completed")
                : open.Count == 0 ? "Completed"
                : overdue.Any(r => r.Criticality >= Criticality.Critical) || (overdue.Count > 0 && overdue.Count >= 0.2 * open.Count) ? "Off Track"
                : overdue.Count > 0 || blocked > 0 || open.Any(r => r.Health >= TaskHealth.AtRisk) ? "At Risk"
                : "On Track";
            var liveIds = live.Select(r => r.Id).ToHashSet();
            var top = live.Where(r => r.ParentTaskId is null || !liveIds.Contains(r.ParentTaskId.Value)).ToList();
            var weight = top.Sum(r => TaskMetrics.CriticalityWeight(r.Criticality));
            var percent = weight == 0 ? 0 : (int)Math.Round(top.Sum(r => (r.Status == WorkStatus.Completed ? 100 : r.ProgressPercent) * TaskMetrics.CriticalityWeight(r.Criticality)) / (double)weight);
            var worst = open.Count == 0 ? TaskHealth.Healthy : open.Max(r => r.Health);
            return new ProjectSummary(p.Id, p.Code, p.Name, p.Description,
                p.OwnerId is { } o && people.TryGetValue(o, out var owner) ? owner : null,
                p.DepartmentId is { } d && depts.TryGetValue(d, out var dn) ? new NamedRef(d, dn) : null,
                p.StartDate, p.TargetDate, status, live.Count, open.Count, live.Count(r => r.Status == WorkStatus.Completed), overdue.Count, blocked, critical,
                percent, worst, next.TryGetValue(p.Id, out var nid) && nextItems.TryGetValue(nid, out var ni) ? ni : null, p.IsActive);
        }).ToList();
    }
}
