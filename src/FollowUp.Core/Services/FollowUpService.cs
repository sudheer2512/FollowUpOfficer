using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>
/// Intelligent follow-up (spec §9, §10, §35, §62). For each task whose follow-up point is due it decides —
/// using recent communication, commitments, blockers, dependencies and deadlines — whether a reminder is
/// needed, and records the decision. Each point is processed exactly once (unique (task, scheduled-at)) and
/// points missed during downtime are coalesced into a single catch-up reminder (no notification floods).
/// </summary>
public sealed class FollowUpService(
    FollowUpDbContext db,
    IClock clock,
    CalendarProvider calendars,
    NotificationService notifications,
    MonitoringService monitoring,
    AuditService audit,
    IOptions<FollowUpOptions> options,
    ILogger<FollowUpService> logger) : IReminderService
{
    private readonly FollowUpOptions _o = options.Value;

    public async Task<int> ProcessDueFollowUpsAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var dueIds = await db.Tasks.OpenOnly().Where(t => t.NextFollowUpAt != null && t.NextFollowUpAt <= now)
            .OrderBy(t => t.NextFollowUpAt).Select(t => t.Id).Take(200).ToListAsync(ct);
        var processed = 0;
        foreach (var id in dueIds)
        {
            try
            {
                if (await ProcessOneAsync(id, ct)) processed++;
            }
            catch (DbUpdateException ex) when (Database.IsUniqueViolation(ex))
            {
                // Another host processed this follow-up point first — the unique index made it exactly-once.
                logger.LogInformation("Follow-up point for task {TaskId} already processed elsewhere", id);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Task {TaskId} changed while processing follow-up; will retry next run", id);
            }
            finally { db.ChangeTracker.Clear(); }
        }
        return processed;
    }

    private async Task<bool> ProcessOneAsync(Guid taskId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var task = await db.Tasks.Include(t => t.Assignees).FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task?.NextFollowUpAt is not { } scheduled || scheduled > now) return false;
        var anchor = task.StartDate ?? (task.AssignedAt is { } aa ? DateOnly.FromDateTime(cal.ToLocal(aa)) : cal.Today(now));

        // Quiet hours: a non-critical point reached outside working hours (e.g. catching up at night after a
        // restart) waits for the next working morning instead of reaching people at 2 AM.
        var local = cal.ToLocal(now);
        var inHours = cal.IsWorkingDay(DateOnly.FromDateTime(local)) && TimeOnly.FromDateTime(local) >= cal.WorkdayStart && TimeOnly.FromDateTime(local) <= cal.WorkdayEnd;
        if (!inHours && task.Criticality < Criticality.Critical)
        {
            var nextDay = TimeOnly.FromDateTime(local) < cal.WorkdayStart && cal.IsWorkingDay(DateOnly.FromDateTime(local))
                ? DateOnly.FromDateTime(local) : cal.NextWorkingDay(DateOnly.FromDateTime(local), inclusive: false);
            var morning = cal.ToUtc(nextDay, cal.WorkdayStart.AddMinutes(30));
            if (morning > scheduled && now - scheduled < TimeSpan.FromDays(3))
            {
                task.NextFollowUpAt = morning;
                await db.SaveChangesAsync(ct);
                return false;
            }
        }

        // Downtime catch-up: coalesce all points missed since the scheduled one into this single evaluation.
        var coalesced = 0;
        if (now - scheduled > TimeSpan.FromMinutes(_o.FollowUpCatchUpGraceMinutes))
            coalesced = FollowUpScheduler.CountPoints(task.FollowUp, scheduled, now, anchor, cal);

        var ctx = await BuildContextAsync(task, now, cal, ct);
        var decision = FollowUpDecisionEngine.Decide(ctx);
        var log = new TaskFollowUpLog
        {
            TaskId = task.Id,
            ScheduledAt = scheduled,
            ProcessedAt = now,
            Action = decision.Action,
            Kind = decision.Kind,
            Reason = decision.Reason + (coalesced > 0 ? $" (caught up {coalesced} missed point(s) after downtime)" : ""),
            CoalescedMissedPoints = coalesced,
        };

        if (decision.Action == FollowUpAction.Remind)
        {
            var recipients = await RecipientsAsync(task, decision, ct);
            var capped = new List<Guid>();
            foreach (var r in recipients)
                if (await notifications.RemindersTodayAsync(r, ct) < _o.MaxRemindersPerRecipientPerDay) capped.Add(r);
            if (capped.Count == 0)
            {
                log.Action = FollowUpAction.Skip;
                log.Reason = $"Daily reminder limit reached — {decision.Reason}";
            }
            else
            {
                var urgent = decision.Tone == "urgent";
                log.NotificationCount = await notifications.EnqueueAsync(new NoticeRequest
                {
                    Key = $"fu:{task.Id:N}:{scheduled:yyyyMMddHHmm}",
                    Category = CategoryFor(decision.Kind),
                    Task = task,
                    RecipientIds = capped,
                    Headline = HeadlineFor(decision, task),
                    Message = MessageFor(decision),
                    Reason = $"Follow-up schedule: {task.FollowUp.Describe()}.",
                    Urgent = urgent,
                    SupersedeKey = $"rem:{task.Id:N}",
                    ExpiresIn = TimeSpan.FromHours(_o.ReminderExpiryHours),
                }, ct);

                // Team-level reminder in the team's Teams channel when the task is assigned to a team and overdue.
                if (decision.Kind == FollowUpKind.Overdue && task.TeamId is { } tid && task.Assignees.Any(a => a.TeamId == tid && a.EmployeeId == null)
                    && await db.Teams.FirstOrDefaultAsync(t => t.Id == tid, ct) is { } team)
                    await notifications.EnqueueChannelAsync($"fu:{task.Id:N}:{scheduled:yyyyMMddHHmm}", task, team, NotificationCategory.Overdue, HeadlineFor(decision, task), decision.Reason, ct);

                if (decision.Kind is not FollowUpKind.ApprovalPending)
                {
                    task.LastReminderAt = now;
                    task.RemindersSinceResponse++;
                    task.NoResponseSince ??= now;
                }
            }
        }

        db.TaskFollowUps.Add(log);
        task.LastFollowUpAt = now;
        task.NextFollowUpAt = decision.Action == FollowUpAction.Stop ? null : FollowUpScheduler.Next(task.FollowUp, now > scheduled ? now : scheduled, anchor, cal);
        audit.Record(log.Action == FollowUpAction.Remind ? "ReminderSent" : "FollowUpSkipped", nameof(WorkTask), task.Id, task.Id, "FollowUp", null, log.Kind.ToString(), log.Reason);
        await monitoring.RefreshAsync(task, ct);
        UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<FollowUpContext> BuildContextAsync(WorkTask task, DateTime now, BusinessCalendar cal, CancellationToken ct)
    {
        var today = cal.Today(now);
        var overdue = DeadlineRules.IsOverdue(task.DueDate, task.DueTime, task.Status, now, cal);
        var openCommitment = await db.Commitments.Where(c => c.TaskId == task.Id && c.Status == CommitmentStatus.Open)
            .OrderByDescending(c => c.CreatedAt).Select(c => (DateOnly?)c.CommitmentDate).FirstOrDefaultAsync(ct);
        var missed = await db.Commitments.AnyAsync(c => c.TaskId == task.Id && c.Status == CommitmentStatus.Missed
            && c.ResolvedAt > (task.LastAssigneeActivityAt ?? DateTime.MinValue), ct);
        var pendingDeps = await db.TaskDependencies.CountAsync(d => d.TaskId == task.Id && !d.IsResolved
            && (d.DependsOnTaskId == null || (d.DependsOnTask!.Status != WorkStatus.Completed && d.DependsOnTask.Status != WorkStatus.Cancelled)), ct);
        var lastChannel = await db.TaskMessageLinks.Where(l => l.TaskId == task.Id && l.Status == LinkStatus.Linked)
            .OrderByDescending(l => l.Message!.SentAt).Select(l => new { l.Message!.Channel, l.Message.SentAt }).FirstOrDefaultAsync(ct);
        string? channel = lastChannel is not null && task.LastAssigneeActivityAt is { } la && Math.Abs((la - lastChannel.SentAt).TotalMinutes) < 5
            ? lastChannel.Channel == CommunicationChannel.Email ? "email" : "Teams" : null;
        return new FollowUpContext
        {
            Now = now,
            Today = today,
            Status = task.Status,
            Criticality = task.Criticality,
            Acknowledgement = task.Acknowledgement,
            DueDate = task.DueDate,
            IsOverdue = overdue,
            DaysOverdue = overdue && task.DueDate is { } d ? DeadlineRules.DaysOverdue(d, today) : 0,
            DaysUntilDue = !overdue && task.DueDate is { } d2 ? d2.DayNumber - today.DayNumber : null,
            PausedUntil = task.FollowUpPausedUntil,
            PauseReason = task.FollowUpPauseReason,
            LastAssigneeActivityAt = task.LastAssigneeActivityAt,
            LastActivityChannel = channel,
            LastReminderAt = task.LastReminderAt,
            RemindersSinceResponse = task.RemindersSinceResponse,
            IsBlocked = task.IsBlocked,
            BlockedReason = task.BlockedReason,
            PendingDependencies = pendingDeps,
            OpenCommitmentDate = openCommitment,
            HasMissedCommitment = missed,
            RecentUpdateWindow = TimeSpan.FromHours(task.Criticality >= Criticality.Critical ? Math.Min(12, _o.RecentUpdateWindowHours) : _o.RecentUpdateWindowHours),
        };
    }

    private async Task<List<Guid>> RecipientsAsync(WorkTask task, FollowUpDecision decision, CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (decision.Kind == FollowUpKind.ApprovalPending)
        {
            var approver = await db.TaskApprovals.Where(a => a.TaskId == task.Id && a.Round == task.CompletionRound && a.Decision == ApprovalDecision.Pending)
                .OrderBy(a => a.Stage).Select(a => (Guid?)a.ApproverId).FirstOrDefaultAsync(ct);
            if (approver is { } ap) ids.Add(ap);
            else if (task.DelegatorId is { } d) ids.Add(d);
            return ids;
        }
        if (task.PrimaryAssigneeId is { } p) ids.Add(p);
        if (decision.Kind == FollowUpKind.UpdateRequest || decision.Kind == FollowUpKind.Overdue)
            ids.AddRange(task.Assignees.Where(a => a.RemovedAt == null && a.Role == AssigneeRole.Additional && a.EmployeeId is not null).Select(a => a.EmployeeId!.Value));
        if (decision.IncludeTeamLeader && task.TeamId is { } tid && await db.Teams.Where(t => t.Id == tid).Select(t => t.LeaderId).FirstOrDefaultAsync(ct) is { } leader)
            ids.Add(leader);
        return ids.Distinct().ToList();
    }

    private static NotificationCategory CategoryFor(FollowUpKind kind) => kind switch
    {
        FollowUpKind.DueSoon => NotificationCategory.DueSoon,
        FollowUpKind.DueToday => NotificationCategory.DueToday,
        FollowUpKind.Overdue => NotificationCategory.Overdue,
        FollowUpKind.AcknowledgementReminder => NotificationCategory.AcknowledgementRequest,
        FollowUpKind.ApprovalPending => NotificationCategory.ApprovalRequest,
        FollowUpKind.CommitmentCheck => NotificationCategory.CommitmentMissed,
        _ => NotificationCategory.Reminder,
    };

    /// <summary>Wording from spec §20: "requires an update", "is due tomorrow", "is overdue by three days".</summary>
    private static string HeadlineFor(FollowUpDecision d, WorkTask t) => d.Kind switch
    {
        FollowUpKind.DueToday => "is due today.",
        FollowUpKind.DueSoon => d.Reason.Contains("tomorrow", StringComparison.OrdinalIgnoreCase) ? "is due tomorrow." : "is due in 2 days.",
        FollowUpKind.Overdue => $"is {d.Reason.ToLowerInvariant()}.",
        FollowUpKind.AcknowledgementReminder => "is waiting for your acknowledgement.",
        FollowUpKind.BlockerCheck => "is blocked — is the blocker still open?",
        FollowUpKind.DependencyCheck => "is waiting on a dependency — please confirm its status.",
        FollowUpKind.CommitmentCheck => "— the committed date has passed. Please share a revised date.",
        FollowUpKind.ApprovalPending => "is waiting for your completion confirmation.",
        _ => "requires an update.",
    };

    private static string? MessageFor(FollowUpDecision d) => d.Kind switch
    {
        FollowUpKind.UpdateRequest => "Please reply with a short status update: progress, blockers and the expected completion date.",
        FollowUpKind.Overdue => "Please share the revised completion date and what is holding it up. If you need more time, request an extension.",
        FollowUpKind.BlockerCheck => "Reply with the current blocker status, or pause follow-up if you are legitimately waiting on someone outside.",
        _ => null,
    };

    // ═══════════════════════════════ Deadline notices (job "deadline.process") ═══════════════════════════════

    /// <summary>
    /// Deadline processing: "due tomorrow", "due today" and "now overdue" notices for tasks without a follow-up
    /// point that day, once per task per deadline (idempotent keys), plus monitoring refresh when a deadline passes.
    /// </summary>
    public async Task<int> ProcessDeadlinesAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var today = cal.Today(now);
        var local = cal.ToLocal(now);
        if (!cal.IsWorkingDay(today) || TimeOnly.FromDateTime(local) < cal.WorkdayStart) return 0;   // notices go out in working hours
        var tomorrow = cal.NextWorkingDay(today, inclusive: false);

        var candidates = await db.Tasks.ActiveOnly()
            .Where(t => t.DueDate != null && t.DueDate <= tomorrow && t.Status != WorkStatus.CompletedByAssignee && t.Status != WorkStatus.CompletionAwaitingApproval)
            .Include(t => t.Assignees).ToListAsync(ct);
        var count = 0;
        foreach (var t in candidates)
        {
            var due = t.DueDate!.Value;
            var overdue = DeadlineRules.IsOverdue(t.DueDate, t.DueTime, t.Status, now, cal);
            (string kind, NotificationCategory cat, string headline)? notice =
                overdue && DeadlineRules.DaysOverdue(due, today) <= 1 ? ("overdue", NotificationCategory.Overdue, DeadlineRules.DaysOverdue(due, today) == 0 ? "is now overdue." : "is overdue by 1 day.")
                : !overdue && due == today ? ("today", NotificationCategory.DueToday, "is due today.")
                : !overdue && due == tomorrow ? ("tomorrow", NotificationCategory.DueSoon, tomorrow == today.AddDays(1) ? "is due tomorrow." : $"is due {due:dddd}.")
                : null;
            if (notice is not { } n || t.PrimaryAssigneeId is not { } assignee) continue;

            var key = $"dl:{t.Id:N}:{n.kind}:{due:yyyyMMdd}";
            if (await db.Notifications.AnyAsync(x => x.IdempotencyKey.StartsWith(key), ct)) continue;
            // An escalation step or follow-up already sent the same kind of notice today.
            var startOfDay = cal.ToUtc(today, TimeOnly.MinValue);
            if (await db.Notifications.AnyAsync(x => x.TaskId == t.Id && x.Category == n.cat && x.CreatedAt >= startOfDay && x.Channel != NotificationChannel.InApp, ct)) continue;
            // Skip when a reminder for the task already went out recently (the follow-up covered it).
            if (t.LastReminderAt is { } lr && now - lr < TimeSpan.FromHours(12) || t.LastAssigneeActivityAt is { } la && now - la < TimeSpan.FromHours(4))
            {
                db.Notifications.Add(new Notification { IdempotencyKey = key + ":skipped", TaskId = t.Id, RecipientEmployeeId = assignee, Channel = NotificationChannel.InApp, Category = n.cat, Subject = "Deadline notice skipped (recent reminder/update)", Status = NotificationStatus.Suppressed, ScheduledAt = now, CreatedAt = now });
                await db.SaveChangesAsync(ct);
                continue;
            }
            count += await notifications.EnqueueAsync(new NoticeRequest
            {
                Key = key,
                Category = n.cat,
                Task = t,
                RecipientIds = [assignee],
                Headline = n.headline,
                Message = n.kind == "overdue" ? "Please complete it or share a revised date and the reason for the delay." : null,
                Urgent = t.Criticality >= Criticality.Critical && n.kind == "overdue",
                SupersedeKey = $"rem:{t.Id:N}",
                ExpiresIn = TimeSpan.FromHours(_o.ReminderExpiryHours),
            }, ct);
            if (n.kind == "overdue") audit.Record("BecameOverdue", nameof(WorkTask), t.Id, t.Id, "DueDate", null, due.ToString("yyyy-MM-dd"));
            await monitoring.RefreshAsync(t, ct);
            UiEvents.Publish(db, clock, UiEvents.TaskChanged, t.Id);
            await db.SaveChangesAsync(ct);
        }
        return count;
    }
}
