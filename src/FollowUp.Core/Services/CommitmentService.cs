using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace FollowUp.Core.Services;

/// <summary>
/// Commitment tracking (spec §28, §29): evaluates promises whose date has passed — met when the task was
/// completed in time, otherwise missed — notifies the person and the delegator, and answers
/// "which commitments made to me have been missed?".
/// </summary>
public sealed class CommitmentService(FollowUpDbContext db, IClock clock, CalendarProvider calendars, NotificationService notifications,
    MonitoringService monitoring, AuditService audit, ActorContext actor)
{
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var today = cal.Today(now);
        var due = await db.Commitments.Where(c => c.Status == CommitmentStatus.Open && c.CommitmentDate <= today).ToListAsync(ct);
        var changed = 0;
        foreach (var c in due)
        {
            // A commitment "by Friday" is kept until the end of Friday's working day (or the stated time).
            if (now <= cal.DeadlineUtc(c.CommitmentDate, c.CommitmentTime)) continue;
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == c.TaskId, ct);
            if (task is null) continue;
            var completedOn = task.CompletedAt is { } ca ? DateOnly.FromDateTime(cal.ToLocal(ca)) : (DateOnly?)null;
            var submitted = await db.CompletionSubmissions.Where(s => s.TaskId == task.Id).OrderBy(s => s.SubmittedAt).Select(s => (DateOnly?)s.CompletionDate).FirstOrDefaultAsync(ct);
            var doneOn = completedOn ?? submitted;
            if (doneOn is { } d && d <= c.CommitmentDate)
            {
                c.Status = CommitmentStatus.Met;
                c.ResolvedAt = now;
                c.ResolutionNote = "Completed by the committed date";
            }
            else if (task.Status == WorkStatus.Cancelled)
            {
                c.Status = CommitmentStatus.Cancelled;
                c.ResolvedAt = now;
                c.ResolutionNote = "Task cancelled";
            }
            else
            {
                c.Status = CommitmentStatus.Missed;
                c.ResolvedAt = now;
                c.ResolutionNote = $"Not completed by {c.CommitmentDate:ddd d MMM}";
                c.MissedNotifiedAt = now;
                audit.Record("CommitmentMissed", nameof(Commitment), c.Id, task.Id, "Status", "Open", "Missed", c.OriginalText);
                var recipients = new List<Guid>();
                if (c.EmployeeId is { } who) recipients.Add(who);
                if (recipients.Count > 0)
                    await notifications.EnqueueAsync(new NoticeRequest
                    {
                        Key = $"commit-missed:{c.Id:N}",
                        Category = NotificationCategory.CommitmentMissed,
                        Task = task,
                        RecipientIds = recipients,
                        Headline = $"— you committed to finish by {c.CommitmentDate:ddd d MMM}. Please share a revised date.",
                        Message = $"Your message: \"{NotificationService.Shorten(c.OriginalText, 300)}\"",
                        Channels = NotifyChannels.InApp | NotifyChannels.Email | NotifyChannels.Teams,
                    }, ct);
                if ((c.CommittedToId ?? task.DelegatorId) is { } to && to != c.EmployeeId)
                    await notifications.EnqueueAsync(new NoticeRequest
                    {
                        Key = $"commit-missed-to:{c.Id:N}",
                        Category = NotificationCategory.CommitmentMissed,
                        Task = task,
                        RecipientIds = [to],
                        Headline = $"— a commitment for {c.CommitmentDate:ddd d MMM} was missed.",
                        Message = $"\"{NotificationService.Shorten(c.OriginalText, 300)}\"",
                        Channels = task.Criticality >= Criticality.Important ? NotifyChannels.InApp | NotifyChannels.Email : NotifyChannels.InApp,
                    }, ct);
            }
            await monitoring.RefreshAsync(task, ct);
            UiEvents.Publish(db, clock, UiEvents.TaskChanged, task.Id);
            await db.SaveChangesAsync(ct);
            changed++;
        }
        return changed;
    }

    /// <summary>Commitments list for the commitments page and the CEO workbench.</summary>
    public async Task<List<CommitmentDto>> ListAsync(CommitmentStatus? status, bool toMe, Guid? employeeId, DateOnly? from, DateOnly? to, CancellationToken ct, int take = 200)
    {
        var visible = db.Tasks.VisibleTo(db, actor.Viewer).Select(t => t.Id);
        var q = db.Commitments.Where(c => visible.Contains(c.TaskId));
        if (status is { } s) q = q.Where(c => c.Status == s);
        if (toMe && actor.EmployeeId is { } me) q = q.Where(c => c.CommittedToId == me);
        if (employeeId is { } e) q = q.Where(c => c.EmployeeId == e);
        if (from is { } f) q = q.Where(c => c.CommitmentDate >= f);
        if (to is { } tt) q = q.Where(c => c.CommitmentDate <= tt);
        return await ProjectAsync(q.OrderByDescending(c => c.CommitmentDate).Take(take), ct);
    }

    public async Task<List<CommitmentDto>> ProjectAsync(IQueryable<Commitment> q, CancellationToken ct)
    {
        var rows = await q.Select(c => new
        {
            c.Id, c.TaskId, c.CommitmentDate, c.What, c.OriginalText, c.Source, c.SourceMessageId, c.Confidence, c.Status, c.CreatedAt, c.ResolvedAt,
            c.EmployeeId, c.CommittedToId,
            TaskNumber = db.Tasks.Where(t => t.Id == c.TaskId).Select(t => t.Number).FirstOrDefault(),
            TaskTitle = db.Tasks.Where(t => t.Id == c.TaskId).Select(t => t.Title).FirstOrDefault(),
            Person = db.Employees.Where(e => e.Id == c.EmployeeId).Select(e => e.DisplayName).FirstOrDefault(),
            To = db.Employees.Where(e => e.Id == c.CommittedToId).Select(e => e.DisplayName).FirstOrDefault(),
        }).ToListAsync(ct);
        return rows.Select(r => new CommitmentDto(r.Id, r.TaskId, r.TaskNumber ?? "", r.TaskTitle ?? "",
            r.EmployeeId is { } e ? new PersonRef(e, r.Person ?? "") : null,
            r.CommittedToId is { } t ? new PersonRef(t, r.To ?? "") : null,
            r.CommitmentDate, r.What, r.OriginalText, r.Source, r.SourceMessageId, r.Confidence, r.Status, r.CreatedAt, r.ResolvedAt)).ToList();
    }
}
