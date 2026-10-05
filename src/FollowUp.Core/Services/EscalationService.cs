using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Services;

/// <summary>
/// Escalation engine (spec §34): evaluates each open task against its escalation policy (explicit, or the
/// default for its criticality). Each ladder step fires once per cycle — the idempotency key is unique in the
/// database — so a restart, a second host or a re-run never escalates twice. Escalations to the CEO also
/// raise a CEO attention item.
/// </summary>
public sealed class EscalationService(
    FollowUpDbContext db,
    IClock clock,
    CalendarProvider calendars,
    NotificationService notifications,
    MonitoringService monitoring,
    AuditService audit,
    TaskService tasks,
    ILogger<EscalationService> logger) : IEscalationService
{
    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var horizon = now.AddDays(3);
        var policies = await db.EscalationPolicies.AsNoTracking().Include(p => p.Steps).Where(p => p.IsActive).ToListAsync(ct);
        if (policies.Count == 0) return 0;

        var missedCommitmentTasks = db.Commitments.Where(c => c.Status == CommitmentStatus.Missed).Select(c => c.TaskId);
        var ids = await db.Tasks.ActiveOnly().Where(t =>
                (t.DeadlineAt != null && t.DeadlineAt <= horizon)
                || t.NoResponseSince != null
                || t.Acknowledgement == AcknowledgementState.Pending
                || t.IsBlocked
                || missedCommitmentTasks.Contains(t.Id))
            .Select(t => t.Id).ToListAsync(ct);

        var fired = 0;
        foreach (var id in ids)
        {
            try { fired += await ProcessTaskAsync(id, policies, ct); }
            catch (DbUpdateException ex) when (Database.IsUniqueViolation(ex))
            {
                logger.LogInformation("Escalation for task {TaskId} already recorded (idempotency key)", id);
            }
            finally { db.ChangeTracker.Clear(); }
        }
        return fired;
    }

    private async Task<int> ProcessTaskAsync(Guid id, List<EscalationPolicy> policies, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var task = await db.Tasks.Include(t => t.Assignees).Include(t => t.Followers).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null || task.IsClosed) return 0;
        var policy = PolicyFor(task, policies);
        if (policy is null) return 0;

        var missed = await db.Commitments.Where(c => c.TaskId == id && c.Status == CommitmentStatus.Missed && c.ResolvedAt != null)
            .Select(c => new MissedCommitmentRef(c.Id, c.CommitmentDate, c.ResolvedAt!.Value)).ToListAsync(ct);
        var snapshot = new EscalationSnapshot
        {
            TaskId = task.Id,
            Now = now,
            DueDate = task.DueDate,
            DueTime = task.DueTime,
            IsClosed = task.IsClosed || TaskStatusRules.IsAwaitingConfirmation(task.Status),
            IsFollowUpPaused = task.FollowUpPausedUntil is { } p && p >= cal.Today(now),
            Criticality = task.Criticality,
            AssignedAt = task.AssignedAt,
            Acknowledgement = task.Acknowledgement,
            NoResponseSince = task.NoResponseSince,
            IsBlocked = task.IsBlocked,
            BlockedSince = task.BlockedSince,
            MissedCommitments = missed,
        };
        var due = EscalationEvaluator.Evaluate(snapshot, policy.Steps, cal);
        if (due.Count == 0) return 0;
        var keys = due.Select(d => d.IdempotencyKey).ToList();
        var existing = (await db.TaskEscalations.Where(e => keys.Contains(e.IdempotencyKey)).Select(e => e.IdempotencyKey).ToListAsync(ct)).ToHashSet();

        var fired = 0;
        foreach (var d in due.Where(d => !existing.Contains(d.IdempotencyKey)))
        {
            var esc = new TaskEscalation
            {
                TaskId = task.Id,
                PolicyId = policy.Id,
                StepId = d.Step.Id,
                Level = d.Step.Level,
                Trigger = d.Step.Trigger,
                IdempotencyKey = d.IdempotencyKey,
                Targets = d.Step.Targets,
                Reason = d.Superseded ? $"{d.Reason} — superseded by a higher escalation level" : d.Reason,
                TriggeredAt = now,
            };
            db.TaskEscalations.Add(esc);
            if (d.Superseded) continue;

            fired++;
            if (d.Step.Level > 1 || d.Step.Trigger is EscalationTrigger.Overdue or EscalationTrigger.NoResponse or EscalationTrigger.CommitmentMissed or EscalationTrigger.Blocked)
            {
                task.EscalationLevel = Math.Max(task.EscalationLevel, d.Step.Level);
                task.EscalationCount++;
            }
            var recipients = await ResolveTargetsAsync(db, task, d.Step.Targets, tasks, ct);
            var toCeo = d.Step.Targets.HasFlag(EscalationTarget.Ceo);
            var channels = NotifyChannels.InApp | d.Step.Channels;
            await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = d.IdempotencyKey,
                Category = toCeo ? NotificationCategory.CeoAlert : d.Step.Level <= 1 ? CategoryFor(d.Step.Trigger) : NotificationCategory.Escalation,
                Task = task,
                RecipientIds = recipients,
                Headline = toCeo ? "requires CEO attention." : d.Step.Level <= 1 ? HeadlineFor(d) : $"has been escalated (level {d.Step.Level}).",
                Message = d.Reason,
                Reason = $"Escalation policy \"{policy.Name}\", step {d.Step.Order}.",
                Channels = channels,
                Urgent = d.Step.Level >= 3 || task.Criticality >= Criticality.Critical,
                ExpiresIn = TimeSpan.FromDays(1),
            }, ct);
            audit.Record(d.Step.Level <= 1 ? "DeadlineNotice" : "Escalated", nameof(TaskEscalation), esc.Id, task.Id, "Level", null, d.Step.Level.ToString(), d.Reason);
            if (toCeo || d.Step.RaiseCeoAttention)
                monitoring.UpsertAttention(AttentionType.EscalatedToCeo, $"esc-ceo:{d.IdempotencyKey}", task,
                    $"Escalated to you: {task.Title}", $"{d.Reason}. {task.LastUpdateSummary}".Trim(), task.AttentionScore + 10);
        }
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await db.SaveChangesAsync(ct);
        return fired;
    }

    private static NotificationCategory CategoryFor(EscalationTrigger t) => t switch
    {
        EscalationTrigger.BeforeDue => NotificationCategory.DueSoon,
        EscalationTrigger.OnDue => NotificationCategory.DueToday,
        EscalationTrigger.Overdue => NotificationCategory.Overdue,
        EscalationTrigger.AcknowledgementPending => NotificationCategory.AcknowledgementRequest,
        _ => NotificationCategory.Escalation,
    };

    private static string HeadlineFor(EscalationDue d) => d.Step.Trigger switch
    {
        EscalationTrigger.BeforeDue => "is due soon.",
        EscalationTrigger.OnDue => "is due today.",
        EscalationTrigger.AcknowledgementPending => "is waiting for your acknowledgement.",
        _ => "needs attention.",
    };

    /// <summary>Explicit task policy, else the default policy for the task's criticality, else the general default.</summary>
    public static EscalationPolicy? PolicyFor(WorkTask task, IReadOnlyList<EscalationPolicy> policies)
    {
        if (task.EscalationPolicyId is { } pid && policies.FirstOrDefault(p => p.Id == pid) is { } explicitPolicy) return explicitPolicy;
        var crit = task.Criticality.ToString();
        return policies.FirstOrDefault(p => p.AppliesTo.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(crit, StringComparer.OrdinalIgnoreCase))
            ?? policies.FirstOrDefault(p => p.IsDefault);
    }

    /// <summary>Resolves escalation targets to people (spec §34: assignee, team leader, department head, CEO…).</summary>
    public static async Task<List<Guid>> ResolveTargetsAsync(FollowUpDbContext db, WorkTask task, EscalationTarget targets, TaskService tasks, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        if (targets.HasFlag(EscalationTarget.Assignee))
        {
            if (task.PrimaryAssigneeId is { } p) ids.Add(p);
            foreach (var a in task.Assignees.Where(a => a.RemovedAt == null && a.Role == AssigneeRole.Additional && a.EmployeeId is not null)) ids.Add(a.EmployeeId!.Value);
        }
        if (targets.HasFlag(EscalationTarget.TeamLeader))
        {
            var teamIds = task.Assignees.Where(a => a.TeamId is not null).Select(a => a.TeamId!.Value).ToList();
            if (task.TeamId is { } t) teamIds.Add(t);
            foreach (var l in await db.Teams.Where(x => teamIds.Contains(x.Id) && x.LeaderId != null).Select(x => x.LeaderId!.Value).ToListAsync(ct)) ids.Add(l);
        }
        if (targets.HasFlag(EscalationTarget.AccountableManager) && task.AccountableManagerId is { } am) ids.Add(am);
        if (targets.HasFlag(EscalationTarget.DepartmentHead) && task.DepartmentId is { } dep
            && await db.Departments.Where(d => d.Id == dep).Select(d => d.HeadId).FirstOrDefaultAsync(ct) is { } head) ids.Add(head);
        if (targets.HasFlag(EscalationTarget.Delegator) && (task.DelegatorId ?? task.OriginalDelegatorId) is { } del) ids.Add(del);
        if (targets.HasFlag(EscalationTarget.Ceo) && await tasks.CeoIdAsync(task, ct) is { } ceo) ids.Add(ceo);
        if (targets.HasFlag(EscalationTarget.Followers)) foreach (var f in task.Followers) ids.Add(f.EmployeeId);
        return [.. ids];
    }
}
