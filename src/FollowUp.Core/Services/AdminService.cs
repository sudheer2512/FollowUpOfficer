using System.Globalization;
using System.Reflection;
using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Jobs;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>
/// Integration and background-processing monitor (spec §65–§68): Microsoft 365 connection health, sync checkpoints,
/// jobs and their executions, queue depths, integration errors, hosts, notification retry, failed-message
/// reprocessing and audit-chain verification.
/// </summary>
public sealed class AdminService(
    FollowUpDbContext db,
    ActorContext actor,
    IClock clock,
    CalendarProvider calendars,
    IIntegrationHealth health,
    SyncService sync,
    NotificationDispatcher dispatcher,
    CommunicationService communications,
    AuditService audit,
    INaturalLanguageService nl,
    HostIdentity host,
    IConfiguration configuration,
    IOptions<FollowUpOptions> options)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private void RequireAdmin()
    {
        if (!actor.IsSystem && !actor.Viewer.IsAdmin) throw new ForbiddenException("Only administrators can do that.");
    }

    private void RequireMonitor()
    {
        if (!actor.IsSystem && !actor.Viewer.IsAdmin && !actor.Viewer.IsExecutive) throw new ForbiddenException("Only administrators and executives can open the monitor.");
    }

    public string Mode => configuration["Microsoft365:Mode"] is { Length: > 0 } m ? m : "Disabled";

    public async Task<MonitorOverview> OverviewAsync(CancellationToken ct)
    {
        RequireMonitor();
        var now = clock.UtcNow;
        var since = now.AddHours(-24);
        IntegrationProbe probe;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(ProbeTimeout);
            try { probe = await health.ProbeAsync(cts.Token).WaitAsync(ProbeTimeout, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                probe = new IntegrationProbe(Mode, false, null, null, [], [],
                    ex is OperationCanceledException or TimeoutException ? $"Health probe timed out after {ProbeTimeout.TotalSeconds:0} s" : ex.Message, now);
            }
        }

        var checkpoints = await sync.ListAsync(ct);
        var jobs = await JobsAsync(ct);
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ProcessingStatus == MessageProcessingStatus.Pending || m.ProcessingStatus == MessageProcessingStatus.NeedsReview
                || m.ProcessingStatus == MessageProcessingStatus.Failed)
            .GroupBy(m => m.ProcessingStatus).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var notifications = await db.Notifications.AsNoTracking()
            .Where(n => n.Status == NotificationStatus.Pending || n.Status == NotificationStatus.Sending || n.Status == NotificationStatus.Failed
                || n.Status == NotificationStatus.DeadLetter)
            .GroupBy(n => n.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var errorKinds = await db.IntegrationErrors.AsNoTracking().Where(e => e.OccurredAt >= since).Select(e => e.Kind).ToListAsync(ct);
        var recentErrors = await ErrorsAsync(ct, 20);
        var executions = await ExecutionsQuery(null).Take(40).ToListAsync(ct);
        int M(MessageProcessingStatus s) => messages.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        int N(NotificationStatus s) => notifications.FirstOrDefault(x => x.Key == s)?.Count ?? 0;

        return new MonitorOverview(probe, probe.Mode, checkpoints, jobs,
            M(MessageProcessingStatus.Pending), M(MessageProcessingStatus.NeedsReview), M(MessageProcessingStatus.Failed),
            N(NotificationStatus.Pending) + N(NotificationStatus.Sending), N(NotificationStatus.Failed), N(NotificationStatus.DeadLetter),
            errorKinds.Count,
            errorKinds.Count(k => k == IntegrationErrorKind.Authentication),
            errorKinds.Count(k => k == IntegrationErrorKind.Throttled),
            errorKinds.Count(k => k == IntegrationErrorKind.Permission),
            errorKinds.Count(k => k == IntegrationErrorKind.Gone),
            recentErrors, executions.Select(ToDto).ToList(), await HostsAsync(ct), now);
    }

    public async Task<List<HostHeartbeat>> HostsAsync(CancellationToken ct)
    {
        var since = clock.UtcNow.AddHours(-24);
        var groups = await db.JobExecutions.AsNoTracking().Where(e => e.StartedAt >= since).GroupBy(e => e.Host)
            .Select(g => new { Host = g.Key, Count = g.Count() }).ToListAsync(ct);
        var result = new List<HostHeartbeat>();
        foreach (var g in groups)
        {
            var last = await db.JobExecutions.AsNoTracking().Where(e => e.Host == g.Host).OrderByDescending(e => e.StartedAt)
                .Select(e => new { e.StartedAt, e.FinishedAt }).FirstAsync(ct);
            result.Add(new HostHeartbeat(g.Host, last.FinishedAt is { } f && f > last.StartedAt ? f : last.StartedAt, g.Count));
        }
        if (result.All(h => h.Host != host.Id)) result.Add(new HostHeartbeat(host.Id, clock.UtcNow, 0));
        return [.. result.OrderByDescending(h => h.LastSeen)];
    }

    // ── Jobs ──

    public async Task<List<JobDto>> JobsAsync(CancellationToken ct)
    {
        var rows = await db.ScheduledJobs.AsNoTracking().OrderBy(j => j.Key).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public static JobDto ToDto(ScheduledJob j) => new(j.Key, j.Name, j.Description, j.ScheduleKind, j.IntervalSeconds, j.TimeOfDay, j.DayOfWeek, j.Misfire,
        j.Enabled, j.State, j.NextRunAt, j.LastStartedAt, j.LastSucceededAt, j.LastFailedAt, j.LastError, j.ConsecutiveFailures, j.LeaseOwner, j.LeaseExpiresAt,
        j.Checkpoint, ScheduleText(j));

    public static string ScheduleText(ScheduledJob j) => j.ScheduleKind switch
    {
        JobScheduleKind.DailyAt => $"Daily at {(j.TimeOfDay ?? new TimeOnly(8, 0)).ToString("HH:mm", CultureInfo.InvariantCulture)}",
        JobScheduleKind.WeeklyAt => $"Every {j.DayOfWeek ?? DayOfWeek.Monday} at {(j.TimeOfDay ?? new TimeOnly(9, 0)).ToString("HH:mm", CultureInfo.InvariantCulture)}",
        _ => j.IntervalSeconds < 60 ? $"Every {j.IntervalSeconds} s"
            : j.IntervalSeconds < 3600 ? $"Every {j.IntervalSeconds / 60} min"
            : $"Every {j.IntervalSeconds / 3600.0:0.#} h",
    };

    private IQueryable<BackgroundJobExecution> ExecutionsQuery(string? key)
    {
        var q = db.JobExecutions.AsNoTracking().AsQueryable();
        if (key is not null) q = q.Where(e => e.JobKey == key);
        return q.OrderByDescending(e => e.StartedAt);
    }

    public async Task<List<JobExecutionDto>> ExecutionsAsync(string key, CancellationToken ct, int take = 50)
    {
        RequireMonitor();
        if (!await db.ScheduledJobs.AnyAsync(j => j.Key == key, ct)) throw new NotFoundException("Job");
        return (await ExecutionsQuery(key).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct)).Select(ToDto).ToList();
    }

    private static JobExecutionDto ToDto(BackgroundJobExecution e) => new(e.Id, e.JobKey, e.Trigger, e.Host, e.StartedAt, e.FinishedAt, e.Outcome,
        e.ItemsProcessed, e.Message, e.Error, e.DueAt, e.FinishedAt is { } f ? (f - e.StartedAt).TotalMilliseconds : null);

    public async Task RunJobNowAsync(string key, CancellationToken ct)
    {
        RequireAdmin();
        var job = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.Key == key, ct) ?? throw new NotFoundException("Job");
        if (!job.Enabled) throw new ConflictException("job_disabled", "Enable the job before running it.");
        job.RunRequested = true;
        audit.Record("JobRunRequested", nameof(ScheduledJob), job.Id, details: key);
        await db.SaveChangesAsync(ct);
    }

    public async Task<JobDto> SetJobEnabledAsync(string key, bool enabled, CancellationToken ct)
    {
        RequireAdmin();
        var job = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.Key == key, ct) ?? throw new NotFoundException("Job");
        var now = clock.UtcNow;
        job.Enabled = enabled;
        if (!enabled) job.State = JobState.Disabled;
        else
        {
            if (job.State == JobState.Disabled) job.State = JobState.Idle;
            if (job.NextRunAt is null || job.NextRunAt < now)
                job.NextRunAt = job.ScheduleKind == JobScheduleKind.Interval ? now : JobEngine.NextScheduled(job, now, await calendars.GetDefaultAsync(ct));
        }
        audit.Record(enabled ? "JobEnabled" : "JobDisabled", nameof(ScheduledJob), job.Id, details: key);
        UiEvents.Publish(db, clock, UiEvents.JobStatus, payload: key);
        await db.SaveChangesAsync(ct);
        return ToDto(job);
    }

    // ── Sync checkpoints ──

    public async Task ResetCheckpointAsync(Guid id, bool fromScratch, CancellationToken ct)
    {
        RequireAdmin();
        await sync.ResetAsync(id, fromScratch, ct);
        audit.Record("CheckpointReset", nameof(SynchronizationCheckpoint), id, details: fromScratch ? "From scratch" : "Resync from watermark");
        await db.SaveChangesAsync(ct);
    }

    public async Task SetCheckpointEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        RequireAdmin();
        await sync.SetEnabledAsync(id, enabled, ct);
        audit.Record(enabled ? "CheckpointEnabled" : "CheckpointDisabled", nameof(SynchronizationCheckpoint), id);
        await db.SaveChangesAsync(ct);
    }

    public async Task SyncNowAsync(CancellationToken ct)
    {
        RequireAdmin();
        await sync.RunNowAsync(ct);
        foreach (var job in await db.ScheduledJobs.Where(j => (j.Key == "mail.sync" || j.Key == "teams.sync") && j.Enabled).ToListAsync(ct))
            job.RunRequested = true;
        await db.SaveChangesAsync(ct);
    }

    // ── Notifications & errors ──

    public async Task<List<NotificationRow>> NotificationsAsync(string? status, CancellationToken ct, int take = 200)
    {
        RequireAdmin();
        var q = db.Notifications.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<NotificationStatus>(status, true, out var st)) throw new ValidationException("status_invalid", $"Unknown notification status '{status}'.");
            q = q.Where(n => n.Status == st);
        }
        var rows = await q.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(take, 1, 1000))
            .Select(n => new { n, Number = db.Tasks.Where(t => t.Id == n.TaskId).Select(t => t.Number).FirstOrDefault() }).ToListAsync(ct);
        return rows.Select(r => new NotificationRow(r.n.Id, r.n.IdempotencyKey, r.n.TaskId, r.Number, r.n.RecipientName ?? r.n.RecipientAddress, r.n.Channel,
            r.n.Category, r.n.Subject, r.n.Status, r.n.AttemptCount, r.n.ScheduledAt, r.n.LastAttemptAt, r.n.NextAttemptAt, r.n.SentAt, r.n.Error,
            r.n.CreatedAt, r.n.ExpiresAt)).ToList();
    }

    public async Task<int> RetryNotificationsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        RequireAdmin();
        if (ids.Count == 0) return 0;
        var n = await dispatcher.RetryAsync(ids, ct);
        if (n > 0)
        {
            audit.Record("NotificationsRetried", nameof(Notification), null, details: $"{n} notification(s) re-queued");
            UiEvents.Publish(db, clock, UiEvents.NotificationQueued);
            await db.SaveChangesAsync(ct);
        }
        return n;
    }

    public async Task<List<IntegrationErrorDto>> ErrorsAsync(CancellationToken ct, int take = 200)
    {
        RequireMonitor();
        return await db.IntegrationErrors.AsNoTracking().OrderByDescending(e => e.OccurredAt).Take(Math.Clamp(take, 1, 1000))
            .Select(e => new IntegrationErrorDto(e.Id, e.Integration, e.SourceKey, e.Operation, e.Kind, e.StatusCode, e.Message, e.RetryAfterSeconds, e.OccurredAt, e.Resolved))
            .ToListAsync(ct);
    }

    public async Task<List<MessageDto>> FailedMessagesAsync(CancellationToken ct)
    {
        RequireAdmin();
        return [.. (await communications.ListAsync(new MessageFilter(null, null, MessageProcessingStatus.Failed, null, null, null, 1, 200), ct)).Items];
    }

    public async Task ReprocessMessageAsync(Guid messageId, CancellationToken ct)
    {
        await communications.GetAsync(messageId, ct);           // 404 unless visible to the caller
        await communications.ReprocessAsync(messageId, ct);
        audit.Record("MessageReprocess", nameof(CommunicationMessage), messageId);
        UiEvents.Publish(db, clock, UiEvents.CommunicationImported);
        await db.SaveChangesAsync(ct);
    }

    public async Task<AuditVerifyResult> VerifyAuditAsync(Guid taskId, CancellationToken ct)
    {
        RequireAdmin();
        var broken = await audit.VerifyTaskChainAsync(taskId, ct);
        return new AuditVerifyResult(broken is null, broken?.Id);
    }

    // ── System info ──

    public async Task<SystemInfoDto> SystemInfoAsync(CancellationToken ct)
    {
        var cal = await calendars.GetDefaultAsync(ct);
        string provider;
        try { provider = nl.ProviderName; } catch (Exception) { provider = "Unavailable"; }
        var version = typeof(AdminService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AdminService).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        var plus = version.IndexOf('+');
        return new SystemInfoDto(options.Value.OrganisationName, cal.TimeZone.Id, Mode, provider, await HostsAsync(ct), plus > 0 ? version[..plus] : version);
    }
}
