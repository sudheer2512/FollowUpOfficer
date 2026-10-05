using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FollowUp.Core.Services;

/// <summary>
/// Checkpoint-based synchronisation of Outlook mailboxes and Teams chats / channels (spec §60–§64).
/// Every page of changes is imported and its cursor (delta link, next link, watermark, last id) is persisted
/// in the SAME transaction — so after a crash, restart or Graph outage the next run continues exactly after
/// the last committed page. Expired delta tokens trigger a resync from the watermark; duplicates from the
/// overlap are absorbed by the unique message key.
/// </summary>
public sealed class SyncService(
    FollowUpDbContext db,
    IClock clock,
    IMailSynchronizationService mail,
    ITeamsSynchronizationService teams,
    CommunicationService communications,
    IntegrationErrorRecorder errors,
    ILogger<SyncService> logger) : ISynchronizationCheckpointService
{
    private const int MaxPagesPerRun = 20;

    /// <summary>Discovers authorised sources and creates checkpoints for new ones (job "sources.discover").</summary>
    public async Task<int> DiscoverAsync(CancellationToken ct)
    {
        var added = 0;
        foreach (var (type, discover) in new (string, Func<CancellationToken, Task<IReadOnlyList<SyncSourceInfo>>>)[] { ("mail", mail.DiscoverSourcesAsync), ("teams", teams.DiscoverSourcesAsync) })
        {
            IReadOnlyList<SyncSourceInfo> sources;
            try { sources = await discover(ct); }
            catch (IntegrationException ex)
            {
                await errors.RecordAsync(type == "mail" ? "Outlook" : "Teams", null, "Discover sources", ex, ct);
                await db.SaveChangesAsync(ct);
                continue;
            }
            var owners = await db.Employees.AsNoTracking().Select(e => new { e.Id, e.Email }).ToListAsync(ct);
            foreach (var s in sources)
            {
                if (await db.SyncCheckpoints.AnyAsync(c => c.IntegrationType == s.Type && c.SourceKey == s.SourceKey, ct)) continue;
                db.SyncCheckpoints.Add(new SynchronizationCheckpoint
                {
                    IntegrationType = s.Type,
                    SourceKey = s.SourceKey,
                    DisplayName = s.DisplayName,
                    OwnerEmployeeId = owners.FirstOrDefault(o => string.Equals(o.Email, s.OwnerAddress, StringComparison.OrdinalIgnoreCase))?.Id,
                    Enabled = true,
                    NextRunAt = clock.UtcNow,
                });
                added++;
            }
            await db.SaveChangesAsync(ct);
        }
        return added;
    }

    public Task<int> SyncMailAsync(CancellationToken ct) => SyncTypeAsync([IntegrationType.OutlookMail], ct);

    public Task<int> SyncTeamsAsync(CancellationToken ct) => SyncTypeAsync([IntegrationType.TeamsChat, IntegrationType.TeamsChannel], ct);

    private async Task<int> SyncTypeAsync(IntegrationType[] types, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await db.SyncCheckpoints.Where(c => types.Contains(c.IntegrationType) && c.Enabled && (c.NextRunAt == null || c.NextRunAt <= now))
            .OrderBy(c => c.LastSuccessfulSyncAt).Select(c => c.Id).ToListAsync(ct);
        var imported = 0;
        foreach (var id in due)
        {
            imported += await SyncSourceAsync(id, ct);
            db.ChangeTracker.Clear();
        }
        return imported;
    }

    /// <summary>Synchronises one source page by page; each page commits messages + cursor atomically.</summary>
    public async Task<int> SyncSourceAsync(Guid checkpointId, CancellationToken ct)
    {
        var imported = 0;
        var resynced = false;
        for (var page = 0; page < MaxPagesPerRun; page++)
        {
            var cp = await db.SyncCheckpoints.FirstAsync(c => c.Id == checkpointId, ct);
            var now = clock.UtcNow;
            cp.LastAttemptAt = now;
            cp.Status = SyncState.Running;
            var cursor = new SyncCursor(cp.IntegrationType, cp.SourceKey, cp.DeltaToken, cp.ContinuationToken, cp.LastProcessedAt, cp.LastProcessedExternalId);
            SyncPage result;
            try
            {
                result = cp.IntegrationType == IntegrationType.OutlookMail ? await mail.FetchAsync(cursor, ct) : await teams.FetchAsync(cursor, ct);
            }
            catch (IntegrationException ex) when (ex.Kind == IntegrationErrorKind.Gone && !resynced)
            {
                // Delta token expired / sync state lost: restart the delta round from the watermark (spec §64).
                logger.LogWarning("Sync token for {Source} expired — resynchronising from watermark {Watermark}", cp.SourceKey, cp.LastProcessedAt);
                cp.DeltaToken = null;
                cp.ContinuationToken = null;
                await errors.RecordAsync(Name(cp.IntegrationType), cp.SourceKey, "Delta resync", ex, ct);
                await db.SaveChangesAsync(ct);
                resynced = true;
                page--;
                continue;
            }
            catch (IntegrationException ex)
            {
                await FailAsync(cp, ex, ct);
                return imported;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await FailAsync(cp, new IntegrationException(IntegrationErrorKind.Transient, ex.Message, inner: ex), ct);
                return imported;
            }

            // Import + advance the checkpoint in one transaction: exactly-once from the application's view.
            var inserted = 0;
            foreach (var m in result.Messages.OrderBy(m => m.SentAt))
                if (await communications.IngestAsync(m with { SourceKey = cp.SourceKey }, ct)) inserted++;
            var last = result.Messages.OrderBy(m => m.LastModifiedAt).LastOrDefault();
            if (last is not null) cp.LastProcessedExternalId = last.ExternalId.Length > 400 ? last.ExternalId[..400] : last.ExternalId;
            if (result.Watermark is { } w && (cp.LastProcessedAt is null || w > cp.LastProcessedAt)) cp.LastProcessedAt = w;
            cp.ContinuationToken = result.IsFinal ? null : result.ContinuationToken;
            if (result.DeltaToken is not null) cp.DeltaToken = result.DeltaToken;
            cp.ItemsImportedTotal += inserted;
            imported += inserted;
            if (result.IsFinal)
            {
                cp.Status = SyncState.Succeeded;
                cp.LastSuccessfulSyncAt = now;
                cp.LastRunItemCount = imported;
                cp.Error = null;
                cp.RetryCount = 0;
                cp.NextRunAt = null;
            }
            if (inserted > 0) UiEvents.Publish(db, clock, UiEvents.CommunicationImported);
            UiEvents.Publish(db, clock, UiEvents.SyncStatus);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            if (result.IsFinal) break;
        }
        return imported;
    }

    private async Task FailAsync(SynchronizationCheckpoint cp, IntegrationException ex, CancellationToken ct)
    {
        var now = clock.UtcNow;
        cp.RetryCount++;
        cp.Error = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
        (cp.Status, cp.NextRunAt) = ex.Kind switch
        {
            IntegrationErrorKind.Throttled => (SyncState.Throttled, now + (ex.RetryAfter ?? TimeSpan.FromSeconds(30))),
            IntegrationErrorKind.Authentication or IntegrationErrorKind.Permission => (SyncState.AuthError, now.AddMinutes(5)),
            _ => (SyncState.Failed, now + TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, cp.RetryCount - 1), 900))),
        };
        logger.LogWarning("Sync of {Source} failed ({Kind}): {Message}; retry at {Next}", cp.SourceKey, ex.Kind, ex.Message, cp.NextRunAt);
        await errors.RecordAsync(Name(cp.IntegrationType), cp.SourceKey, "Synchronise", ex, ct);
        UiEvents.Publish(db, clock, UiEvents.SyncStatus);
        await db.SaveChangesAsync(ct);
    }

    private static string Name(IntegrationType t) => t == IntegrationType.OutlookMail ? "Outlook" : "Teams";

    // ═══════════════════════════════ Admin ═══════════════════════════════

    public async Task<List<CheckpointDto>> ListAsync(CancellationToken ct)
    {
        var pending = await db.Messages.Where(m => m.ProcessingStatus == MessageProcessingStatus.Pending)
            .GroupBy(m => m.SourceKey).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var rows = await db.SyncCheckpoints.AsNoTracking().OrderBy(c => c.IntegrationType).ThenBy(c => c.DisplayName).ToListAsync(ct);
        return rows.Select(c => new CheckpointDto(c.Id, c.IntegrationType, c.SourceKey, c.DisplayName, c.Enabled, c.Status, c.LastSuccessfulSyncAt,
            c.LastAttemptAt, c.NextRunAt, c.LastProcessedAt, c.LastProcessedExternalId, c.DeltaToken is not null, c.ContinuationToken is not null,
            c.Error, c.RetryCount, c.ItemsImportedTotal, c.LastRunItemCount, pending.FirstOrDefault(p => p.Key == c.SourceKey)?.Count ?? 0)).ToList();
    }

    /// <summary>Forces a full resync from the watermark (drops delta / continuation tokens).</summary>
    public async Task ResetAsync(Guid id, bool fromScratch, CancellationToken ct)
    {
        var cp = await db.SyncCheckpoints.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Checkpoint");
        cp.DeltaToken = null;
        cp.ContinuationToken = null;
        if (fromScratch) { cp.LastProcessedAt = null; cp.LastProcessedExternalId = null; }
        cp.NextRunAt = clock.UtcNow;
        cp.RetryCount = 0;
        cp.Status = SyncState.Idle;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var cp = await db.SyncCheckpoints.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Checkpoint");
        cp.Enabled = enabled;
        cp.Status = enabled ? SyncState.Idle : SyncState.Disabled;
        cp.NextRunAt = enabled ? clock.UtcNow : null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Requests an immediate sync of every enabled source.</summary>
    public async Task RunNowAsync(CancellationToken ct) =>
        await db.SyncCheckpoints.Where(c => c.Enabled).ExecuteUpdateAsync(s => s.SetProperty(c => c.NextRunAt, clock.UtcNow), ct);
}
