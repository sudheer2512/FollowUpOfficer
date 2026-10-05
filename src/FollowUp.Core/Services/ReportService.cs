using System.Globalization;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

/// <summary>
/// Performance reports (spec §56 analytics): on-time completion, delays, ageing, escalations, commitments and
/// recurring compliance by department, team and person for a period — over the viewer's visible tasks only.
/// Rows are loaded once with a narrow projection and aggregated in memory (no SQL aggregation of decimals/dates).
/// </summary>
public sealed class ReportService(
    FollowUpDbContext db,
    ActorContext actor,
    TaskQueryService queries,
    CommitmentService commitments,
    RecurringTaskService recurring)
{
    private sealed record Row(Guid Id, Guid? DepartmentId, Guid? TeamId, Guid? PrimaryAssigneeId, List<Guid> AssignedTeams, WorkStatus Status,
        DateOnly? DueDate, DateTime? DeadlineAt, DateTime CreatedAt, DateTime? AssignedAt, DateTime? CompletedAt, Criticality Criticality,
        DateTime? NoResponseSince);

    public async Task<ReportDto> ReportAsync(ReportRequest r, CancellationToken ct)
    {
        var c = await queries.ClockAsync(ct);
        var to = r.To ?? c.Today;
        var from = r.From ?? to.AddDays(-29);
        if (from > to) throw new ValidationException("dates", "The report start is after its end.");
        if (to.DayNumber - from.DayNumber > 730) throw new ValidationException("range_too_long", "Reports cover at most two years.");
        var fromUtc = c.Utc(from);
        var toUtc = c.Utc(to.AddDays(1));

        var vis = db.Tasks.AsNoTracking().VisibleTo(db, actor.Viewer).Where(t => t.Status != WorkStatus.Draft);
        if (r.DepartmentId is { } dep) vis = vis.Where(t => t.DepartmentId == dep);
        if (r.TeamId is { } team) vis = vis.ForTeam(db, team);
        var scopedIds = vis.Select(t => t.Id);

        var rows = await vis.Where(t => t.CreatedAt < toUtc && (t.Status != WorkStatus.Completed && t.Status != WorkStatus.Cancelled
                || (t.CompletedAt != null && t.CompletedAt >= fromUtc) || (t.ClosedAt != null && t.ClosedAt >= fromUtc) || t.CreatedAt >= fromUtc))
            .Select(t => new Row(t.Id, t.DepartmentId, t.TeamId, t.PrimaryAssigneeId,
                t.Assignees.Where(a => a.RemovedAt == null && a.TeamId != null).Select(a => a.TeamId!.Value).ToList(),
                t.Status, t.DueDate, t.DeadlineAt, t.CreatedAt, t.AssignedAt, t.CompletedAt, t.Criticality, t.NoResponseSince))
            .ToListAsync(ct);
        var escalations = await db.TaskEscalations.AsNoTracking().Where(e => scopedIds.Contains(e.TaskId) && e.TriggeredAt >= fromUtc && e.TriggeredAt < toUtc)
            .Select(e => new { e.TaskId, e.Level, e.Trigger }).ToListAsync(ct);
        var commitmentRows = await db.Commitments.AsNoTracking()
            .Where(x => scopedIds.Contains(x.TaskId) && x.CommitmentDate >= from && x.CommitmentDate <= to && (x.Status == CommitmentStatus.Met || x.Status == CommitmentStatus.Missed))
            .Select(x => new { x.TaskId, x.EmployeeId, x.Status }).ToListAsync(ct);
        var reopened = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "Reopened" && a.TaskId != null && scopedIds.Contains(a.TaskId.Value) && a.At >= fromUtc && a.At < toUtc)
            .Select(a => a.TaskId!.Value).ToListAsync(ct);
        var extensions = await db.ExtensionRequests.AsNoTracking().CountAsync(x => scopedIds.Contains(x.TaskId) && x.RequestedAt >= fromUtc && x.RequestedAt < toUtc, ct);

        bool CompletedInRange(Row x) => x.Status == WorkStatus.Completed && x.CompletedAt is { } ca && ca >= fromUtc && ca < toUtc;
        bool OnTime(Row x) => x.DeadlineAt is null || x.CompletedAt <= x.DeadlineAt;
        bool OverdueNow(Row x) => x.DeadlineAt is { } d && d < c.Now && TaskStatusRules.IsActive(x.Status) && !TaskStatusRules.IsAwaitingConfirmation(x.Status);
        double DaysBetween(DateTime a, DateTime b) => Math.Max(0, (b - a).TotalDays);

        GroupPerformance Perform(Guid id, string name, List<Row> set)
        {
            var ids = set.Select(x => x.Id).ToHashSet();
            var assigned = set.Count(x => x.CreatedAt >= fromUtc);
            var done = set.Where(CompletedInRange).ToList();
            var onTime = done.Count(OnTime);
            var late = done.Where(x => !OnTime(x)).ToList();
            return new GroupPerformance(id, name, assigned, done.Count, onTime, done.Count == 0 ? 100 : (int)Math.Round(100.0 * onTime / done.Count),
                done.Count == 0 ? 0 : Math.Round(done.Average(x => DaysBetween(x.AssignedAt ?? x.CreatedAt, x.CompletedAt!.Value)), 1),
                late.Count == 0 ? 0 : Math.Round(late.Average(x => DaysBetween(x.DeadlineAt!.Value, x.CompletedAt!.Value)), 1),
                set.Count(OverdueNow), escalations.Count(e => ids.Contains(e.TaskId)),
                commitmentRows.Count(x => ids.Contains(x.TaskId) && x.Status == CommitmentStatus.Met),
                commitmentRows.Count(x => ids.Contains(x.TaskId) && x.Status == CommitmentStatus.Missed),
                reopened.Count(ids.Contains));
        }

        var depts = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var teams = await db.Teams.AsNoTracking().Select(t => new { t.Id, t.Name, Members = t.Members.Select(m => m.EmployeeId).ToList() }).ToListAsync(ct);
        var personIds = rows.Where(x => x.PrimaryAssigneeId is not null).Select(x => x.PrimaryAssigneeId!.Value).Distinct().ToList();
        var people = await db.Employees.AsNoTracking().Where(e => personIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);

        var byDept = rows.Where(x => x.DepartmentId is not null).GroupBy(x => x.DepartmentId!.Value)
            .Select(g => Perform(g.Key, depts.GetValueOrDefault(g.Key, "Unknown"), g.ToList())).OrderBy(g => g.Name).ToList();
        var byTeam = teams.Select(t => (t, set: rows.Where(x => x.TeamId == t.Id || x.AssignedTeams.Contains(t.Id)
                || (x.PrimaryAssigneeId is { } p && t.Members.Contains(p))).ToList()))
            .Where(x => x.set.Count > 0).Select(x => Perform(x.t.Id, x.t.Name, x.set)).OrderBy(g => g.Name).ToList();
        var byPerson = rows.Where(x => x.PrimaryAssigneeId is not null).GroupBy(x => x.PrimaryAssigneeId!.Value)
            .Select(g => Perform(g.Key, people.GetValueOrDefault(g.Key, "Unknown"), g.ToList()))
            .OrderByDescending(g => g.Overdue).ThenBy(g => g.Name).ToList();

        var all = Perform(Guid.Empty, "All", rows);
        var created = rows.Count(x => x.CreatedAt >= fromUtc);
        var openNow = rows.Count(x => TaskStatusRules.IsActive(x.Status));
        string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var scope = new List<(string, string)>();
        if (r.DepartmentId is { } sd) scope.Add(("departmentId", sd.ToString()));
        if (r.TeamId is { } st) scope.Add(("teamId", st.ToString()));
        Drilldown Tasks(params (string, string)[] q) => Drilldown.Tasks([.. scope, .. q]);
        var metrics = new List<MetricValue>
        {
            new("created", "Tasks delegated", created, "tasks", "Created in the period", Tasks(("sort", "number"))),
            new("completed", "Completed", all.Completed, "tasks", "Completed in the period",
                Tasks(("completedFrom", D(from)), ("completedTo", D(to)), ("sort", "completed"))),
            new("on-time", "On-time completion", all.OnTimePercent, "%", "Completed by the deadline", null),
            new("avg-completion", "Average completion time", all.AvgCompletionDays, "days", "From assignment to completion", null),
            new("avg-delay", "Average delay", all.AvgDelayDays, "days", "For work completed late", null),
            new("open", "Open now", openNow, "tasks", null, Tasks(("open", "true"))),
            new("overdue", "Overdue now", all.Overdue, "tasks", null, Tasks(("overdue", "true"), ("sort", "due"))),
            new("escalations", "Escalations", escalations.Count, "escalations", "Triggered in the period", null),
            new("commitments-met", "Commitments met", all.CommitmentsMet, "commitments", null, Drilldown.To("/commitments", ("status", "Met"), ("from", D(from)), ("to", D(to)))),
            new("commitments-missed", "Commitments missed", all.CommitmentsMissed, "commitments", null,
                Drilldown.To("/commitments", ("status", "Missed"), ("from", D(from)), ("to", D(to)))),
            new("reopened", "Reopened", all.Reopened, "tasks", "Completions sent back for more work", null),
            new("extensions", "Extension requests", extensions, "requests", "Deadline extensions requested in the period", null),
        };

        // Ageing of currently overdue work.
        var overdueNow = rows.Where(OverdueNow).Select(x => DeadlineRules.DaysOverdue(x.DueDate ?? c.Today, c.Today)).ToList();
        AgeingBucket Bucket(string label, int min, int? max) => new(label, overdueNow.Count(d => d >= min && (max is null || d <= max)),
            Tasks(("overdue", "true"), ("minDaysOverdue", min.ToString(CultureInfo.InvariantCulture)), ("sort", "due")));
        var ageing = new List<AgeingBucket> { Bucket("0–2 days", 0, 2), Bucket("3–7 days", 3, 7), Bucket("8–14 days", 8, 14), Bucket("15–30 days", 15, 30), Bucket("Over 30 days", 31, null) };

        var byLevel = escalations.GroupBy(e => e.Level).OrderBy(g => g.Key)
            .Select(g => new EscalationBreakdown(g.Key.ToString(CultureInfo.InvariantCulture), $"Level {g.Key}", g.Count())).ToList();
        var byTrigger = escalations.GroupBy(e => e.Trigger).OrderByDescending(g => g.Count())
            .Select(g => new EscalationBreakdown(g.Key.ToString(), Text.Humanize(g.Key), g.Count())).ToList();

        // Daily trend (weekly points for long ranges).
        var step = to.DayNumber - from.DayNumber > 92 ? 7 : 1;
        var trend = new List<TrendPoint>();
        for (var day = from; day <= to; day = day.AddDays(step))
        {
            var startUtc = c.Utc(day);
            var endUtc = c.Utc(day.AddDays(step));
            var cutoff = c.Utc(day.AddDays(1));
            trend.Add(new TrendPoint(day,
                rows.Count(x => x.CreatedAt >= startUtc && x.CreatedAt < endUtc),
                rows.Count(x => x.Status == WorkStatus.Completed && x.CompletedAt >= startUtc && x.CompletedAt < endUtc),
                rows.Count(x => x.DeadlineAt is { } d && d < cutoff && x.CreatedAt < cutoff && x.Status != WorkStatus.Cancelled
                    && (x.CompletedAt is null || x.CompletedAt > d && x.CompletedAt >= cutoff))));
        }

        var recurringDefs = await recurring.ListAsync(false, ct, r.TeamId);
        if (r.DepartmentId is { } rd) recurringDefs = recurringDefs.Where(d => d.Department?.Id == rd).ToList();
        var missed = await commitments.ProjectAsync(db.Commitments.AsNoTracking()
            .Where(x => scopedIds.Contains(x.TaskId) && x.Status == CommitmentStatus.Missed && x.CommitmentDate >= from && x.CommitmentDate <= to)
            .OrderByDescending(x => x.CommitmentDate).Take(100), ct);
        var noResponse = await queries.ProjectAsync(vis.NoResponseOnly().OrderBy(t => t.NoResponseSince).Take(20), c, ct);

        return new ReportDto(from, to, metrics, byDept, byTeam, byPerson, ageing, byLevel, byTrigger, trend,
            [.. recurringDefs.OrderBy(d => d.CompliancePercent)], missed, noResponse);
    }
}
