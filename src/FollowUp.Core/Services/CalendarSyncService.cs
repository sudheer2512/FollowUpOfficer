using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>
/// Outlook calendar integration (spec §72): important deadlines, review dates and pending completion approvals are
/// pushed as calendar events — never routine notifications. Each entry is keyed by (task, kind, owner) and carries
/// a fingerprint (date + title + status) so Graph is only called when something visible changed; events of closed
/// tasks or obsolete entries are deleted. Failures are recorded and the run continues with the next entry.
/// </summary>
public sealed class CalendarSyncService(
    FollowUpDbContext db,
    IClock clock,
    CalendarProvider calendars,
    ICalendarService calendar,
    IntegrationErrorRecorder errors,
    SettingsService settings,
    IOptions<FollowUpOptions> options,
    ILogger<CalendarSyncService> logger)
{
    public const string Deadline = "Deadline";
    public const string Review = "Review";
    public const string Approval = "Approval";

    private sealed record Desired(Guid TaskId, string Kind, Guid OwnerId, DateOnly Date, TimeOnly? Start, TimeOnly? End, string Subject, string Body,
        bool HighImportance, string Fingerprint);

    public async Task<int> SyncAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (!s.CalendarSyncEnabled || calendar is Platform.IDisabledPort) return 0;
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var today = cal.Today(now);
        var execs = (await Executives.IdsAsync(db, ct)).ToHashSet();
        var appUrl = options.Value.PublicAppUrl.TrimEnd('/');

        var candidates = await db.Tasks.AsNoTracking().ActiveOnly()
            .Where(t => t.Criticality >= Criticality.Important || t.NotifyCeoOnDelay)
            .Select(t => new
            {
                t.Id, t.Number, t.Title, t.Status, t.DueDate, t.ReviewDate, t.Criticality, t.PrimaryAssigneeId, t.DelegatorId, t.CompletionRound,
                Approval = db.TaskApprovals.Where(a => a.TaskId == t.Id && a.Round == t.CompletionRound && a.Decision == ApprovalDecision.Pending)
                    .OrderBy(a => a.Stage).Select(a => new { a.ApproverId, a.RequestedAt }).FirstOrDefault(),
            }).ToListAsync(ct);

        var desired = new List<Desired>();
        foreach (var t in candidates)
        {
            var status = Text.Humanize(t.Status);
            var high = t.Criticality >= Criticality.Critical;
            var link = $"{appUrl}/tasks/{t.Id}";
            if (t.DueDate is { } due && !TaskStatusRules.IsAwaitingConfirmation(t.Status))
            {
                var subject = $"[{t.Number}] Due: {t.Title}";
                var body = $"{t.Number} — {t.Title}\nCriticality: {Text.Humanize(t.Criticality)}\nStatus: {status}\nOpen the task: {link}";
                var owners = new HashSet<Guid>();
                if (t.PrimaryAssigneeId is { } owner) owners.Add(owner);
                if (t.DelegatorId is { } del && execs.Contains(del)) owners.Add(del);
                foreach (var o in owners) desired.Add(new(t.Id, Deadline, o, due, null, null, subject, body, high, Fingerprint(due, subject, status)));
            }
            if (t.ReviewDate is { } review && t.DelegatorId is { } reviewer && review >= today.AddDays(-1))
            {
                var subject = $"[{t.Number}] Review: {t.Title}";
                desired.Add(new(t.Id, Review, reviewer, review, null, null, subject,
                    $"Review the progress of {t.Number} — {t.Title}.\nStatus: {status}\nOpen the task: {link}", high, Fingerprint(review, subject, status)));
            }
            if (t.Status == WorkStatus.CompletionAwaitingApproval && t.Approval is { } ap)
            {
                var date = cal.NextWorkingDay(DateOnly.FromDateTime(cal.ToLocal(ap.RequestedAt)));
                var start = cal.WorkdayStart.AddHours(1);
                var subject = $"[{t.Number}] Confirm completion: {t.Title}";
                desired.Add(new(t.Id, Approval, ap.ApproverId, date, start, start.AddMinutes(30), subject,
                    $"{t.Number} was marked complete and is waiting for your confirmation (Confirm, Reopen or Request More Work).\nOpen the task: {link}",
                    high, Fingerprint(date, subject, status)));
            }
        }

        var existing = await db.CalendarEntries.ToListAsync(ct);
        var ownerIds = desired.Select(d => d.OwnerId).Concat(existing.Select(e => e.OwnerEmployeeId)).Distinct().ToList();
        var mailboxes = await db.Employees.AsNoTracking().Where(e => ownerIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Email, ct);
        var changes = 0;

        foreach (var d in desired)
        {
            var entry = existing.FirstOrDefault(e => e.TaskId == d.TaskId && e.Kind == d.Kind && e.OwnerEmployeeId == d.OwnerId);
            if (entry is { Deleted: false, ExternalEventId: not null } && entry.Fingerprint == d.Fingerprint) continue;
            if (!mailboxes.TryGetValue(d.OwnerId, out var mailbox) || string.IsNullOrWhiteSpace(mailbox)) continue;
            if (entry is null)
            {
                entry = new CalendarEntry { TaskId = d.TaskId, Kind = d.Kind, OwnerEmployeeId = d.OwnerId };
                db.CalendarEntries.Add(entry);
                existing.Add(entry);
            }
            try
            {
                var eventId = await calendar.UpsertEventAsync(new CalendarEventRequest(
                    $"cal:{d.TaskId:N}:{d.Kind}:{d.OwnerId:N}", mailbox, entry.Deleted ? null : entry.ExternalEventId, d.Subject, d.Body, d.Date, d.Start, d.End,
                    cal.TimeZone.Id, d.HighImportance, d.Start is null ? null : 15), ct);
                entry.ExternalEventId = eventId;
                entry.Date = d.Date;
                entry.Fingerprint = d.Fingerprint;
                entry.SyncedAt = clock.UtcNow;
                entry.Deleted = false;
                entry.Error = null;
                changes++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await RecordAsync(entry, mailbox, "UpsertEvent", ex, ct);
            }
            await db.SaveChangesAsync(ct);
        }

        var wanted = desired.Select(d => (d.TaskId, d.Kind, d.OwnerId)).ToHashSet();
        foreach (var entry in existing.Where(e => !e.Deleted && !wanted.Contains((e.TaskId, e.Kind, e.OwnerEmployeeId))).ToList())
        {
            if (entry.ExternalEventId is { } eventId && mailboxes.TryGetValue(entry.OwnerEmployeeId, out var mailbox))
            {
                try { await calendar.DeleteEventAsync(mailbox, eventId, ct); }
                catch (IntegrationException ex) when (ex.Kind is IntegrationErrorKind.NotFound or IntegrationErrorKind.Gone) { /* already gone */ }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    await RecordAsync(entry, mailbox, "DeleteEvent", ex, ct);
                    await db.SaveChangesAsync(ct);
                    continue;
                }
            }
            entry.Deleted = true;
            entry.SyncedAt = clock.UtcNow;
            entry.Error = null;
            changes++;
            await db.SaveChangesAsync(ct);
        }
        return changes;
    }

    private async Task RecordAsync(CalendarEntry entry, string mailbox, string operation, Exception ex, CancellationToken ct)
    {
        var ie = ex as IntegrationException ?? new IntegrationException(IntegrationErrorKind.Unknown, ex.Message, inner: ex);
        entry.Error = ie.Message.Length > 900 ? ie.Message[..900] : ie.Message;
        await errors.RecordAsync("Calendar", mailbox, operation, ie, ct);
        logger.LogWarning(ex, "Calendar {Operation} failed for task {TaskId} ({Kind})", operation, entry.TaskId, entry.Kind);
    }

    public static string Fingerprint(DateOnly date, string subject, string status)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}|{subject}|{status}"));
        return Convert.ToHexString(hash)[..40];
    }
}
