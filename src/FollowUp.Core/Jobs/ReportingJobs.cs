using FollowUp.Core.Common;
using FollowUp.Core.Services;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Jobs;

/// <summary>Daily CEO digest (spec §54). A digest that is more than 4 hours late (downtime) is skipped, not sent.</summary>
public sealed class DigestJob(DigestService digest, SettingsService settings, IOptions<FollowUpOptions> options) : IBackgroundJob
{
    public const string JobKey = "digest.daily";
    private const int WindowMinutes = 240;

    public JobDescriptor Descriptor { get; } = new(JobKey, "Daily CEO digest",
        "Queues the exception-oriented daily digest (email + in-app) for every executive, once per day.",
        JobScheduleKind.DailyAt, TimeOfDay: options.Value.DailyDigestTime, Misfire: MisfirePolicy.RunIfWithinWindow, MisfireWindowMinutes: WindowMinutes);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (!s.DailyDigestEnabled) return new(0, "Daily digest is disabled in settings");
        if (context.Trigger != JobTrigger.Manual && context.DueAt is { } due && context.Now - due > TimeSpan.FromMinutes(WindowMinutes))
            return new(0, $"Digest due {due:u} is more than {WindowMinutes / 60} hours late and was skipped");
        var n = await digest.SendDailyDigestAsync(ct);
        return new(n, n > 0 ? $"{n} digest notification(s) queued" : "Today's digest was already queued");
    }
}

/// <summary>Weekly executive review (spec §55) for the previous week, Mondays 09:00 by default.</summary>
public sealed class WeeklyReviewJob(DigestService digest, SettingsService settings, CalendarProvider calendars, IOptions<FollowUpOptions> options) : IBackgroundJob
{
    public const string JobKey = "review.weekly";

    public JobDescriptor Descriptor { get; } = new(JobKey, "Weekly executive review",
        "Queues the weekly executive review of the previous week for every executive.",
        JobScheduleKind.WeeklyAt, TimeOfDay: options.Value.WeeklyReviewTime, DayOfWeek: options.Value.WeeklyReviewDay,
        Misfire: MisfirePolicy.RunIfWithinWindow, MisfireWindowMinutes: 1440);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (!s.WeeklyReviewEnabled) return new(0, "Weekly review is disabled in settings");
        var cal = await calendars.GetDefaultAsync(ct);
        var reference = cal.Today(context.DueAt ?? context.Now);
        var weekStart = BusinessCalendar.StartOfWeek(reference).AddDays(-7);
        var n = await digest.SendWeeklyReviewAsync(weekStart, ct);
        return new(n, n > 0 ? $"Weekly review for the week of {weekStart:d MMM} queued ({n} notification(s))" : "This week's review was already queued");
    }
}

/// <summary>Outlook calendar sync for important deadlines, reviews and approvals (spec §72).</summary>
public sealed class CalendarSyncJob(CalendarSyncService service) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("calendar.sync", "Outlook calendar sync",
        "Creates, updates and removes calendar events for important deadlines, review dates and pending approvals (only when they change).",
        JobScheduleKind.Interval, 600, TimeoutSeconds: 600);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await service.SyncAsync(ct);
        return new(n, n > 0 ? $"{n} calendar event(s) created, updated or removed" : null);
    }
}

/// <summary>Nightly directory sync with Entra ID / Microsoft 365 (spec §71).</summary>
public sealed class DirectoryJob(OrgService org) : IBackgroundJob
{
    public JobDescriptor Descriptor { get; } = new("directory.sync", "Directory synchronisation",
        "Matches Microsoft 365 users to employees by email (external ids, blank job titles) and Teams teams by name. Never deletes anyone.",
        JobScheduleKind.DailyAt, TimeOfDay: new TimeOnly(2, 0), Misfire: MisfirePolicy.RunOnce);

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var n = await org.SyncDirectoryAsync(ct);
        return new(n, $"{n} directory record(s) updated");
    }
}
