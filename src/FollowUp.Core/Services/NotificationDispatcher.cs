using FollowUp.Core.Abstractions;
using FollowUp.Core.Data;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Services;

/// <summary>
/// Delivers the persistent notification queue (spec §66) over email and Teams. Rows are claimed with a lease
/// (safe with several hosts), marked "Sending" before the external call so a crash is detectable, retried with
/// backoff on transient/throttling failures, expired when stale after downtime (no floods), and dead-lettered
/// after repeated failure. A crash between "sent" and "marked sent" is reconciled for email by looking the
/// idempotency key up in Sent Items.
/// </summary>
public sealed class NotificationDispatcher(
    FollowUpDbContext db,
    IClock clock,
    IEmailService email,
    ITeamsService teams,
    IntegrationErrorRecorder errors,
    ILogger<NotificationDispatcher> logger)
{
    private const int MaxAttempts = 8;
    private static readonly TimeSpan StuckSendingAfter = TimeSpan.FromMinutes(5);
    private readonly string _host = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<int> DispatchAsync(CancellationToken ct, int batchSize = 50)
    {
        var now = clock.UtcNow;
        await RecoverStuckAsync(now, ct);
        await ExpireStaleAsync(now, ct);

        // Claim a batch atomically: only rows whose lease is free are taken.
        var candidates = await db.Notifications
            .Where(n => (n.Status == NotificationStatus.Pending || n.Status == NotificationStatus.Failed)
                && n.ScheduledAt <= now && (n.NextAttemptAt == null || n.NextAttemptAt <= now)
                && (n.LockedUntil == null || n.LockedUntil < now))
            .OrderByDescending(n => n.Priority).ThenBy(n => n.ScheduledAt)
            .Select(n => n.Id).Take(batchSize).ToListAsync(ct);
        if (candidates.Count == 0) return 0;
        var leaseUntil = now.AddMinutes(2);
        await db.Notifications.Where(n => candidates.Contains(n.Id) && (n.LockedUntil == null || n.LockedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LockedBy, _host).SetProperty(n => n.LockedUntil, leaseUntil), ct);
        var claimed = await db.Notifications.Where(n => candidates.Contains(n.Id) && n.LockedBy == _host && n.LockedUntil == leaseUntil).ToListAsync(ct);

        var sent = 0;
        foreach (var n in claimed)
        {
            ct.ThrowIfCancellationRequested();
            n.Status = NotificationStatus.Sending;
            n.AttemptCount++;
            n.LastAttemptAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);   // persisted before the external call

            try
            {
                var result = await SendAsync(n, ct);
                if (!result.Success) throw new IntegrationException(IntegrationErrorKind.Unknown, result.Error ?? "Delivery failed");
                n.Status = NotificationStatus.Sent;
                n.SentAt = clock.UtcNow;
                n.ExternalMessageId = result.ExternalMessageId;
                n.Error = null;
                sent++;
            }
            catch (IntegrationException ex)
            {
                Fail(n, ex.IsTransient, ex.Message, ex.RetryAfter);
                await errors.RecordAsync("Notifications", n.Channel.ToString(), $"Send {n.Category}", ex, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Fail(n, transient: true, ex.Message, null);
                await errors.RecordAsync("Notifications", n.Channel.ToString(), $"Send {n.Category}", new IntegrationException(IntegrationErrorKind.Transient, ex.Message), ct);
            }
            n.LockedBy = null;
            n.LockedUntil = null;
            UiEvents.Publish(db, clock, UiEvents.NotificationQueued, n.TaskId);
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }

    private Task<DeliveryResult> SendAsync(Notification n, CancellationToken ct) => n.Channel switch
    {
        NotificationChannel.Email => email.SendAsync(new OutboundEmail(n.IdempotencyKey, [new EmailAddress(n.RecipientAddress ?? "", n.RecipientName)], [],
            n.Subject, n.Body, n.BodyHtml, n.Priority > 0), ct),
        NotificationChannel.TeamsChat => teams.SendAsync(new OutboundTeamsMessage(n.IdempotencyKey, TeamsTargetKind.User, n.Body, n.BodyHtml,
            UserId: n.RecipientAddress, Urgent: n.Priority > 1), ct),
        NotificationChannel.TeamsChannel => SendChannelAsync(n, ct),
        NotificationChannel.TeamsActivity => teams.SendAsync(new OutboundTeamsMessage(n.IdempotencyKey, TeamsTargetKind.ActivityFeed, n.Subject, null,
            UserId: n.RecipientAddress), ct),
        _ => Task.FromResult(new DeliveryResult(true, null)),
    };

    private Task<DeliveryResult> SendChannelAsync(Notification n, CancellationToken ct)
    {
        var parts = (n.TargetConversation ?? "").Split('|');
        if (parts.Length != 2) return Task.FromResult(new DeliveryResult(false, null, "Missing team/channel target"));
        return teams.SendAsync(new OutboundTeamsMessage(n.IdempotencyKey, TeamsTargetKind.Channel, n.Body, n.BodyHtml, TeamId: parts[0], ChannelId: parts[1]), ct);
    }

    private void Fail(Notification n, bool transient, string error, TimeSpan? retryAfter)
    {
        n.Error = error.Length > 1900 ? error[..1900] : error;
        if (!transient && n.AttemptCount >= 2 || n.AttemptCount >= MaxAttempts)
        {
            n.Status = NotificationStatus.DeadLetter;
            n.NextAttemptAt = null;
            logger.LogWarning("Notification {Key} dead-lettered after {Attempts} attempts: {Error}", n.IdempotencyKey, n.AttemptCount, error);
            return;
        }
        n.Status = NotificationStatus.Failed;
        var backoff = retryAfter ?? TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, n.AttemptCount - 1), 1800));
        n.NextAttemptAt = clock.UtcNow + backoff;
    }

    /// <summary>
    /// Rows left in "Sending" by a crashed process. Email: ask the mailbox whether the key was sent. Teams: the
    /// provider cannot tell, so the message is retried (at-least-once inside a tiny crash window).
    /// </summary>
    private async Task RecoverStuckAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = now - StuckSendingAfter;
        var stuck = await db.Notifications.Where(n => n.Status == NotificationStatus.Sending && n.LastAttemptAt < cutoff).Take(50).ToListAsync(ct);
        foreach (var n in stuck)
        {
            bool? alreadySent = null;
            if (n.Channel == NotificationChannel.Email)
            {
                try { alreadySent = await email.WasSentAsync(n.IdempotencyKey, (n.LastAttemptAt ?? now).AddHours(-1), ct); }
                catch (IntegrationException ex) { logger.LogWarning(ex, "Could not reconcile notification {Key}", n.IdempotencyKey); continue; }
            }
            if (alreadySent == true)
            {
                n.Status = NotificationStatus.Sent;
                n.SentAt = n.LastAttemptAt;
                n.Error = "Recovered after restart: found in Sent Items";
            }
            else
            {
                n.Status = NotificationStatus.Failed;
                n.NextAttemptAt = now;
                n.Error = "Recovered after restart: delivery not confirmed, retrying";
            }
            n.LockedBy = null;
            n.LockedUntil = null;
        }
        if (stuck.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>Unsent reminders past their expiry are not delivered late — a newer reminder supersedes them (spec §62).</summary>
    private async Task ExpireStaleAsync(DateTime now, CancellationToken ct)
    {
        await db.Notifications.Where(n => (n.Status == NotificationStatus.Pending || n.Status == NotificationStatus.Failed) && n.ExpiresAt != null && n.ExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, NotificationStatus.Expired)
                .SetProperty(n => n.Error, "Expired before delivery (system unavailable or superseded)"), ct);
    }

    /// <summary>Manual retry of dead-lettered notifications from the admin monitor.</summary>
    public async Task<int> RetryAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = await db.Notifications.Where(n => ids.Contains(n.Id) && (n.Status == NotificationStatus.DeadLetter || n.Status == NotificationStatus.Failed || n.Status == NotificationStatus.Expired)).ToListAsync(ct);
        foreach (var n in rows)
        {
            n.Status = NotificationStatus.Pending;
            n.NextAttemptAt = null;
            n.ScheduledAt = now;
            n.ExpiresAt = null;
            n.AttemptCount = 0;
            n.Error = null;
        }
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}

/// <summary>Records failed external calls for the integration monitor (spec §67).</summary>
public sealed class IntegrationErrorRecorder(FollowUpDbContext db, IClock clock)
{
    public async Task RecordAsync(string integration, string? sourceKey, string operation, IntegrationException ex, CancellationToken ct)
    {
        db.IntegrationErrors.Add(new IntegrationError
        {
            Integration = integration,
            SourceKey = sourceKey,
            Operation = operation,
            Kind = ex.Kind,
            StatusCode = ex.StatusCode,
            Message = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message,
            RetryAfterSeconds = ex.RetryAfter is { } r ? (int)r.TotalSeconds : null,
            OccurredAt = clock.UtcNow,
        });
        await Task.CompletedTask;
        _ = ct;
    }
}
