using System.Globalization;
using System.Net;
using System.Text;
using FollowUp.Core.Common;
using FollowUp.Core.Data;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Services;

/// <summary>What a notification is about — the composer turns it into an email / Teams / in-app message.</summary>
public sealed record NoticeRequest
{
    public required string Key { get; init; }
    public required NotificationCategory Category { get; init; }
    public WorkTask? Task { get; init; }
    public required IReadOnlyCollection<Guid> RecipientIds { get; init; }
    /// <summary>Headline line, e.g. "requires an update" → "Task TASK-2026-00142 requires an update."</summary>
    public required string Headline { get; init; }
    public string? Message { get; init; }
    public string? Reason { get; init; }
    public NotifyChannels? Channels { get; init; }
    public bool Urgent { get; init; }
    /// <summary>Newer notices with the same supersede key cancel older pending ones for the same recipient/channel.</summary>
    public string? SupersedeKey { get; init; }
    public TimeSpan? ExpiresIn { get; init; }
    public DateTime? ScheduledAt { get; init; }
    /// <summary>Extra lines appended to the body (digest content etc.).</summary>
    public IReadOnlyList<string>? ExtraLines { get; init; }
    public string? CustomSubject { get; init; }
}

/// <summary>
/// Enqueues notifications in the persistent queue (spec §19, §20, §66) as part of the caller's unit of work —
/// the business change and its notifications commit together (transactional outbox). Delivery is done later
/// by <see cref="NotificationDispatcher"/>.
/// </summary>
public sealed class NotificationService(FollowUpDbContext db, IClock clock, CalendarProvider calendars, IOptions<FollowUpOptions> options)
    : INotificationService
{
    private readonly FollowUpOptions _o = options.Value;

    /// <summary>Default channels by criticality (spec §18: criticality affects notification channels).</summary>
    public static NotifyChannels ChannelsFor(Criticality c) => c switch
    {
        Criticality.Informational => NotifyChannels.InApp,
        Criticality.Normal => NotifyChannels.InApp | NotifyChannels.Email,
        _ => NotifyChannels.InApp | NotifyChannels.Email | NotifyChannels.Teams,
    };

    public async Task<int> EnqueueAsync(NoticeRequest n, CancellationToken ct)
    {
        if (n.RecipientIds.Count == 0) return 0;
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var recipients = await db.Employees.AsNoTracking().Where(e => n.RecipientIds.Contains(e.Id) && e.IsActive).ToListAsync(ct);
        var channels = n.Channels ?? (n.Task is { } t ? ChannelsFor(t.Criticality) | (n.Urgent ? NotifyChannels.Teams : NotifyChannels.None) : NotifyChannels.InApp | NotifyChannels.Email);
        var context = n.Task is { } task ? await LoadContextAsync(task, ct) : null;
        var count = 0;

        foreach (var r in recipients)
        {
            var effective = channels & (r.PreferredChannels | NotifyChannels.InApp);
            // Urgent / critical notices always reach email even if the person prefers Teams only.
            if (n.Urgent) effective |= channels & NotifyChannels.Email;
            foreach (var channel in Expand(effective, r))
            {
                var key = $"{n.Key}:{r.Id:N}:{channel}";
                if (db.Notifications.Local.Any(x => x.IdempotencyKey == key)
                    || await db.Notifications.AnyAsync(x => x.IdempotencyKey == key, ct)) continue;

                var supersede = n.SupersedeKey is null ? null : $"{n.SupersedeKey}:{r.Id:N}:{channel}";
                if (supersede is not null)
                {
                    var older = await db.Notifications.Where(x => x.SupersedeKey == supersede && x.Status == NotificationStatus.Pending).ToListAsync(ct);
                    foreach (var o in older) { o.Status = NotificationStatus.Cancelled; o.Error = "Superseded by a newer notice"; }
                }

                var (subject, text, html) = Compose(n, r, channel, context, now, cal);
                db.Notifications.Add(new Notification
                {
                    IdempotencyKey = key,
                    TaskId = n.Task?.Id,
                    RecipientEmployeeId = r.Id,
                    RecipientAddress = channel == NotificationChannel.Email ? r.Email : r.ExternalUserId ?? r.Email,
                    RecipientName = r.DisplayName,
                    Channel = channel,
                    Category = n.Category,
                    Subject = subject,
                    Body = text,
                    BodyHtml = html,
                    Priority = n.Urgent ? 2 : n.Task?.Criticality >= Criticality.Critical ? 1 : 0,
                    ScheduledAt = n.ScheduledAt ?? now,
                    ExpiresAt = n.ExpiresIn is { } exp ? now + exp : null,
                    SupersedeKey = supersede,
                    Status = channel == NotificationChannel.InApp ? NotificationStatus.Sent : NotificationStatus.Pending,
                    SentAt = channel == NotificationChannel.InApp ? now : null,
                    CreatedAt = now,
                });
                count++;
            }
            UiEvents.Publish(db, clock, UiEvents.InboxChanged, n.Task?.Id, r.Id);
        }
        if (count > 0) UiEvents.Publish(db, clock, UiEvents.NotificationQueued, n.Task?.Id);
        return count;
    }

    /// <summary>Posts a message to a team's Teams channel (team-level reminders, spec §20 "Team / Channel").</summary>
    public async Task<bool> EnqueueChannelAsync(string key, WorkTask task, Team team, NotificationCategory category, string headline, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(team.TeamsTeamId) || string.IsNullOrEmpty(team.TeamsChannelId)) return false;
        var fullKey = $"{key}:team:{team.Id:N}";
        if (db.Notifications.Local.Any(x => x.IdempotencyKey == fullKey) || await db.Notifications.AnyAsync(x => x.IdempotencyKey == fullKey, ct)) return false;
        var now = clock.UtcNow;
        var cal = await calendars.GetDefaultAsync(ct);
        var context = await LoadContextAsync(task, ct);
        var n = new NoticeRequest { Key = key, Category = category, Task = task, RecipientIds = [], Headline = headline, Reason = reason };
        var (subject, text, html) = Compose(n, null, NotificationChannel.TeamsChannel, context, now, cal);
        db.Notifications.Add(new Notification
        {
            IdempotencyKey = fullKey,
            TaskId = task.Id,
            RecipientName = team.Name,
            RecipientAddress = team.TeamsChannelName,
            TargetConversation = $"{team.TeamsTeamId}|{team.TeamsChannelId}",
            Channel = NotificationChannel.TeamsChannel,
            Category = category,
            Subject = subject,
            Body = text,
            BodyHtml = html,
            ScheduledAt = now,
            ExpiresAt = now.AddHours(_o.ReminderExpiryHours),
            CreatedAt = now,
        });
        return true;
    }

    private static IEnumerable<NotificationChannel> Expand(NotifyChannels channels, Employee r)
    {
        if (channels.HasFlag(NotifyChannels.InApp)) yield return NotificationChannel.InApp;
        if (channels.HasFlag(NotifyChannels.Email) && !string.IsNullOrEmpty(r.Email)) yield return NotificationChannel.Email;
        if (channels.HasFlag(NotifyChannels.Teams) && !string.IsNullOrEmpty(r.ExternalUserId)) yield return NotificationChannel.TeamsChat;
    }

    /// <summary>Counts reminder-type notifications already queued today for a recipient (anti-spam cap).</summary>
    public async Task<int> RemindersTodayAsync(Guid recipientId, CancellationToken ct)
    {
        var cal = await calendars.GetDefaultAsync(ct);
        var startOfDay = cal.ToUtc(cal.Today(clock.UtcNow), TimeOnly.MinValue);
        return await db.Notifications.CountAsync(n => n.RecipientEmployeeId == recipientId && n.CreatedAt >= startOfDay
            && n.Channel != NotificationChannel.InApp
            && (n.Category == NotificationCategory.Reminder || n.Category == NotificationCategory.DueSoon || n.Category == NotificationCategory.DueToday
                || n.Category == NotificationCategory.Overdue), ct);
    }

    // ───────────────────────────── Composition (spec §19 example) ─────────────────────────────

    private sealed record TaskContext(string Status, string? AssigneeName, string? LastUpdate, string? Blockers, string? TeamName);

    private async Task<TaskContext> LoadContextAsync(WorkTask t, CancellationToken ct)
    {
        var assignee = t.PrimaryAssigneeId is { } a ? await db.Employees.Where(e => e.Id == a).Select(e => e.DisplayName).FirstOrDefaultAsync(ct) : null;
        var team = t.TeamId is { } tid ? await db.Teams.Where(x => x.Id == tid).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var blockers = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.BlockedReason)) blockers.Add(t.BlockedReason!);
        var deps = await db.TaskDependencies.Where(d => d.TaskId == t.Id && !d.IsResolved)
            .Select(d => new { d.ExternalDescription, Number = d.DependsOnTask != null ? d.DependsOnTask.Number : null, Status = d.DependsOnTask != null ? (WorkStatus?)d.DependsOnTask.Status : null })
            .ToListAsync(ct);
        foreach (var d in deps)
        {
            if (d.Number is not null && d.Status is { } s && !TaskStatusRules.IsClosed(s)) blockers.Add($"Blocked by {d.Number}");
            else if (d.ExternalDescription is not null) blockers.Add($"Waiting for {d.ExternalDescription}");
        }
        string? lastUpdate = t.LastUpdateSummary;
        if (lastUpdate is null)
        {
            var c = await db.TaskComments.Where(x => x.TaskId == t.Id && !x.IsSystem).OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.Body, x.CreatedAt }).FirstOrDefaultAsync(ct);
            if (c is not null) lastUpdate = $"{Shorten(c.Body, 160)} ({c.CreatedAt:d MMM})";
        }
        return new TaskContext(Text.Humanize(t.Status), assignee, lastUpdate, blockers.Count > 0 ? string.Join("; ", blockers) : null, team);
    }

    private (string Subject, string Text, string Html) Compose(NoticeRequest n, Employee? recipient, NotificationChannel channel, TaskContext? ctx, DateTime now, BusinessCalendar cal)
    {
        var t = n.Task;
        var link = t is null ? _o.PublicAppUrl : $"{_o.PublicAppUrl.TrimEnd('/')}/tasks/{t.Id}";
        var subjectTitle = CategoryTitle(n.Category);
        var subject = n.CustomSubject ?? (t is null ? subjectTitle : $"[{t.Number}] {subjectTitle} — {t.Title}");
        var headline = t is null ? n.Headline : $"Task {t.Number} {n.Headline}";

        var lines = new List<(string Label, string Value)>();
        if (t is not null)
        {
            lines.Add(("Task", $"{t.Number} — {t.Title}"));
            lines.Add(("Current status", ctx?.Status ?? Text.Humanize(t.Status)));
            if (ctx?.AssigneeName is { } who) lines.Add(("Responsible", who));
            if (t.DueDate is { } due)
            {
                lines.Add(("Due date", due.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)));
                lines.Add(("Days remaining / overdue", DeadlineRules.Describe(t.DueDate, t.DueTime, t.Status, now, cal)));
            }
            lines.Add(("Last update", ctx?.LastUpdate ?? "No update received yet"));
            if (ctx?.Blockers is { } b) lines.Add(("Current blockers", b));
            if (n.Reason is { } reason) lines.Add(("Why you are receiving this", reason));
        }

        var greeting = recipient is null ? "Hello team," : $"Hello {FirstName(recipient.DisplayName)},";
        var text = new StringBuilder();
        text.AppendLine(greeting).AppendLine().AppendLine(headline);
        if (n.Message is { } m) text.AppendLine().AppendLine(m);
        if (lines.Count > 0) text.AppendLine();
        foreach (var (label, value) in lines) text.AppendLine($"{label}: {value}");
        if (n.ExtraLines is { } extra) { text.AppendLine(); foreach (var l in extra) text.AppendLine(l); }
        text.AppendLine().AppendLine($"Open the task: {link}");
        if (t is not null) text.AppendLine().AppendLine($"Reply to this message with an update — mention {t.Number} and it will be linked automatically.");
        text.AppendLine().Append("— Digital Follow-up Officer");

        var html = new StringBuilder();
        if (channel is NotificationChannel.TeamsChat or NotificationChannel.TeamsChannel)
        {
            html.Append($"<p><b>{E(headline)}</b></p>");
            if (n.Message is { } tm) html.Append($"<p>{E(tm)}</p>");
            if (t is not null)
            {
                html.Append("<p>");
                foreach (var (label, value) in lines.Take(5)) html.Append($"{E(label)}: {E(value)}<br/>");
                html.Append("</p>");
            }
            html.Append($"<p><a href=\"{E(link)}\">Open {E(t?.Number ?? "Follow-up Officer")}</a></p>");
            // Signature doubles as the loop guard: synchronised copies of our own posts are never re-processed.
            html.Append("<p>— Digital Follow-up Officer</p>");
        }
        else
        {
            html.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2937\">");
            html.Append($"<p>{E(greeting)}</p><p style=\"font-size:16px\"><b>{E(headline)}</b></p>");
            if (n.Message is { } hm) html.Append($"<p>{E(hm)}</p>");
            if (lines.Count > 0)
            {
                html.Append("<table cellpadding=\"6\" style=\"border-collapse:collapse;border:1px solid #e5e7eb\">");
                foreach (var (label, value) in lines)
                    html.Append($"<tr><td style=\"background:#f9fafb;border:1px solid #e5e7eb;color:#6b7280\">{E(label)}</td><td style=\"border:1px solid #e5e7eb\">{E(value)}</td></tr>");
                html.Append("</table>");
            }
            if (n.ExtraLines is { } extra2)
            {
                html.Append("<ul>");
                foreach (var l in extra2) html.Append($"<li>{E(l)}</li>");
                html.Append("</ul>");
            }
            html.Append($"<p><a href=\"{E(link)}\" style=\"background:#1d4ed8;color:#fff;padding:8px 14px;border-radius:6px;text-decoration:none\">Open {E(t?.Number ?? "Follow-up Officer")}</a></p>");
            if (t is not null) html.Append($"<p style=\"color:#6b7280\">Reply to this email with an update — mention {E(t.Number)} and it will be linked to the task automatically.</p>");
            html.Append("<p style=\"color:#6b7280\">— Digital Follow-up Officer</p></div>");
        }

        if (channel == NotificationChannel.InApp) return (headline, n.Message ?? n.Reason ?? headline, html.ToString());
        return (subject, text.ToString(), html.ToString());
    }

    public static string CategoryTitle(NotificationCategory c) => c switch
    {
        NotificationCategory.Assignment => "New Task Assigned",
        NotificationCategory.AcknowledgementRequest => "Please Acknowledge",
        NotificationCategory.Reminder => "Follow-up Required",
        NotificationCategory.DueSoon => "Due Soon",
        NotificationCategory.DueToday => "Due Today",
        NotificationCategory.Overdue => "Overdue",
        NotificationCategory.Escalation => "Escalation",
        NotificationCategory.CommitmentMissed => "Commitment Missed",
        NotificationCategory.ApprovalRequest => "Completion Awaiting Your Confirmation",
        NotificationCategory.ApprovalDecision => "Completion Decision",
        NotificationCategory.ExtensionRequest => "Deadline Extension Requested",
        NotificationCategory.ExtensionDecision => "Deadline Extension Decision",
        NotificationCategory.StatusChange => "Status Update",
        NotificationCategory.Comment => "New Comment",
        NotificationCategory.CeoAlert => "CEO Attention Required",
        NotificationCategory.Digest => "CEO Task Digest",
        NotificationCategory.WeeklyReview => "Weekly Executive Review",
        _ => "Notification",
    };

    private static string FirstName(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
    private static string E(string s) => WebUtility.HtmlEncode(s);
    public static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
