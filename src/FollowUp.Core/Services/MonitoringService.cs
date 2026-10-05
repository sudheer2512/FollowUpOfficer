using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>
/// Continuous monitoring (spec §35–§37, §39): recomputes deadline state, blocked state, health and the
/// executive Attention Score for a task, and keeps the CEO "Attention Required" queue in step with the
/// task's conditions — items appear when intervention is needed and resolve themselves when it no longer is.
/// </summary>
public sealed class MonitoringService(FollowUpDbContext db, IClock clock, CalendarProvider calendars, IOptions<FollowUpOptions> options)
{
    private readonly FollowUpOptions _o = options.Value;
    private HashSet<Guid>? _executives;

    /// <summary>Attention types whose items are derived from the task's current state (auto-resolved when the condition clears).</summary>
    private static readonly HashSet<AttentionType> ConditionTypes =
    [
        AttentionType.CriticalOverdue, AttentionType.RepeatedNonResponse, AttentionType.Blocked, AttentionType.AcknowledgementPending,
        AttentionType.AcknowledgementDeclined, AttentionType.ApprovalRequired, AttentionType.ExtensionRequest, AttentionType.AiSuggestion,
        AttentionType.DepartmentConflict,
    ];

    public async Task<HashSet<Guid>> ExecutivesAsync(CancellationToken ct) =>
        _executives ??= (await db.UserAccounts.Where(a => (a.Roles & AppRole.Executive) == AppRole.Executive && a.IsEnabled)
            .Select(a => a.EmployeeId).ToListAsync(ct)).ToHashSet();

    /// <summary>Recalculates the task's monitoring state and attention items (tracked entities; caller saves).</summary>
    public async Task RefreshAsync(WorkTask task, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var today = cal.Today(now);
        var execs = await ExecutivesAsync(ct);

        task.DeadlineAt = task.DueDate is { } due ? cal.DeadlineUtc(due, task.DueTime) : null;
        var overdue = DeadlineRules.IsOverdue(task.DueDate, task.DueTime, task.Status, now, cal);
        var daysOverdue = overdue && task.DueDate is { } d1 ? DeadlineRules.DaysOverdue(d1, today) : 0;
        int? daysUntilDue = !overdue && task.DueDate is { } d2 ? d2.DayNumber - today.DayNumber : null;

        // Dependencies: finish-to-start waits for completion, start-to-start for a start, external until resolved.
        // Tracked + pending rows are used so a refresh inside a unit of work sees its own unsaved changes.
        var depRows = (await WithLocalAsync(db.TaskDependencies.Where(x => x.TaskId == task.Id), x => x.TaskId == task.Id, ct))
            .Where(x => !x.IsResolved && db.Entry(x).State != EntityState.Deleted).ToList();
        var predecessorIds = depRows.Where(x => x.DependsOnTaskId is not null).Select(x => x.DependsOnTaskId!.Value).ToList();
        var predecessors = await db.Tasks.Where(t => predecessorIds.Contains(t.Id)).Select(t => new { t.Id, t.Number, t.Status, t.DepartmentId }).ToListAsync(ct);
        var deps = depRows.Select(x =>
        {
            var p = predecessors.FirstOrDefault(t => t.Id == x.DependsOnTaskId);
            var localStatus = x.DependsOnTaskId is { } pid ? db.Tasks.Local.FirstOrDefault(t => t.Id == pid)?.Status : null;
            return new
            {
                x.Id, x.Type, x.ExternalDescription, x.ExternalParty, x.CreatedAt, x.DependsOnTaskId,
                Number = p?.Number, Status = localStatus ?? p?.Status, DepartmentId = p?.DepartmentId,
            };
        }).ToList();
        var blocking = deps.Where(x => x.DependsOnTaskId is null
            || (x.Type == DependencyType.StartToStart
                ? x.Status is WorkStatus.Draft or WorkStatus.Assigned or WorkStatus.AcknowledgementPending or WorkStatus.Accepted
                : x.Status is { } s && !TaskStatusRules.IsClosed(s))).ToList();
        var statusWaiting = TaskStatusRules.IsWaiting(task.Status);
        var wasBlocked = task.IsBlocked;
        if (task.IsOpen && (blocking.Count > 0 || statusWaiting))
        {
            task.IsBlocked = true;
            task.BlockedSince ??= now;
            if (blocking.Count > 0)
                task.BlockedReason = string.Join("; ", blocking.Select(b => b.Number is not null ? $"Blocked by {b.Number}" : $"Waiting for {b.ExternalDescription}{(b.ExternalParty is null ? "" : $" ({b.ExternalParty})")}"));
        }
        else
        {
            task.IsBlocked = false;
            task.BlockedSince = null;
            if (wasBlocked) task.BlockedReason = null;
        }

        var commitments = await WithLocalAsync(db.Commitments.Where(c => c.TaskId == task.Id), c => c.TaskId == task.Id, ct);
        var missedCommitments = commitments.Count(c => c.Status == CommitmentStatus.Missed);
        var dependents = await db.TaskDependencies.Where(x => x.DependsOnTaskId == task.Id)
            .Join(db.Tasks, x => x.TaskId, t => t.Id, (x, t) => t.Status)
            .CountAsync(st => st != WorkStatus.Completed && st != WorkStatus.Cancelled, ct);
        var pendingApprovals = (await WithLocalAsync(db.TaskApprovals.Where(a => a.TaskId == task.Id && a.Round == task.CompletionRound), a => a.TaskId == task.Id, ct))
            .Where(a => a.Round == task.CompletionRound && a.Decision == ApprovalDecision.Pending).ToList();
        var currentApproval = pendingApprovals.OrderBy(a => a.Stage).FirstOrDefault();
        var pendingExtensions = (await WithLocalAsync(db.ExtensionRequests.Where(x => x.TaskId == task.Id), x => x.TaskId == task.Id, ct))
            .Where(x => x.Status == ExtensionStatus.Pending).ToList();
        var awaitingCeo = (currentApproval is not null && execs.Contains(currentApproval.ApproverId))
            || pendingExtensions.Any(x => x.ApproverId is { } a && execs.Contains(a))
            || (task.Acknowledgement is AcknowledgementState.Declined or AcknowledgementState.ClarificationRequested && task.DelegatorId is { } del && execs.Contains(del));

        var signals = new TaskSignals
        {
            Now = now,
            Today = today,
            Status = task.Status,
            Criticality = task.Criticality,
            Priority = task.Priority,
            ProgressPercent = task.ProgressPercent,
            StartDate = task.StartDate ?? (task.AssignedAt is { } aa ? DateOnly.FromDateTime(cal.ToLocal(aa)) : null),
            DueDate = task.DueDate,
            IsOverdue = overdue,
            DaysOverdue = daysOverdue,
            DaysUntilDue = daysUntilDue,
            SeverelyOverdueDays = _o.SeverelyOverdueDays,
            LastActivityAt = task.LastActivityAt,
            NoResponseSince = task.NoResponseSince,
            RemindersSinceResponse = task.RemindersSinceResponse,
            MissedCommitments = missedCommitments,
            IsBlocked = task.IsBlocked,
            BlockedDays = task.BlockedSince is { } bs ? (int)(now - bs).TotalDays : 0,
            PendingDependencies = blocking.Count,
            DependentTasks = dependents,
            EscalationLevel = task.EscalationLevel,
            EscalationCount = task.EscalationCount,
            CustomerImpact = task.CustomerImpact,
            FinancialImpact = task.FinancialImpact,
            AwaitingCeoDecision = awaitingCeo,
            Acknowledgement = task.Acknowledgement,
            AcknowledgementPendingHours = task.AssignedAt is { } at ? (now - at).TotalHours : 0,
        };
        var health = TaskScoring.Health(signals);
        var attention = TaskScoring.Attention(signals, health.Health);
        task.Health = health.Health;
        task.AttentionScore = attention.Score;
        task.AttentionFactors = [.. health.Factors.Select(f => f with { Code = "health:" + f.Code }), .. attention.Factors];

        await SyncAttentionAsync(task, signals, overdue, daysOverdue, currentApproval, pendingExtensions, blocking.Select(b => (b.DependsOnTaskId, b.DepartmentId, b.Number, b.CreatedAt)).ToList(),
            commitments, execs, now, ct);
    }

    /// <summary>Rows from the database plus rows added in the current unit of work (tracked instances reflect unsaved edits).</summary>
    private async Task<List<T>> WithLocalAsync<T>(IQueryable<T> query, Func<T, bool> localFilter, CancellationToken ct) where T : class
    {
        var list = await query.ToListAsync(ct);
        foreach (var e in db.Set<T>().Local.Where(localFilter))
            if (!list.Contains(e)) list.Add(e);
        return list;
    }

    private async Task SyncAttentionAsync(WorkTask task, TaskSignals s, bool overdue, int daysOverdue, TaskApproval? currentApproval,
        List<ExtensionRequest> pendingExtensions, List<(Guid? TaskId, Guid? DepartmentId, string? Number, DateTime CreatedAt)> blocking,
        List<Commitment> commitments, HashSet<Guid> execs, DateTime now, CancellationToken ct)
    {
        var active = new HashSet<string>();
        if (task.IsOpen)
        {
            var important = task.Criticality >= Criticality.Important;
            if (overdue && task.DueDate is { } due && (task.Criticality >= Criticality.Critical || task.NotifyCeoOnDelay || (important && daysOverdue >= _o.SeverelyOverdueDays)))
                active.Add(UpsertAttention(AttentionType.CriticalOverdue, $"overdue:{task.Id:N}:{due:yyyyMMdd}", task,
                    task.NotifyCeoOnDelay && task.Criticality < Criticality.Critical ? $"Delayed: {task.Title}" : $"{Text.Humanize(task.Criticality)} task overdue: {task.Title}",
                    $"Overdue by {Text.Days(daysOverdue)} (due {due:d MMM}).{(task.LastUpdateSummary is { } u ? $" Last update: {u}" : "")}", task.AttentionScore + 10));

            if (task.RemindersSinceResponse >= _o.RepeatedNonResponseReminders && important && task.NoResponseSince is { } nr)
                active.Add(UpsertAttention(AttentionType.RepeatedNonResponse, $"noresp:{task.Id:N}:{nr.Ticks}", task,
                    $"Repeated non-response: {task.Title}",
                    $"{task.RemindersSinceResponse} reminders unanswered; no response for {Text.Days(TaskScoring.NoResponseDays(s))}.", task.AttentionScore + 8));

            if (task.IsBlocked && task.BlockedSince is { } since
                && (task.Criticality >= Criticality.Critical && now - since >= TimeSpan.FromHours(4) || important && now - since >= TimeSpan.FromDays(3)))
                active.Add(UpsertAttention(AttentionType.Blocked, $"blocked:{task.Id:N}:{since.Ticks}", task,
                    $"Blocked: {task.Title}", $"{task.BlockedReason ?? "Blocked"} — since {since:d MMM}.", task.AttentionScore + 6));

            if (task.Acknowledgement == AcknowledgementState.Pending && important && task.AssignedAt is { } assigned
                && now - assigned >= TimeSpan.FromHours(_o.AcknowledgementAttentionHours))
                active.Add(UpsertAttention(AttentionType.AcknowledgementPending, $"ack:{task.Id:N}:{assigned.Ticks}", task,
                    $"Not acknowledged: {task.Title}", $"Assigned {Text.Ago(now - assigned)} and still not acknowledged.", task.AttentionScore + 4));

            if (task.Acknowledgement is AcknowledgementState.Declined or AcknowledgementState.ClarificationRequested
                && task.DelegatorId is { } del && execs.Contains(del) && task.AcknowledgedAt is { } ackAt)
                active.Add(UpsertAttention(AttentionType.AcknowledgementDeclined, $"declined:{task.Id:N}:{ackAt.Ticks}", task,
                    task.Acknowledgement == AcknowledgementState.Declined ? $"Could not accept: {task.Title}" : $"Clarification needed: {task.Title}",
                    task.AcknowledgementComment ?? "The assignee responded to the delegation.", task.AttentionScore + 6));

            if (currentApproval is not null && execs.Contains(currentApproval.ApproverId))
                active.Add(UpsertAttention(AttentionType.ApprovalRequired, $"approval:{currentApproval.Id:N}", task,
                    $"Completion awaiting your confirmation: {task.Title}", "Confirm, reopen or request more work.", task.AttentionScore + 5,
                    a => a.ApprovalId = currentApproval.Id));

            foreach (var ext in pendingExtensions.Where(x => x.ApproverId is { } a && execs.Contains(a) || task.Criticality >= Criticality.Critical))
                active.Add(UpsertAttention(AttentionType.ExtensionRequest, $"ext:{ext.Id:N}", task,
                    $"Deadline extension requested: {task.Title}",
                    $"{(ext.CurrentDueDate is { } cd ? $"From {cd:d MMM} " : "")}to {ext.ProposedDueDate:d MMM}: {ext.Reason}", task.AttentionScore + 5,
                    a => a.ExtensionRequestId = ext.Id));

            if (important && task.DelegatorId is { } d2 && execs.Contains(d2))
            {
                var suggestions = (await WithLocalAsync(db.TaskSuggestions.Where(x => x.TaskId == task.Id), x => x.TaskId == task.Id, ct))
                    .Where(x => x.Status == SuggestionStatus.Pending && x.Kind == SuggestionKind.MarkCompletedByAssignee);
                foreach (var sug in suggestions)
                    active.Add(UpsertAttention(AttentionType.AiSuggestion, $"sugg:{sug.Id:N}", task,
                        $"Completion reported: {task.Title}", $"{sug.Explanation} ({sug.Confidence:P0} confidence) — confirm before closing.", task.AttentionScore,
                        a => { a.SuggestionId = sug.Id; a.MessageId = sug.MessageId; }));
            }

            foreach (var b in blocking.Where(b => b.TaskId is not null && b.DepartmentId is not null && b.DepartmentId != task.DepartmentId && now - b.CreatedAt >= TimeSpan.FromDays(2)))
                if (important)
                    active.Add(UpsertAttention(AttentionType.DepartmentConflict, $"conflict:{task.Id:N}:{b.TaskId:N}", task,
                        $"Cross-department dependency stuck: {task.Title}", $"Waiting on {b.Number} owned by another department for {Text.Days((int)(now - b.CreatedAt).TotalDays)}.",
                        task.AttentionScore + 4));
        }

        // Missed commitments are event-based but derived from the commitment row (resolved when the task closes).
        if (task.IsOpen && (task.Criticality >= Criticality.Important || task.NotifyCeoOnDelay))
        {
            var cutoff = now.AddDays(-14);
            var missedRows = commitments.Where(c => c.Status == CommitmentStatus.Missed && c.ResolvedAt >= cutoff).ToList();
            var whoIds = missedRows.Where(c => c.EmployeeId is not null).Select(c => c.EmployeeId!.Value).ToList();
            var names = await db.Employees.Where(e => whoIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
            var missed = missedRows.Select(c => new { c.Id, c.CommitmentDate, c.OriginalText, Who = c.EmployeeId is { } w && names.TryGetValue(w, out var n) ? n : null });
            foreach (var m in missed)
                active.Add(UpsertAttention(AttentionType.CommitmentMissed, $"commit:{m.Id:N}", task, $"Commitment missed: {task.Title}",
                    $"{m.Who ?? "The team"} committed to {m.CommitmentDate:ddd d MMM}: \"{NotificationService.Shorten(m.OriginalText, 160)}\"", task.AttentionScore + 6,
                    a => a.CommitmentId = m.Id));
        }

        // Resolve condition items that no longer apply (and everything once the task is closed).
        var open = await db.AttentionItems.Where(a => a.TaskId == task.Id && (a.Status == AttentionStatus.Open || a.Status == AttentionStatus.Snoozed)).ToListAsync(ct);
        foreach (var item in open.Where(i => !active.Contains(i.DedupeKey) && (!task.IsOpen || ConditionTypes.Contains(i.Type) || i.Type == AttentionType.CommitmentMissed)))
        {
            item.Status = AttentionStatus.Resolved;
            item.ResolvedAt = now;
            item.Resolution = task.IsOpen ? "Condition cleared" : $"Task {Text.Humanize(task.Status).ToLowerInvariant()}";
            item.UpdatedAt = now;
        }
    }

    /// <summary>Creates or refreshes an attention item by dedupe key. Dismissed/resolved items are never reopened for the same key.</summary>
    public string UpsertAttention(AttentionType type, string key, WorkTask? task, string title, string detail, int score, Action<AttentionItem>? configure = null)
    {
        var now = clock.UtcNow;
        var item = db.AttentionItems.Local.FirstOrDefault(a => a.DedupeKey == key) ?? db.AttentionItems.FirstOrDefault(a => a.DedupeKey == key);
        if (item is null)
        {
            item = new AttentionItem
            {
                Type = type,
                DedupeKey = key,
                TaskId = task?.Id,
                Title = NotificationService.Shorten(title, 400),
                Detail = NotificationService.Shorten(detail, 2000),
                Score = Math.Min(100, score),
                CreatedAt = now,
                UpdatedAt = now,
            };
            configure?.Invoke(item);
            db.AttentionItems.Add(item);
            UiEvents.Publish(db, clock, UiEvents.AttentionChanged, task?.Id);
        }
        else if (item.Status is AttentionStatus.Open or AttentionStatus.Snoozed)
        {
            var newScore = Math.Min(100, score);
            if (item.Score != newScore || item.Detail != detail)
            {
                item.Score = newScore;
                item.Title = NotificationService.Shorten(title, 400);
                item.Detail = NotificationService.Shorten(detail, 2000);
                item.UpdatedAt = now;
            }
            if (item.Status == AttentionStatus.Snoozed && item.SnoozedUntil is { } until && until <= now)
            {
                item.Status = AttentionStatus.Open;
                item.UpdatedAt = now;
            }
        }
        return key;
    }

    /// <summary>Full sweep (job "attention.refresh"): refresh every open task and wake snoozed items.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var ids = await db.Tasks.OpenOnly().Select(t => t.Id).ToListAsync(ct);
        var processed = 0;
        foreach (var chunk in ids.Chunk(50))
        {
            var tasks = await db.Tasks.Where(t => chunk.Contains(t.Id)).ToListAsync(ct);
            foreach (var t in tasks) await RefreshAsync(t, ct);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            processed += tasks.Count;
        }

        // Closed tasks: resolve anything still open.
        var now = clock.UtcNow;
        var stale = await db.AttentionItems.Where(a => (a.Status == AttentionStatus.Open || a.Status == AttentionStatus.Snoozed) && a.TaskId != null
            && (a.Task!.Status == WorkStatus.Completed || a.Task.Status == WorkStatus.Cancelled)).ToListAsync(ct);
        foreach (var a in stale) { a.Status = AttentionStatus.Resolved; a.ResolvedAt = now; a.Resolution = "Task closed"; a.UpdatedAt = now; }
        var wake = await db.AttentionItems.Where(a => a.Status == AttentionStatus.Snoozed && a.SnoozedUntil <= now).ToListAsync(ct);
        foreach (var a in wake) { a.Status = AttentionStatus.Open; a.UpdatedAt = now; }
        if (stale.Count + wake.Count > 0) UiEvents.Publish(db, clock, UiEvents.AttentionChanged);
        await db.SaveChangesAsync(ct);
        return processed;
    }
}
