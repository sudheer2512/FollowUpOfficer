using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

/// <summary>
/// "Now" for a query: the UTC instant, the organisation's business calendar and the local business date
/// (spec §73). Every dashboard / filter computes buckets from the same snapshot so numbers agree.
/// </summary>
public sealed record QueryClock(DateTime Now, BusinessCalendar Calendar, DateOnly Today, int SevereDays)
{
    public DateOnly WeekStart => BusinessCalendar.StartOfWeek(Today);
    public DateOnly WeekEnd => WeekStart.AddDays(6);

    /// <summary>Local midnight of <paramref name="d"/> as a UTC instant (for CompletedAt / CreatedAt ranges).</summary>
    public DateTime Utc(DateOnly d) => Calendar.ToUtc(d, TimeOnly.MinValue);

    public DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(Calendar.ToLocal(utc));

    public static async Task<QueryClock> CreateAsync(IClock clock, CalendarProvider calendars, int severeDays, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        return new QueryClock(now, cal, cal.Today(now), Math.Max(1, severeDays));
    }
}

/// <summary>
/// Reusable SQL-translatable task predicates shared by the task list filter, dashboards, digest and Ask.
/// Overdue is never stored — it is derived from <see cref="WorkTask.DeadlineAt"/> (spec §7, §17).
/// </summary>
public static class TaskMetrics
{
    /// <summary>Active work that is still with the assignee (not draft, deferred, closed or awaiting confirmation).</summary>
    public static IQueryable<WorkTask> InProgressWork(this IQueryable<WorkTask> q) =>
        q.ActiveOnly().Where(t => t.Status != WorkStatus.CompletedByAssignee && t.Status != WorkStatus.CompletionAwaitingApproval);

    public static IQueryable<WorkTask> NotPastDeadline(this IQueryable<WorkTask> q, DateTime now) =>
        q.Where(t => t.DeadlineAt == null || t.DeadlineAt >= now);

    /// <summary>Work due between two local dates (inclusive) whose deadline has not passed yet.</summary>
    public static IQueryable<WorkTask> DueBetween(this IQueryable<WorkTask> q, QueryClock c, DateOnly from, DateOnly to) =>
        q.InProgressWork().NotPastDeadline(c.Now).Where(t => t.DueDate != null && t.DueDate >= from && t.DueDate <= to);

    public static IQueryable<WorkTask> DueToday(this IQueryable<WorkTask> q, QueryClock c) => q.DueBetween(c, c.Today, c.Today);

    /// <summary>Due from today to the end of the (Monday-based) week, not yet overdue.</summary>
    public static IQueryable<WorkTask> DueThisWeek(this IQueryable<WorkTask> q, QueryClock c) => q.DueBetween(c, c.Today, c.WeekEnd);

    /// <summary>Overdue by at least <paramref name="minDays"/> calendar days.</summary>
    public static IQueryable<WorkTask> OverdueBy(this IQueryable<WorkTask> q, QueryClock c, int minDays)
    {
        var cutoff = c.Today.AddDays(-Math.Max(0, minDays));
        return q.OverdueAt(c.Now).Where(t => t.DueDate <= cutoff);
    }

    public static IQueryable<WorkTask> SeverelyOverdue(this IQueryable<WorkTask> q, QueryClock c) => q.OverdueBy(c, c.SevereDays);

    /// <summary>Overdue but not yet severely overdue.</summary>
    public static IQueryable<WorkTask> MildlyOverdue(this IQueryable<WorkTask> q, QueryClock c)
    {
        var cutoff = c.Today.AddDays(-c.SevereDays);
        return q.OverdueAt(c.Now).Where(t => t.DueDate > cutoff);
    }

    public static IQueryable<WorkTask> BlockedOnly(this IQueryable<WorkTask> q) =>
        q.OpenOnly().Where(t => t.IsBlocked || t.Status == WorkStatus.Blocked);

    public static IQueryable<WorkTask> NoResponseOnly(this IQueryable<WorkTask> q) =>
        q.OpenOnly().Where(t => t.NoResponseSince != null);

    public static IQueryable<WorkTask> AwaitingConfirmation(this IQueryable<WorkTask> q) =>
        q.Where(t => t.Status == WorkStatus.CompletedByAssignee || t.Status == WorkStatus.CompletionAwaitingApproval);

    public static IQueryable<WorkTask> CriticalOnly(this IQueryable<WorkTask> q, Criticality min = Criticality.Critical) =>
        q.Where(t => t.Criticality >= min);

    public static IQueryable<WorkTask> CompletedBetween(this IQueryable<WorkTask> q, DateTime fromUtc, DateTime toUtc) =>
        q.Where(t => t.Status == WorkStatus.Completed && t.CompletedAt != null && t.CompletedAt >= fromUtc && t.CompletedAt < toUtc);

    /// <summary>
    /// A team's tasks: owned by the team, assigned to it as a whole, or with a team member (leader and deputy are
    /// member rows) as the primary responsible person.
    /// </summary>
    public static IQueryable<WorkTask> ForTeam(this IQueryable<WorkTask> q, FollowUpDbContext db, Guid teamId)
    {
        var members = db.TeamMembers.Where(m => m.TeamId == teamId).Select(m => m.EmployeeId);
        return q.Where(t => t.TeamId == teamId || t.Assignees.Any(a => a.RemovedAt == null && a.TeamId == teamId)
            || (t.PrimaryAssigneeId != null && members.Contains(t.PrimaryAssigneeId.Value)));
    }

    /// <summary>
    /// Tasks waiting for a decision by one of <paramref name="approverIds"/> (spec §38 "Awaiting CEO"): the current
    /// stage of the completion approval, a pending extension request, or an acknowledgement that declined / asked
    /// for clarification and went back to an executive delegator.
    /// </summary>
    public static IQueryable<WorkTask> WaitingFor(this IQueryable<WorkTask> q, FollowUpDbContext db, IReadOnlyCollection<Guid> approverIds)
    {
        var ids = approverIds.ToList();
        return q.OpenOnly().Where(t =>
            db.TaskApprovals.Any(a => a.TaskId == t.Id && a.Round == t.CompletionRound && a.Decision == ApprovalDecision.Pending && ids.Contains(a.ApproverId)
                && !db.TaskApprovals.Any(b => b.TaskId == t.Id && b.Round == t.CompletionRound && b.Decision == ApprovalDecision.Pending && b.Stage < a.Stage))
            || db.ExtensionRequests.Any(x => x.TaskId == t.Id && x.Status == ExtensionStatus.Pending && x.ApproverId != null && ids.Contains(x.ApproverId.Value))
            || ((t.Acknowledgement == AcknowledgementState.Declined || t.Acknowledgement == AcknowledgementState.ClarificationRequested)
                && t.DelegatorId != null && ids.Contains(t.DelegatorId.Value)));
    }

    /// <summary>Case-insensitive LIKE pattern (both SQL Server and SQLite) with wildcards escaped.</summary>
    public static string Like(string text) =>
        "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";

    public const string LikeEscape = "\\";

    /// <summary>Severity used to rank overdue work: days overdue × criticality weight.</summary>
    public static int Severity(int daysOverdue, Criticality c) => Math.Max(1, daysOverdue) * ((int)c + 1);

    public static int CriticalityWeight(Criticality c) => (int)c + 1;

    /// <summary>Projects the viewer may see (spec §46; same visibility levels as tasks).</summary>
    public static IQueryable<Project> VisibleProjects(this IQueryable<Project> q, Viewer v)
    {
        if (v.IsExecutive) return q;
        var me = v.EmployeeId;
        var mgmt = v.IsManagement;
        var depts = v.ManagedDepartmentIds.ToList();
        return q.Where(p => p.OwnerId == me
            || p.Visibility == TaskVisibility.Normal || p.Visibility == TaskVisibility.TeamOnly
            || (p.Visibility == TaskVisibility.ManagementOnly && (mgmt || (p.DepartmentId != null && depts.Contains(p.DepartmentId.Value)))));
    }

    /// <summary>Human list "A, B and C".</summary>
    public static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    public static string Plural(int n, string singular, string? plural = null) => n == 1 ? singular : plural ?? singular + "s";
}

/// <summary>Executive employee ids (enabled accounts with the Executive role).</summary>
public static class Executives
{
    public static async Task<List<Guid>> IdsAsync(FollowUpDbContext db, CancellationToken ct) =>
        await db.UserAccounts.AsNoTracking().Where(a => (a.Roles & AppRole.Executive) == AppRole.Executive && a.IsEnabled)
            .Select(a => a.EmployeeId).ToListAsync(ct);
}
