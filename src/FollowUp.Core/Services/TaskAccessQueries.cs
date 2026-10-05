using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

/// <summary>
/// SQL translation of <see cref="TaskAccess.CanView"/> (spec §3, §70). Every task query in the application goes
/// through <see cref="VisibleTo"/>; tests keep it in step with the domain rule.
/// </summary>
public static class TaskAccessQueries
{
    public static IQueryable<WorkTask> VisibleTo(this IQueryable<WorkTask> q, FollowUpDbContext db, Viewer v)
    {
        if (v.IsExecutive) return q;
        var me = v.EmployeeId;
        var teams = v.TeamIds.ToList();
        var led = v.LedTeamIds.ToList();
        var depts = v.ManagedDepartmentIds.ToList();
        var admin = v.IsAdmin;
        var mgmt = v.IsManagement;
        var senior = v.IsSeniorManager;

        return q.Where(t =>
            // Core participants always see the task.
            t.DelegatorId == me || t.OriginalDelegatorId == me || t.CreatedById == me || t.PrimaryAssigneeId == me
            || t.Assignees.Any(a => a.RemovedAt == null && a.EmployeeId == me)
            || (t.Visibility != TaskVisibility.CeoOnly && (
                // Other participants: followers, accountable manager, approvers.
                ((t.AccountableManagerId == me
                  || t.Followers.Any(f => f.EmployeeId == me)
                  || db.TaskApprovals.Any(ap => ap.TaskId == t.Id && ap.ApproverId == me))
                 && (t.Visibility != TaskVisibility.ManagementOnly || mgmt))
                || (t.Visibility != TaskVisibility.SelectedParticipants && (
                    (senior && t.DepartmentId != null && depts.Contains(t.DepartmentId.Value))
                    || (t.TeamId != null && led.Contains(t.TeamId.Value))
                    || t.Assignees.Any(a => a.RemovedAt == null && a.TeamId != null && led.Contains(a.TeamId.Value))
                    || (t.Visibility == TaskVisibility.TeamOnly
                        && ((t.TeamId != null && teams.Contains(t.TeamId.Value))
                            || t.Assignees.Any(a => a.RemovedAt == null && a.TeamId != null && teams.Contains(a.TeamId.Value))))
                    || (t.Visibility == TaskVisibility.Normal && admin))))));
    }

    /// <summary>Tasks where the person is the primary or an additional / supporting assignee.</summary>
    public static IQueryable<WorkTask> AssignedTo(this IQueryable<WorkTask> q, Guid employeeId) =>
        q.Where(t => t.PrimaryAssigneeId == employeeId || t.Assignees.Any(a => a.RemovedAt == null && a.EmployeeId == employeeId));

    public static IQueryable<WorkTask> OpenOnly(this IQueryable<WorkTask> q) =>
        q.Where(t => t.Status != WorkStatus.Completed && t.Status != WorkStatus.Cancelled);

    /// <summary>Active work: open, not draft, not deferred.</summary>
    public static IQueryable<WorkTask> ActiveOnly(this IQueryable<WorkTask> q) =>
        q.Where(t => t.Status != WorkStatus.Completed && t.Status != WorkStatus.Cancelled && t.Status != WorkStatus.Draft && t.Status != WorkStatus.Deferred);

    /// <summary>Overdue: deadline passed and the work is still with the assignee (not awaiting confirmation, not deferred).</summary>
    public static IQueryable<WorkTask> OverdueAt(this IQueryable<WorkTask> q, DateTime now) =>
        q.Where(t => t.DeadlineAt != null && t.DeadlineAt < now
            && t.Status != WorkStatus.Completed && t.Status != WorkStatus.Cancelled && t.Status != WorkStatus.Draft
            && t.Status != WorkStatus.Deferred && t.Status != WorkStatus.CompletedByAssignee
            && t.Status != WorkStatus.CompletionAwaitingApproval);
}

/// <summary>Projects tasks into list rows with names, counts and deadline wording.</summary>
public sealed class TaskProjector(FollowUpDbContext db)
{
    public async Task<List<TaskListItem>> ProjectAsync(IQueryable<WorkTask> q, DateTime now, BusinessCalendar cal, int severeDays, CancellationToken ct)
    {
        var rows = await q.Select(t => new
        {
            t.Id, t.Number, t.Title, t.Status, t.Priority, t.Criticality, t.Health, t.AttentionScore, t.ProgressPercent,
            t.DueDate, t.DueTime, t.DeadlineAt, t.PrimaryAssigneeId,
            AssigneeName = t.PrimaryAssignee != null ? t.PrimaryAssignee.DisplayName : null,
            AssigneeTitle = t.PrimaryAssignee != null ? t.PrimaryAssignee.JobTitle : null,
            t.DelegatorId,
            DelegatorName = db.Employees.Where(e => e.Id == t.DelegatorId).Select(e => e.DisplayName).FirstOrDefault(),
            TeamName = db.Teams.Where(x => x.Id == t.TeamId).Select(x => x.Name).FirstOrDefault(),
            DepartmentName = db.Departments.Where(x => x.Id == t.DepartmentId).Select(x => x.Name).FirstOrDefault(),
            ProjectName = db.Projects.Where(x => x.Id == t.ProjectId).Select(x => x.Name).FirstOrDefault(),
            t.ParentTaskId,
            ParentNumber = t.ParentTask != null ? t.ParentTask.Number : null,
            t.IsBlocked, t.BlockedReason, t.NoResponseSince, t.LastActivityAt, t.NextFollowUpAt,
            IsRecurring = t.RecurringDefinitionId != null,
            SubtaskCount = t.Subtasks.Count(),
            OpenSubtaskCount = t.Subtasks.Count(s => s.Status != WorkStatus.Completed && s.Status != WorkStatus.Cancelled),
            t.EscalationLevel, t.Visibility,
            OpenCommitmentDate = db.Commitments.Where(c => c.TaskId == t.Id && c.Status == CommitmentStatus.Open)
                .OrderBy(c => c.CommitmentDate).Select(c => (DateOnly?)c.CommitmentDate).FirstOrDefault(),
            MissedCommitments = db.Commitments.Count(c => c.TaskId == t.Id && c.Status == CommitmentStatus.Missed),
            t.LastUpdateSummary, t.CompletedAt,
            Tags = t.Tags.Select(x => x.Tag!.Name).ToList(),
        }).ToListAsync(ct);

        return rows.Select(r =>
        {
            var overdue = DeadlineRules.IsOverdue(r.DueDate, r.DueTime, r.Status, now, cal);
            var today = cal.Today(now);
            return new TaskListItem
            {
                Id = r.Id,
                Number = r.Number,
                Title = r.Title,
                Status = r.Status,
                DisplayStatus = overdue ? "Overdue" : Text.Humanize(r.Status),
                Priority = r.Priority,
                Criticality = r.Criticality,
                Health = r.Health,
                AttentionScore = r.AttentionScore,
                ProgressPercent = r.ProgressPercent,
                DueDate = r.DueDate,
                DeadlineText = DeadlineRules.Describe(r.DueDate, r.DueTime, r.Status, now, cal),
                Bucket = DeadlineRules.Bucket(r.DueDate, r.DueTime, r.Status, now, cal, severeDays),
                IsOverdue = overdue,
                DaysOverdue = overdue && r.DueDate is { } d ? DeadlineRules.DaysOverdue(d, today) : 0,
                PrimaryAssignee = r.PrimaryAssigneeId is { } aid ? new PersonRef(aid, r.AssigneeName ?? "", r.AssigneeTitle) : null,
                Delegator = r.DelegatorId is { } did ? new PersonRef(did, r.DelegatorName ?? "") : null,
                TeamName = r.TeamName,
                DepartmentName = r.DepartmentName,
                ProjectName = r.ProjectName,
                ParentTaskId = r.ParentTaskId,
                ParentNumber = r.ParentNumber,
                IsBlocked = r.IsBlocked || r.Status == WorkStatus.Blocked,
                BlockedReason = r.BlockedReason,
                NoResponseDays = r.NoResponseSince is { } nr ? Math.Max(0, (int)Math.Floor((now - nr).TotalDays)) : 0,
                LastActivityAt = r.LastActivityAt,
                NextFollowUpAt = r.NextFollowUpAt,
                IsRecurring = r.IsRecurring,
                SubtaskCount = r.SubtaskCount,
                OpenSubtaskCount = r.OpenSubtaskCount,
                EscalationLevel = r.EscalationLevel,
                Visibility = r.Visibility,
                OpenCommitmentDate = r.OpenCommitmentDate,
                MissedCommitments = r.MissedCommitments,
                LastUpdateSummary = r.LastUpdateSummary,
                CompletedAt = r.CompletedAt,
                Tags = r.Tags,
            };
        }).ToList();
    }
}
