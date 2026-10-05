using System.Globalization;
using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Services;

/// <summary>
/// Executive summary (spec §56), daily digest (§54) and weekly executive review (§55). Every sentence is built
/// deterministically from counts and carries a drill-down; an optional narrative paragraph comes from the
/// natural-language service and is dropped silently when the AI is unavailable (spec §78).
/// </summary>
public sealed class DigestService(
    FollowUpDbContext db,
    ActorContext actor,
    TaskQueryService queries,
    AttentionService attention,
    CommitmentService commitments,
    RecurringTaskService recurring,
    NotificationService notifications,
    INaturalLanguageService nl,
    ILogger<DigestService> logger)
{
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ═══════════════════════════════ Executive summary (spec §56) ═══════════════════════════════

    public async Task<ExecutiveSummary> ExecutiveSummaryAsync(CancellationToken ct)
    {
        var c = await queries.ClockAsync(ct);
        var vis = db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer);
        var visibleIds = vis.Select(t => t.Id);
        var execs = await Executives.IdsAsync(db, ct);

        var active = await vis.ActiveOnly().CountAsync(ct);
        var needAttention = await attention.CountOpenAsync(ct);
        var critical = await vis.ActiveOnly().CriticalOnly().CountAsync(ct);
        var overdue = await vis.OverdueAt(c.Now).CountAsync(ct);
        var severe = await vis.SeverelyOverdue(c).CountAsync(ct);
        var weekStartUtc = c.Utc(c.WeekStart);
        var missedThisWeek = await db.Commitments.CountAsync(x => visibleIds.Contains(x.TaskId) && x.Status == CommitmentStatus.Missed && x.CreatedAt >= weekStartUtc, ct);
        var noResponseSince = c.Now.AddDays(-3);
        var noResponse = await vis.NoResponseOnly().CountAsync(t => t.NoResponseSince <= noResponseSince, ct);
        List<Guid> approvers = actor.Viewer.IsExecutive || actor.IsSystem ? execs : actor.EmployeeId is { } me ? [me] : [];
        var awaiting = approvers.Count == 0 ? 0 : await vis.WaitingFor(db, approvers).CountAsync(ct);
        var completedWeek = await vis.CompletedBetween(weekStartUtc, c.Now.AddSeconds(1)).CountAsync(ct);

        var external = await vis.OpenOnly()
            .Where(t => t.Status == WorkStatus.WaitingForExternalParty || db.TaskDependencies.Any(d => d.TaskId == t.Id && !d.IsResolved && d.DependsOnTaskId == null))
            .Select(t => new
            {
                t.DepartmentId,
                t.BlockedReason,
                External = db.TaskDependencies.Where(d => d.TaskId == t.Id && !d.IsResolved && d.DependsOnTaskId == null).Select(d => d.ExternalDescription).FirstOrDefault(),
            }).ToListAsync(ct);
        var overdueDepts = await vis.OverdueAt(c.Now).Select(t => t.DepartmentId).ToListAsync(ct);
        var deptNames = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var points = new List<SummaryPoint>
        {
            new($"{active} {TaskMetrics.Plural(active, "task is", "tasks are")} active.", active, "neutral", Drilldown.Tasks(("open", "true"))),
        };
        if (needAttention > 0)
            points.Add(new($"{needAttention} {TaskMetrics.Plural(needAttention, "requires", "require")} your attention.", needAttention, "warning",
                Drilldown.To("/attention", ("status", "Open"))));
        if (critical > 0)
            points.Add(new($"{critical} {TaskMetrics.Plural(critical, "is", "are")} critical.", critical, "danger",
                Drilldown.Tasks(("open", "true"), ("minCriticality", "Critical"))));
        if (overdue > 0)
            points.Add(new($"{overdue} {TaskMetrics.Plural(overdue, "is", "are")} overdue{(severe > 0 ? $", {severe} of them by {c.SevereDays} days or more" : "")}.",
                overdue, "danger", Drilldown.Tasks(("overdue", "true"), ("sort", "due"))));

        foreach (var g in external.GroupBy(x => x.DepartmentId).OrderByDescending(g => g.Count()).Take(3))
        {
            var reasons = g.Select(x => x.External ?? StripReason(x.BlockedReason)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(2).Select(x => x!).ToList();
            var n = g.Count();
            var what = $"{n} {TaskMetrics.Plural(n, "task")}{(reasons.Count > 0 ? $" ({string.Join("; ", reasons)})" : "")}";
            points.Add(g.Key is { } dep && deptNames.TryGetValue(dep, out var dn)
                ? new($"{dn} is waiting for an external party on {what}.", n, "warning", Drilldown.Tasks(("departmentId", dep.ToString()), ("blocked", "true")))
                : new($"{what} {TaskMetrics.Plural(n, "is", "are")} waiting for an external party.", n, "warning", Drilldown.Tasks(("blocked", "true"))));
        }
        foreach (var g in overdueDepts.Where(d => d is not null).GroupBy(d => d!.Value).OrderByDescending(g => g.Count()).Take(3))
        {
            if (!deptNames.TryGetValue(g.Key, out var dn)) continue;
            var n = g.Count();
            points.Add(new($"{dn} has {n} overdue {TaskMetrics.Plural(n, "activity", "activities")}.", n, "danger",
                Drilldown.Tasks(("departmentId", g.Key.ToString()), ("overdue", "true"), ("sort", "due"))));
        }
        if (missedThisWeek > 0)
            points.Add(new($"{missedThisWeek} {TaskMetrics.Plural(missedThisWeek, "commitment")} made this week {TaskMetrics.Plural(missedThisWeek, "was", "were")} missed.",
                missedThisWeek, "warning", Drilldown.To("/commitments", ("status", "Missed"), ("from", D(c.WeekStart)))));
        if (noResponse > 0)
            points.Add(new($"{noResponse} {TaskMetrics.Plural(noResponse, "task has", "tasks have")} had no response for 3 days or more.", noResponse, "warning",
                Drilldown.Tasks(("noResponse", "true"))));
        if (awaiting > 0)
            points.Add(new($"{awaiting} {TaskMetrics.Plural(awaiting, "decision is", "decisions are")} waiting for you (approvals, extensions or acknowledgement responses).",
                awaiting, "info", Drilldown.Tasks(("waitingForCeo", "true"))));
        if (completedWeek > 0)
            points.Add(new($"{completedWeek} {TaskMetrics.Plural(completedWeek, "task was", "tasks were")} completed this week.", completedWeek, "success",
                Drilldown.Tasks(("completedFrom", D(c.WeekStart)), ("sort", "completed"))));

        var headline = $"{active} active {TaskMetrics.Plural(active, "task")} — {needAttention} need{(needAttention == 1 ? "s" : "")} your attention, {critical} critical, {overdue} overdue.";
        string? narrative = null;
        string provider;
        try { provider = nl.ProviderName; } catch (Exception) { provider = "Unavailable"; }
        try { narrative = await nl.NarrateAsync("Executive summary", points.Select(p => p.Text).ToList(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Executive summary narrative unavailable; showing the deterministic points only");
        }
        return new ExecutiveSummary(headline, points, string.IsNullOrWhiteSpace(narrative) ? null : narrative, provider, c.Now);
    }

    private static string? StripReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var first = reason.Split(';')[0].Trim();
        return first.StartsWith("Waiting for ", StringComparison.OrdinalIgnoreCase) ? first[12..] : first;
    }

    // ═══════════════════════════════ Daily digest (spec §54) ═══════════════════════════════

    public async Task<DigestDto> DigestAsync(CancellationToken ct)
    {
        var c = await queries.ClockAsync(ct);
        var vis = db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer);
        var visibleIds = vis.Select(t => t.Id);
        var since = c.Now.AddHours(-24);
        var open = await vis.ActiveOnly().CountAsync(ct);
        var overdue = await vis.OverdueAt(c.Now).CountAsync(ct);
        var critical = await vis.ActiveOnly().CriticalOnly().CountAsync(ct);
        var needAttention = await attention.CountOpenAsync(ct);
        var dueToday = await vis.DueToday(c).CountAsync(ct);
        var missed = await db.Commitments.CountAsync(x => visibleIds.Contains(x.TaskId) && x.Status == CommitmentStatus.Missed && x.ResolvedAt != null && x.ResolvedAt >= since, ct);
        var completedYesterday = await vis.CompletedBetween(c.Utc(c.Today.AddDays(-1)), c.Utc(c.Today)).CountAsync(ct);
        var top = await attention.ListAsync("Open", ct, 5);
        var summary = await ExecutiveSummaryAsync(ct);
        return new DigestDto(c.Now, open, overdue, critical, needAttention, dueToday, missed, completedYesterday, top, summary);
    }

    /// <summary>Queues today's digest for every executive (email + in-app). Idempotent per local date: "digest:{yyyy-MM-dd}".</summary>
    public async Task<int> SendDailyDigestAsync(CancellationToken ct)
    {
        var execs = await Executives.IdsAsync(db, ct);
        if (execs.Count == 0) return 0;
        var c = await queries.ClockAsync(ct);
        var d = await DigestAsync(ct);
        var lines = new List<string>
        {
            $"Open: {d.Open}",
            $"Overdue: {d.Overdue}",
            $"Critical: {d.Critical}",
            $"Need your attention: {d.NeedAttention}",
            $"Due today: {d.DueToday}",
            $"Missed commitments (last 24 hours): {d.MissedCommitments}",
            $"Completed yesterday: {d.CompletedYesterday}",
        };
        foreach (var a in d.TopAttention)
            lines.Add($"Attention: {a.Title}{(a.Task is { } t ? $" ({t.Number}, {t.DeadlineText.ToLowerInvariant()})" : "")}");
        foreach (var p in d.Summary.Points.Skip(1).Take(6)) lines.Add(p.Text);

        var n = await notifications.EnqueueAsync(new NoticeRequest
        {
            Key = $"digest:{D(c.Today)}",
            Category = NotificationCategory.Digest,
            RecipientIds = execs,
            Headline = $"Your task digest for {c.Today.ToString("dddd d MMMM", CultureInfo.InvariantCulture)}: {d.Summary.Headline}",
            Message = d.Summary.Narrative,
            ExtraLines = lines,
            Channels = NotifyChannels.Email | NotifyChannels.InApp,
            CustomSubject = $"CEO Task Digest — {c.Today.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}",
            ExpiresIn = TimeSpan.FromHours(6),
        }, ct);
        await db.SaveChangesAsync(ct);
        return n;
    }

    // ═══════════════════════════════ Weekly executive review (spec §55) ═══════════════════════════════

    /// <summary>The week to review when none is given: the previous week on Mondays, otherwise the current week.</summary>
    public static DateOnly DefaultWeekStart(QueryClock c) =>
        c.Today.DayOfWeek == DayOfWeek.Monday ? c.WeekStart.AddDays(-7) : c.WeekStart;

    public async Task<WeeklyReviewDto> WeeklyReviewAsync(DateOnly? weekStart, CancellationToken ct)
    {
        var c = await queries.ClockAsync(ct);
        var ws = weekStart is { } w ? BusinessCalendar.StartOfWeek(w) : DefaultWeekStart(c);
        var we = ws.AddDays(6);
        var fromUtc = c.Utc(ws);
        var toUtc = c.Utc(we.AddDays(1));
        var vis = db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer);
        var visibleIds = vis.Select(t => t.Id);
        var execs = await Executives.IdsAsync(db, ct);

        var completed = await queries.ProjectAsync(vis.CompletedBetween(fromUtc, toUtc).OrderByDescending(t => t.Criticality).ThenByDescending(t => t.CompletedAt).Take(25), c, ct);
        var overdueNow = await queries.ProjectAsync(vis.OverdueAt(c.Now).OrderBy(t => t.DueDate).Take(200), c, ct);
        var lateCompleted = await queries.ProjectAsync(vis.CompletedBetween(fromUtc, toUtc).Where(t => t.DeadlineAt != null && t.CompletedAt > t.DeadlineAt)
            .OrderByDescending(t => t.Criticality).Take(25), c, ct);
        var delayed = overdueNow.OrderByDescending(t => TaskMetrics.Severity(t.DaysOverdue, t.Criticality)).Take(25).Concat(lateCompleted).ToList();
        var risks = await queries.ProjectAsync(TaskQueryService.Sort(vis.OpenOnly().Where(t => (t.Criticality >= Criticality.Critical && t.Health >= TaskHealth.AtRisk)
            || t.Health == TaskHealth.Critical), "attention").Take(20), c, ct);
        var missed = await commitments.ProjectAsync(db.Commitments.AsNoTracking()
            .Where(x => visibleIds.Contains(x.TaskId) && x.Status == CommitmentStatus.Missed && x.CommitmentDate >= ws && x.CommitmentDate <= we)
            .OrderBy(x => x.CommitmentDate), ct);
        var blockers = await queries.ProjectAsync(vis.BlockedOnly().Where(t => t.Criticality >= Criticality.Important)
            .OrderByDescending(t => t.Criticality).ThenBy(t => t.BlockedSince).Take(20), c, ct);
        var upcoming = await queries.ProjectAsync(vis.DueBetween(c, c.Today, c.Today.AddDays(7)).OrderByDescending(t => t.Criticality).ThenBy(t => t.DueDate).Take(25), c, ct);
        var recurringMissed = await recurring.InstancesAsync(null, null, ws, we, missedOnly: true, ct);
        var ceoTasks = await queries.ProjectAsync(TaskQueryService.Sort(vis.WaitingFor(db, execs), "attention").Take(20), c, ct);
        var openAttention = await attention.ListAsync("Open", ct, 10);
        var teams = await TeamRowsAsync(c, fromUtc, toUtc, ct);

        string Line(TaskListItem t) => $"{t.Number} — {t.Title} ({t.PrimaryAssignee?.Name ?? "unassigned"}, {t.DeadlineText.ToLowerInvariant()})";
        var sections = new List<ReviewSection>
        {
            new("completed", "Completed Tasks", completed),
            new("delayed", "Delayed Tasks", delayed, Lines: lateCompleted.Select(t => $"{t.Number} was completed after its deadline").ToList()),
            new("critical-risks", "Critical Risks", risks),
            new("missed-commitments", "Missed Commitments", [], Commitments: missed),
            new("blockers", "Major Blockers", blockers, Lines: blockers.Select(t => $"{t.Number}: {t.BlockedReason ?? "Blocked"}").ToList()),
            new("team-status", "Team Status", [], Lines: teams.Select(r => $"{r.Team}: {r.Open} open, {r.Overdue} overdue, {r.Blocked} blocked, {r.Completed} completed ({r.OnTimePercent}% on time)").ToList()),
            new("upcoming", "Upcoming Deadlines", upcoming),
            new("recurring-missed", "Recurring Tasks Missed", [], Instances: recurringMissed),
            new("ceo-actions", "CEO Actions Required", ceoTasks, Lines: openAttention.Select(a => a.Title).Concat(ceoTasks.Select(Line)).Distinct().Take(20).ToList()),
        };
        return new WeeklyReviewDto(ws, we, await ExecutiveSummaryAsync(ct), sections, teams);
    }

    private async Task<List<TeamStatusRow>> TeamRowsAsync(QueryClock c, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var teams = await db.Teams.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, Members = t.Members.Select(m => m.EmployeeId).ToList() }).ToListAsync(ct);
        if (teams.Count == 0) return [];
        var rows = await db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer)
            .Where(t => (t.Status != WorkStatus.Completed && t.Status != WorkStatus.Cancelled && t.Status != WorkStatus.Draft && t.Status != WorkStatus.Deferred)
                || (t.Status == WorkStatus.Completed && t.CompletedAt >= fromUtc && t.CompletedAt < toUtc))
            .Select(t => new
            {
                t.TeamId, t.PrimaryAssigneeId, t.Status, t.DeadlineAt, t.CompletedAt, t.Criticality, t.IsBlocked,
                AssignedTeams = t.Assignees.Where(a => a.RemovedAt == null && a.TeamId != null).Select(a => a.TeamId!.Value).ToList(),
            }).ToListAsync(ct);
        var result = new List<TeamStatusRow>();
        foreach (var team in teams)
        {
            var mine = rows.Where(r => r.TeamId == team.Id || r.AssignedTeams.Contains(team.Id) || (r.PrimaryAssigneeId is { } p && team.Members.Contains(p))).ToList();
            var open = mine.Where(r => r.Status != WorkStatus.Completed).ToList();
            var overdue = open.Where(r => r.DeadlineAt is { } d && d < c.Now && !TaskStatusRules.IsAwaitingConfirmation(r.Status)).ToList();
            var done = mine.Where(r => r.Status == WorkStatus.Completed).ToList();
            var onTime = done.Count(r => r.DeadlineAt is null || r.CompletedAt <= r.DeadlineAt);
            var blocked = open.Count(r => r.IsBlocked || r.Status == WorkStatus.Blocked);
            var health = overdue.Any(r => r.Criticality >= Criticality.Critical) ? TaskHealth.Critical
                : overdue.Count > 0 && overdue.Count >= 0.2 * open.Count ? TaskHealth.AtRisk
                : overdue.Count > 0 || blocked > 0 ? TaskHealth.Watch
                : TaskHealth.Healthy;
            result.Add(new TeamStatusRow(team.Id, team.Name, open.Count, overdue.Count, done.Count, blocked,
                done.Count == 0 ? 100 : (int)Math.Round(100.0 * onTime / done.Count), health));
        }
        return result;
    }

    /// <summary>Queues the weekly review for every executive. Idempotent per week: "weekly:{weekStart}".</summary>
    public async Task<int> SendWeeklyReviewAsync(DateOnly? weekStart, CancellationToken ct)
    {
        var execs = await Executives.IdsAsync(db, ct);
        if (execs.Count == 0) return 0;
        var review = await WeeklyReviewAsync(weekStart, ct);
        var lines = new List<string>();
        foreach (var s in review.Sections)
        {
            var count = s.Tasks.Count + (s.Commitments?.Count ?? 0) + (s.Instances?.Count ?? 0);
            if (s.Key == "team-status") count = review.Teams.Count;
            lines.Add($"{s.Title}: {count}");
            foreach (var t in s.Tasks.Take(3)) lines.Add($"  {t.Number} — {t.Title} ({t.DisplayStatus}, {t.DeadlineText.ToLowerInvariant()})");
            foreach (var cm in (s.Commitments ?? []).Take(3)) lines.Add($"  {cm.TaskNumber}: {cm.Person?.Name ?? "Someone"} committed to {cm.CommitmentDate.ToString("ddd d MMM", CultureInfo.InvariantCulture)}");
            foreach (var i in (s.Instances ?? []).Take(3)) lines.Add($"  {i.DefinitionTitle} ({i.NominalDate.ToString("d MMM", CultureInfo.InvariantCulture)})");
            if (s.Key == "team-status") foreach (var l in (s.Lines ?? []).Take(8)) lines.Add($"  {l}");
        }
        var n = await notifications.EnqueueAsync(new NoticeRequest
        {
            Key = $"weekly:{D(review.WeekStart)}",
            Category = NotificationCategory.WeeklyReview,
            RecipientIds = execs,
            Headline = $"Weekly executive review for {review.WeekStart.ToString("d MMM", CultureInfo.InvariantCulture)} – {review.WeekEnd.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}: {review.Summary.Headline}",
            Message = review.Summary.Narrative,
            ExtraLines = lines,
            Channels = NotifyChannels.Email | NotifyChannels.InApp,
            CustomSubject = $"Weekly Executive Review — week of {review.WeekStart.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}",
            ExpiresIn = TimeSpan.FromHours(24),
        }, ct);
        await db.SaveChangesAsync(ct);
        return n;
    }
}
