using System.Collections.Concurrent;
using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Jobs;

/// <summary>Static description of a background job and its default schedule.</summary>
public sealed record JobDescriptor(
    string Key,
    string Name,
    string Description,
    JobScheduleKind Kind,
    int IntervalSeconds = 60,
    TimeOnly? TimeOfDay = null,
    DayOfWeek? DayOfWeek = null,
    MisfirePolicy Misfire = MisfirePolicy.RunOnce,
    int MisfireWindowMinutes = 0,
    int TimeoutSeconds = 300,
    int MaxRetries = 5);

public sealed record JobResult(int Items, string? Message = null, string? Checkpoint = null);

public sealed class JobContext
{
    public required DateTime Now { get; init; }
    public required JobTrigger Trigger { get; init; }
    public DateTime? DueAt { get; init; }
    public string? Checkpoint { get; init; }
}

/// <summary>A unit of background work. Implementations are scoped and must be idempotent.</summary>
public interface IBackgroundJob
{
    JobDescriptor Descriptor { get; }
    Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct);
}

/// <summary>Identifies this process for leases and execution history.</summary>
public sealed class HostIdentity(string role)
{
    public string Role { get; } = role;
    public string Machine { get; } = Environment.MachineName;
    public string Id { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{role}";
}

/// <summary>
/// Durable job engine (spec §58–§65): the schedule, leases, checkpoints and execution history live in the
/// database, so jobs survive restarts and several hosts (the Worker service and the API) can run the engine
/// safely — a job runs on exactly one host at a time. On startup it recovers abandoned runs and applies each
/// job's misfire policy to runs missed while the system was down. Jobs have timeouts, retries with backoff and
/// lease heartbeats; a failed or timed-out run never loses work because every job resumes from persisted state.
/// </summary>
public sealed class JobEngine(
    IServiceScopeFactory scopes,
    IClock clock,
    HostIdentity host,
    CalendarProvider calendars,
    IOptions<JobEngineOptions> options,
    ILogger<JobEngine> logger) : BackgroundService
{
    private readonly JobEngineOptions _o = options.Value;
    private readonly ConcurrentDictionary<string, Task> _running = new();
    private readonly ConcurrentDictionary<string, JobTrigger> _catchUp = new();
    private Dictionary<string, JobDescriptor> _descriptors = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_o.Enabled)
        {
            logger.LogInformation("Background processing is disabled on this host ({Host})", host.Id);
            return;
        }
        await Task.Yield();
        // Survive starting before the database is reachable (Windows boot, SQL restart) — spec §61.
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await RegisterAndRecoverAsync(stoppingToken);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Job engine startup failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5 * attempt, 60)), stoppingToken);
            }
        }
        logger.LogInformation("Job engine started on {Host} with {Count} jobs", host.Id, _descriptors.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Job polling failed (database unavailable?); will retry");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _o.PollSeconds)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        // Graceful shutdown: let running jobs observe cancellation, then release leases so a restart resumes at once.
        var pending = _running.Values.ToArray();
        if (pending.Length > 0) await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None));
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            await db.ScheduledJobs.Where(j => j.LeaseOwner == host.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseOwner, (string?)null).SetProperty(j => j.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(j => j.State, JobState.Idle), CancellationToken.None);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not release job leases on shutdown"); }
    }

    private async Task RegisterAndRecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
        _descriptors = scope.ServiceProvider.GetServices<IBackgroundJob>().Select(j => j.Descriptor).ToDictionary(d => d.Key);
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);

        foreach (var d in _descriptors.Values)
        {
            var interval = _o.Intervals.TryGetValue(d.Key, out var custom) ? custom : d.IntervalSeconds;
            var job = await db.ScheduledJobs.FirstOrDefaultAsync(j => j.Key == d.Key, ct);
            if (job is null)
            {
                job = new ScheduledJob
                {
                    Key = d.Key, ScheduleKind = d.Kind, IntervalSeconds = interval, TimeOfDay = d.TimeOfDay, DayOfWeek = d.DayOfWeek,
                    Misfire = d.Misfire, MisfireWindowMinutes = d.MisfireWindowMinutes, TimeoutSeconds = d.TimeoutSeconds, MaxRetries = d.MaxRetries,
                };
                job.NextRunAt = d.Kind == JobScheduleKind.Interval ? now : NextScheduled(job, now, cal);
                db.ScheduledJobs.Add(job);
            }
            job.Name = d.Name;
            job.Description = d.Description;
            if (job.ScheduleKind == JobScheduleKind.Interval) job.IntervalSeconds = interval;
        }
        await db.SaveChangesAsync(ct);

        // Executions left "Running" by a process that died (crash, power loss, kill).
        var timeoutCutoff = now.AddMinutes(-60);
        var machinePrefix = host.Machine + ":";
        var abandoned = await db.JobExecutions.Where(e => e.Outcome == JobOutcome.Running
            && (e.StartedAt < timeoutCutoff || (e.Host.StartsWith(machinePrefix) && e.Host.EndsWith(":" + host.Role) && e.Host != host.Id))).ToListAsync(ct);
        foreach (var e in abandoned)
        {
            e.Outcome = JobOutcome.Abandoned;
            e.FinishedAt = now;
            e.Error = "The host stopped before this run finished; the next run resumes from the last persisted checkpoint.";
        }
        // Leases held by dead processes of this role on this machine are released immediately.
        await db.ScheduledJobs.Where(j => j.LeaseOwner != null && j.LeaseOwner.StartsWith(machinePrefix) && j.LeaseOwner.EndsWith(":" + host.Role) && j.LeaseOwner != host.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseOwner, (string?)null).SetProperty(j => j.LeaseExpiresAt, (DateTime?)null).SetProperty(j => j.State, JobState.Idle), ct);

        // Misfire handling for runs missed while nothing was running (spec §62).
        var jobs = await db.ScheduledJobs.Where(j => j.Enabled).ToListAsync(ct);
        foreach (var job in jobs.Where(j => j.NextRunAt is { } next && now - next > TimeSpan.FromSeconds(Math.Max(30, _o.PollSeconds * 3))))
        {
            var missedSince = job.NextRunAt!.Value;
            var skip = job.Misfire switch
            {
                MisfirePolicy.SkipToNext => true,
                MisfirePolicy.RunIfWithinWindow => now - missedSince > TimeSpan.FromMinutes(job.MisfireWindowMinutes),
                _ => false,
            };
            if (skip)
            {
                job.NextRunAt = NextScheduled(job, now, cal);
                db.JobExecutions.Add(new BackgroundJobExecution
                {
                    JobKey = job.Key, Trigger = JobTrigger.Startup, Host = host.Id, StartedAt = now, FinishedAt = now, Outcome = JobOutcome.Skipped, DueAt = missedSince,
                    Message = $"Run due {missedSince:u} was missed while the system was unavailable and is superseded by the next run at {job.NextRunAt:u} (policy {job.Misfire}).",
                });
                logger.LogInformation("Job {Job}: missed run at {Missed} skipped by misfire policy {Policy}", job.Key, missedSince, job.Misfire);
            }
            else
            {
                _catchUp[job.Key] = JobTrigger.CatchUp;
                logger.LogInformation("Job {Job}: catching up run missed since {Missed}", job.Key, missedSince);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task PollAsync(CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
        var now = clock.UtcNow;
        foreach (var done in _running.Where(r => r.Value.IsCompleted).Select(r => r.Key).ToList()) _running.TryRemove(done, out _);
        var due = await db.ScheduledJobs.AsNoTracking()
            .Where(j => j.Enabled && (j.RunRequested || j.NextRunAt <= now) && (j.LeaseExpiresAt == null || j.LeaseExpiresAt < now))
            .OrderBy(j => j.NextRunAt).ToListAsync(stoppingToken);
        foreach (var job in due)
        {
            if (_running.TryGetValue(job.Key, out var active) && !active.IsCompleted) continue;
            if (_running.Values.Count(t => !t.IsCompleted) >= Math.Max(1, _o.MaxConcurrency) || !_descriptors.ContainsKey(job.Key)) continue;
            var leaseUntil = now.AddSeconds(job.TimeoutSeconds + _o.LeaseGraceSeconds);
            var claimed = await db.ScheduledJobs
                .Where(j => j.Key == job.Key && (j.LeaseExpiresAt == null || j.LeaseExpiresAt < now))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseOwner, host.Id).SetProperty(j => j.LeaseExpiresAt, leaseUntil)
                    .SetProperty(j => j.State, JobState.Running).SetProperty(j => j.LastStartedAt, now).SetProperty(j => j.RunRequested, false), stoppingToken);
            if (claimed != 1) continue;   // another host won the lease
            var trigger = job.RunRequested ? JobTrigger.Manual : _catchUp.TryRemove(job.Key, out var t) ? t : JobTrigger.Schedule;
            _running[job.Key] = Task.Run(() => RunAsync(job, trigger, stoppingToken), CancellationToken.None);
        }
    }

    private async Task RunAsync(ScheduledJob job, JobTrigger trigger, CancellationToken stoppingToken)
    {
        var started = clock.UtcNow;
        var executionId = Ids.New();
        JobOutcome outcome;
        JobResult? result = null;
        string? error = null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(TimeSpan.FromSeconds(job.TimeoutSeconds));
        using var heartbeatCts = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(job, heartbeatCts.Token);
        try
        {
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
                db.JobExecutions.Add(new BackgroundJobExecution
                {
                    Id = executionId, JobKey = job.Key, Trigger = trigger, Host = host.Id, StartedAt = started, Outcome = JobOutcome.Running,
                    DueAt = job.NextRunAt, Attempt = job.ConsecutiveFailures + 1,
                });
                await db.SaveChangesAsync(stoppingToken);
            }
            using (var scope = scopes.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ActorContext>().SetSystem();
                var impl = scope.ServiceProvider.GetServices<IBackgroundJob>().First(j => j.Descriptor.Key == job.Key);
                result = await impl.ExecuteAsync(new JobContext { Now = started, Trigger = trigger, DueAt = job.NextRunAt, Checkpoint = job.Checkpoint }, cts.Token);
            }
            outcome = JobOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            outcome = JobOutcome.Cancelled;
            error = "Host shutting down; resumes from the last checkpoint on restart.";
        }
        catch (OperationCanceledException)
        {
            outcome = JobOutcome.TimedOut;
            error = $"Timed out after {job.TimeoutSeconds} s; work already committed is kept.";
        }
        catch (Exception ex)
        {
            outcome = JobOutcome.Failed;
            error = ex.ToString();
            logger.LogError(ex, "Job {Job} failed", job.Key);
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
            var cal = await calendars.GetDefaultAsync(CancellationToken.None);
            var now = clock.UtcNow;
            var current = await db.ScheduledJobs.AsNoTracking().FirstAsync(j => j.Key == job.Key, CancellationToken.None);
            var failures = outcome == JobOutcome.Succeeded ? 0 : outcome == JobOutcome.Cancelled ? current.ConsecutiveFailures : current.ConsecutiveFailures + 1;
            var next = outcome switch
            {
                JobOutcome.Succeeded => NextScheduled(current, now, cal),
                JobOutcome.Cancelled => now,
                _ when failures <= current.MaxRetries => now + Backoff(current, failures),
                _ => NextScheduled(current, now, cal),
            };
            var lastError = error is null ? null : error.Length > 3900 ? error[..3900] : error;
            await db.ScheduledJobs.Where(j => j.Key == job.Key && j.LeaseOwner == host.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, outcome == JobOutcome.Succeeded || outcome == JobOutcome.Cancelled ? JobState.Idle : failures > current.MaxRetries ? JobState.Failed : JobState.Idle)
                .SetProperty(j => j.NextRunAt, next)
                .SetProperty(j => j.LastSucceededAt, outcome == JobOutcome.Succeeded ? now : current.LastSucceededAt)
                .SetProperty(j => j.LastFailedAt, outcome is JobOutcome.Failed or JobOutcome.TimedOut ? now : current.LastFailedAt)
                .SetProperty(j => j.LastError, outcome == JobOutcome.Succeeded ? null : lastError)
                .SetProperty(j => j.ConsecutiveFailures, failures)
                .SetProperty(j => j.Checkpoint, result != null && result.Checkpoint != null ? result.Checkpoint : current.Checkpoint)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null), CancellationToken.None);
            await db.JobExecutions.Where(e => e.Id == executionId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Outcome, outcome)
                .SetProperty(e => e.FinishedAt, now)
                .SetProperty(e => e.ItemsProcessed, result == null ? 0 : result.Items)
                .SetProperty(e => e.Message, result == null ? null : result.Message)
                .SetProperty(e => e.Error, lastError), CancellationToken.None);
            if (outcome != JobOutcome.Succeeded || (result?.Items ?? 0) > 0)
            {
                db.UiEvents.Add(new UiEvent { At = now, Topic = "job.status", Payload = job.Key });
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Database unavailable: the lease expires and the job runs again — its work is idempotent.
            logger.LogWarning(ex, "Could not record the outcome of job {Job}", job.Key);
        }
    }

    private async Task HeartbeatAsync(ScheduledJob job, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FollowUpDbContext>();
                var until = clock.UtcNow.AddSeconds(job.TimeoutSeconds + _o.LeaseGraceSeconds);
                await db.ScheduledJobs.Where(j => j.Key == job.Key && j.LeaseOwner == host.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, until), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Lease heartbeat failed for {Job}", job.Key); }
        }
    }

    private static TimeSpan Backoff(ScheduledJob job, int failures)
    {
        var cap = job.ScheduleKind == JobScheduleKind.Interval ? Math.Min(job.IntervalSeconds * 4, 900) : 900;
        return TimeSpan.FromSeconds(Math.Min(15 * Math.Pow(2, failures - 1), Math.Max(cap, 30)));
    }

    /// <summary>Next scheduled run strictly after <paramref name="now"/> (local wall-clock time for daily / weekly jobs).</summary>
    public static DateTime NextScheduled(ScheduledJob job, DateTime now, BusinessCalendar cal)
    {
        switch (job.ScheduleKind)
        {
            case JobScheduleKind.DailyAt:
            {
                var time = job.TimeOfDay ?? new TimeOnly(8, 0);
                var today = cal.Today(now);
                var candidate = cal.ToUtc(today, time);
                return candidate > now ? candidate : cal.ToUtc(today.AddDays(1), time);
            }
            case JobScheduleKind.WeeklyAt:
            {
                var time = job.TimeOfDay ?? new TimeOnly(9, 0);
                var day = job.DayOfWeek ?? System.DayOfWeek.Monday;
                var d = cal.Today(now);
                for (var i = 0; i < 8; i++, d = d.AddDays(1))
                    if (d.DayOfWeek == day && cal.ToUtc(d, time) > now) return cal.ToUtc(d, time);
                return now.AddDays(7);
            }
            default:
                return now.AddSeconds(Math.Max(5, job.IntervalSeconds));
        }
    }
}
