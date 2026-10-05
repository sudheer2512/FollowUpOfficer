using System.Text.Json;
using System.Text.Json.Nodes;
using FollowUp.Core.Common;
using FollowUp.Core.Contracts;
using FollowUp.Core.Data;
using FollowUp.Core.Jobs;
using FollowUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>Process-wide cache of the merged runtime settings (singleton).</summary>
public sealed class RuntimeSettingsCache
{
    public RuntimeSettings? Value { get; set; }
    public DateTime LoadedAt { get; set; }
    public void Invalidate() => LoadedAt = default;
}

/// <summary>
/// Runtime-editable settings (admin screen). Stored as JSON in AppSettings["runtime"] and merged over the
/// configured <see cref="FollowUpOptions"/>. The merged values are also written onto the live options instance so
/// every service that reads <c>IOptions&lt;FollowUpOptions&gt;</c> sees the overrides without a restart; other
/// processes (the Worker) pick them up through <see cref="RuntimeSettingsRefresher"/> within a minute.
/// </summary>
public sealed class SettingsService(
    FollowUpDbContext db,
    IOptions<FollowUpOptions> live,
    IOptionsFactory<FollowUpOptions> factory,
    RuntimeSettingsCache cache,
    CalendarProvider calendars,
    IClock clock,
    ActorContext actor,
    AuditService audit)
{
    public const string Key = "runtime";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public async Task<RuntimeSettings> GetAsync(CancellationToken ct = default)
    {
        if (cache.Value is { } cached && clock.UtcNow - cache.LoadedAt < Ttl) return cached;
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, ct);
        var merged = Merge(Baseline(), row?.Value);
        Apply(merged, live.Value);
        cache.Value = merged;
        cache.LoadedAt = clock.UtcNow;
        return merged;
    }

    public async Task<RuntimeSettings> UpdateAsync(RuntimeSettings s, CancellationToken ct)
    {
        if (!actor.IsSystem && !actor.Viewer.IsAdmin) throw new ForbiddenException("Only administrators can change settings.");
        Validate(s);
        var before = await GetAsync(ct);
        var json = JsonSerializer.Serialize(s, JsonDefaults.Options);
        var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == Key, ct);
        var now = clock.UtcNow;
        if (row is null) db.AppSettings.Add(new AppSetting { Key = Key, Value = json, UpdatedAt = now, UpdatedById = actor.EmployeeId });
        else { row.Value = json; row.UpdatedAt = now; row.UpdatedById = actor.EmployeeId; }
        audit.Record("SettingsChanged", nameof(AppSetting), null, null, "runtime",
            JsonSerializer.Serialize(before, JsonDefaults.Options), json);

        // The digest job keeps its own schedule row — move it when the digest time changes.
        if (before.DailyDigestTime != s.DailyDigestTime
            && await db.ScheduledJobs.FirstOrDefaultAsync(j => j.Key == DigestJob.JobKey, ct) is { } job)
        {
            job.TimeOfDay = s.DailyDigestTime;
            job.NextRunAt = JobEngine.NextScheduled(job, now, await calendars.GetDefaultAsync(ct));
        }
        await db.SaveChangesAsync(ct);

        Apply(s, live.Value);
        cache.Value = s;
        cache.LoadedAt = now;
        return s;
    }

    private RuntimeSettings Baseline()
    {
        var o = factory.Create(Options.DefaultName);
        return new RuntimeSettings
        {
            SeverelyOverdueDays = o.SeverelyOverdueDays,
            RecentUpdateWindowHours = o.RecentUpdateWindowHours,
            MaxRemindersPerRecipientPerDay = o.MaxRemindersPerRecipientPerDay,
            ReminderExpiryHours = o.ReminderExpiryHours,
            AutoAttachConfidence = o.AutoAttachConfidence,
            SuggestConfidence = o.SuggestConfidence,
            DailyDigestEnabled = o.DailyDigestEnabled,
            DailyDigestTime = o.DailyDigestTime,
            WeeklyReviewEnabled = o.WeeklyReviewEnabled,
            CalendarSyncEnabled = o.CalendarSyncEnabled,
            AcknowledgementFrom = o.AcknowledgementFrom,
            RepeatedNonResponseReminders = o.RepeatedNonResponseReminders,
        };
    }

    /// <summary>Stored keys override the configured baseline; unknown / missing keys keep the baseline.</summary>
    private static RuntimeSettings Merge(RuntimeSettings baseline, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return baseline;
        try
        {
            var node = JsonSerializer.SerializeToNode(baseline, JsonDefaults.Options)!.AsObject();
            if (JsonNode.Parse(stored) is JsonObject overrides)
                foreach (var (k, v) in overrides)
                    if (node.ContainsKey(k)) node[k] = v?.DeepClone();
            return node.Deserialize<RuntimeSettings>(JsonDefaults.Options) ?? baseline;
        }
        catch (JsonException) { return baseline; }
    }

    private static void Apply(RuntimeSettings s, FollowUpOptions o)
    {
        o.SeverelyOverdueDays = s.SeverelyOverdueDays;
        o.RecentUpdateWindowHours = s.RecentUpdateWindowHours;
        o.MaxRemindersPerRecipientPerDay = s.MaxRemindersPerRecipientPerDay;
        o.ReminderExpiryHours = s.ReminderExpiryHours;
        o.AutoAttachConfidence = s.AutoAttachConfidence;
        o.SuggestConfidence = s.SuggestConfidence;
        o.DailyDigestEnabled = s.DailyDigestEnabled;
        o.DailyDigestTime = s.DailyDigestTime;
        o.WeeklyReviewEnabled = s.WeeklyReviewEnabled;
        o.CalendarSyncEnabled = s.CalendarSyncEnabled;
        o.AcknowledgementFrom = s.AcknowledgementFrom;
        o.RepeatedNonResponseReminders = s.RepeatedNonResponseReminders;
    }

    private static void Validate(RuntimeSettings s)
    {
        static void Range(bool ok, string code, string message) { if (!ok) throw new ValidationException(code, message); }
        Range(s.SeverelyOverdueDays is >= 1 and <= 90, "severely_overdue_days", "Severely overdue must be between 1 and 90 days.");
        Range(s.RecentUpdateWindowHours is >= 1 and <= 168, "recent_update_window", "The recent-update window must be between 1 and 168 hours.");
        Range(s.MaxRemindersPerRecipientPerDay is >= 1 and <= 50, "max_reminders", "Reminders per person per day must be between 1 and 50.");
        Range(s.ReminderExpiryHours is >= 1 and <= 168, "reminder_expiry", "Reminder expiry must be between 1 and 168 hours.");
        Range(s.AutoAttachConfidence is >= 0.5 and <= 1.0, "auto_attach_confidence", "Auto-attach confidence must be between 0.5 and 1.");
        Range(s.SuggestConfidence >= 0.1 && s.SuggestConfidence < s.AutoAttachConfidence, "suggest_confidence",
            "Suggestion confidence must be at least 0.1 and below the auto-attach confidence.");
        Range(s.RepeatedNonResponseReminders is >= 1 and <= 20, "repeated_non_response", "Repeated non-response must be between 1 and 20 reminders.");
        Range(Enum.IsDefined(s.AcknowledgementFrom), "acknowledgement_from", "Unknown criticality.");
    }
}

/// <summary>
/// Re-reads runtime settings once a minute so a second process (the Worker service) applies changes made through
/// the API without a restart. Only runs where the background processor runs.
/// </summary>
public sealed class RuntimeSettingsRefresher(IServiceScopeFactory scopes, IOptions<JobEngineOptions> jobs, ILogger<RuntimeSettingsRefresher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!jobs.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { break; }
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Runtime settings refresh failed; keeping the previous values");
            }
        }
    }
}
