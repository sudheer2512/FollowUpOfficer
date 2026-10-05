using System.Globalization;

namespace FollowUp.Domain.Rules;

/// <summary>
/// How often a task is followed up (spec §9): none, daily, every N days, weekly, fortnightly, monthly,
/// specific weekdays ("every Monday and Thursday until completed"), specific dates or a custom interval.
/// </summary>
public sealed record FollowUpSchedule
{
    public static FollowUpSchedule NoneSchedule { get; } = new() { Frequency = FollowUpFrequency.None };

    public FollowUpFrequency Frequency { get; init; } = FollowUpFrequency.None;
    public int IntervalDays { get; init; } = 1;
    /// <summary>Custom schedules only: every N hours within working hours (critical tasks).</summary>
    public int IntervalHours { get; init; }
    public List<DayOfWeek> Weekdays { get; init; } = [];
    public List<DateOnly> Dates { get; init; } = [];
    public TimeOnly TimeOfDay { get; init; } = new(10, 0);
    public bool WorkingDaysOnly { get; init; } = true;
    public DateOnly? Until { get; init; }

    public bool IsEnabled => Frequency != FollowUpFrequency.None;

    public static FollowUpSchedule Daily(TimeOnly? at = null) => new() { Frequency = FollowUpFrequency.Daily, TimeOfDay = at ?? new(10, 0) };
    public static FollowUpSchedule EveryDays(int days, TimeOnly? at = null) => new() { Frequency = FollowUpFrequency.EveryNDays, IntervalDays = Math.Max(1, days), TimeOfDay = at ?? new(10, 0) };
    public static FollowUpSchedule Weekly(DayOfWeek day, TimeOnly? at = null) => new() { Frequency = FollowUpFrequency.Weekly, Weekdays = [day], TimeOfDay = at ?? new(10, 0) };
    public static FollowUpSchedule OnWeekdays(params DayOfWeek[] days) => new() { Frequency = FollowUpFrequency.SpecificWeekdays, Weekdays = [.. days] };

    public string Describe()
    {
        var at = $" at {TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        return Frequency switch
        {
            FollowUpFrequency.None => "No automatic follow-up",
            FollowUpFrequency.Daily => "Daily" + at,
            FollowUpFrequency.EveryNDays => $"Every {IntervalDays} days" + at,
            FollowUpFrequency.Weekly => $"Weekly{(Weekdays.Count > 0 ? " on " + Weekdays[0] : "")}" + at,
            FollowUpFrequency.Fortnightly => "Fortnightly" + at,
            FollowUpFrequency.Monthly => "Monthly" + at,
            FollowUpFrequency.SpecificWeekdays => $"Every {string.Join(" and ", Weekdays)}" + at,
            FollowUpFrequency.SpecificDates => $"On {string.Join(", ", Dates.Select(d => d.ToString("d MMM", CultureInfo.InvariantCulture)))}" + at,
            FollowUpFrequency.Custom => IntervalHours > 0 ? $"Every {IntervalHours} working hours" : $"Every {IntervalDays} days" + at,
            _ => Frequency.ToString(),
        } + (Until is { } u ? $" until {u.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}" : " until completed");
    }
}

/// <summary>Computes follow-up points for a schedule on a business calendar.</summary>
public static class FollowUpScheduler
{
    /// <summary>
    /// The next follow-up instant strictly after <paramref name="afterUtc"/>.
    /// <paramref name="anchor"/> is the task's start / assignment date — the cadence origin for weekly, fortnightly and monthly schedules.
    /// </summary>
    public static DateTime? Next(FollowUpSchedule s, DateTime afterUtc, DateOnly anchor, BusinessCalendar cal)
    {
        if (!s.IsEnabled) return null;
        var afterLocal = cal.ToLocal(afterUtc);
        var afterDate = DateOnly.FromDateTime(afterLocal);
        DateTime? result = s.Frequency switch
        {
            FollowUpFrequency.Daily => NextMatching(s, afterUtc, afterDate, cal, _ => true),
            FollowUpFrequency.EveryNDays => EveryDays(s, afterUtc, afterDate, cal, Math.Max(1, s.IntervalDays)),
            FollowUpFrequency.Weekly => NextMatching(s, afterUtc, afterDate, cal,
                d => d.DayOfWeek == (s.Weekdays.Count > 0 ? s.Weekdays[0] : anchor.DayOfWeek), rollToWorkingDay: true),
            FollowUpFrequency.Fortnightly => NextMatching(s, afterUtc, afterDate, cal,
                d => (d.DayNumber - anchor.DayNumber) % 14 == 0 && d >= anchor, rollToWorkingDay: true),
            FollowUpFrequency.Monthly => NextMatching(s, afterUtc, afterDate, cal,
                d => d.Day == Math.Min(anchor.Day, DateTime.DaysInMonth(d.Year, d.Month)), rollToWorkingDay: true),
            FollowUpFrequency.SpecificWeekdays => NextMatching(s, afterUtc, afterDate, cal,
                d => (s.Weekdays.Count == 0 ? [anchor.DayOfWeek] : s.Weekdays).Contains(d.DayOfWeek)),
            FollowUpFrequency.SpecificDates => s.Dates.Order()
                .Select(d => (DateTime?)cal.ToUtc(d, s.TimeOfDay))
                .FirstOrDefault(t => t > afterUtc),
            FollowUpFrequency.Custom => s.IntervalHours > 0 ? EveryHours(s, afterUtc, cal) : EveryDays(s, afterUtc, afterDate, cal, Math.Max(1, s.IntervalDays)),
            _ => null,
        };
        if (result is { } r && s.Until is { } until && DateOnly.FromDateTime(cal.ToLocal(r)) > until) return null;
        return result;
    }

    private static DateTime? NextMatching(FollowUpSchedule s, DateTime afterUtc, DateOnly afterDate, BusinessCalendar cal,
        Func<DateOnly, bool> match, bool rollToWorkingDay = false)
    {
        for (var i = 0; i <= 400; i++)
        {
            var d = afterDate.AddDays(i);
            if (!match(d)) continue;
            var day = d;
            if (s.WorkingDaysOnly && !cal.IsWorkingDay(day))
            {
                if (!rollToWorkingDay) continue;
                day = cal.NextWorkingDay(day);
            }
            var t = cal.ToUtc(day, s.TimeOfDay);
            if (t > afterUtc) return t;
        }
        return null;
    }

    private static DateTime? EveryDays(FollowUpSchedule s, DateTime afterUtc, DateOnly afterDate, BusinessCalendar cal, int days)
    {
        var d = afterDate.AddDays(days);
        if (s.WorkingDaysOnly) d = cal.NextWorkingDay(d);
        var t = cal.ToUtc(d, s.TimeOfDay);
        return t > afterUtc ? t : cal.ToUtc(s.WorkingDaysOnly ? cal.NextWorkingDay(d.AddDays(1)) : d.AddDays(1), s.TimeOfDay);
    }

    private static DateTime? EveryHours(FollowUpSchedule s, DateTime afterUtc, BusinessCalendar cal)
    {
        var candidate = afterUtc.AddHours(s.IntervalHours);
        if (!s.WorkingDaysOnly) return candidate;
        var local = cal.ToLocal(candidate);
        var date = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        if (cal.IsWorkingDay(date) && time >= cal.WorkdayStart && time <= cal.WorkdayEnd) return candidate;
        var next = time < cal.WorkdayStart && cal.IsWorkingDay(date) ? date : cal.NextWorkingDay(date, inclusive: false);
        return cal.ToUtc(next, cal.WorkdayStart);
    }

    /// <summary>
    /// All follow-up points in (fromUtc, toUtc] — used after downtime to count the points that were missed
    /// so they can be coalesced into a single reminder instead of a flood (spec §62).
    /// </summary>
    public static int CountPoints(FollowUpSchedule s, DateTime fromUtc, DateTime toUtc, DateOnly anchor, BusinessCalendar cal, int cap = 500)
    {
        var n = 0;
        var cursor = fromUtc;
        while (n < cap)
        {
            var next = Next(s, cursor, anchor, cal);
            if (next is null || next > toUtc) break;
            n++;
            cursor = next.Value;
        }
        return n;
    }
}

/// <summary>Everything the follow-up decision needs to know about a task at a follow-up point.</summary>
public sealed record FollowUpContext
{
    public DateTime Now { get; init; }
    public DateOnly Today { get; init; }
    public WorkStatus Status { get; init; }
    public Criticality Criticality { get; init; }
    public AcknowledgementState Acknowledgement { get; init; }
    public DateOnly? DueDate { get; init; }
    public bool IsOverdue { get; init; }
    public int DaysOverdue { get; init; }
    public int? DaysUntilDue { get; init; }
    public DateOnly? PausedUntil { get; init; }
    public FollowUpPauseReason? PauseReason { get; init; }
    public DateTime? LastAssigneeActivityAt { get; init; }
    public string? LastActivityChannel { get; init; }
    public DateTime? LastReminderAt { get; init; }
    public int RemindersSinceResponse { get; init; }
    public bool IsBlocked { get; init; }
    public string? BlockedReason { get; init; }
    public int PendingDependencies { get; init; }
    public DateOnly? OpenCommitmentDate { get; init; }
    public bool HasMissedCommitment { get; init; }
    /// <summary>A recent update inside this window suppresses the reminder (spec §10 — avoid notification spam).</summary>
    public TimeSpan RecentUpdateWindow { get; init; } = TimeSpan.FromHours(24);
}

public sealed record FollowUpDecision(FollowUpAction Action, FollowUpKind Kind, string Reason, bool IncludeTeamLeader = false, string Tone = "neutral");

/// <summary>
/// Intelligent follow-up (spec §10): at each follow-up point decide whether a reminder is needed and which kind,
/// instead of blindly sending one.
/// </summary>
public static class FollowUpDecisionEngine
{
    public static FollowUpDecision Decide(FollowUpContext c)
    {
        if (TaskStatusRules.IsClosed(c.Status))
            return new(FollowUpAction.Stop, FollowUpKind.None, $"Task is {Text.Humanize(c.Status)} — follow-up stopped");
        if (c.Status == WorkStatus.Draft)
            return new(FollowUpAction.Skip, FollowUpKind.None, "Task is still a draft");
        if (c.Status == WorkStatus.Deferred)
            return new(FollowUpAction.Skip, FollowUpKind.None, "Task is deferred");
        if (c.PausedUntil is { } until && until >= c.Today)
            return new(FollowUpAction.Skip, FollowUpKind.None,
                $"Follow-up paused until {until:d MMM}{(c.PauseReason is { } r ? $" (waiting for {Text.Humanize(r).ToLowerInvariant()})" : "")}");
        if (c.Status is WorkStatus.CompletedByAssignee or WorkStatus.CompletionAwaitingApproval)
            return new(FollowUpAction.Remind, FollowUpKind.ApprovalPending, "Completion submitted — confirmation is pending with the approver");

        var escalateToLeader = c.RemindersSinceResponse >= 2;

        if (c.Acknowledgement == AcknowledgementState.Pending)
            return new(FollowUpAction.Remind, FollowUpKind.AcknowledgementReminder,
                c.RemindersSinceResponse == 0 ? "Task not yet acknowledged" : $"Task still not acknowledged after {c.RemindersSinceResponse} reminder(s)",
                escalateToLeader, c.RemindersSinceResponse >= 1 ? "firm" : "neutral");

        // Recent meaningful communication suppresses the reminder.
        if (c.LastAssigneeActivityAt is { } last && c.Now - last < c.RecentUpdateWindow && (c.LastReminderAt is null || last >= c.LastReminderAt))
            return new(FollowUpAction.Skip, FollowUpKind.None,
                $"Update received {Text.Ago(c.Now - last)}{(c.LastActivityChannel is { } ch ? $" via {ch}" : "")} — no reminder needed");

        if (c.OpenCommitmentDate is { } committed && committed >= c.Today && !c.HasMissedCommitment)
            return new(FollowUpAction.Skip, FollowUpKind.None, $"Waiting on commitment to finish by {committed:ddd d MMM}");

        if (c.HasMissedCommitment)
            return new(FollowUpAction.Remind, FollowUpKind.CommitmentCheck, "A commitment date has passed without completion", true, "firm");

        if (c.PendingDependencies > 0 && !c.IsOverdue)
            return new(FollowUpAction.Remind, FollowUpKind.DependencyCheck, $"Waiting on {c.PendingDependencies} dependenc{(c.PendingDependencies == 1 ? "y" : "ies")} — checking whether it is still pending", escalateToLeader);

        if (c.IsBlocked || c.Status is WorkStatus.Blocked or WorkStatus.WaitingForInformation or WorkStatus.WaitingForExternalParty or WorkStatus.WaitingForApproval)
            return new(FollowUpAction.Remind, FollowUpKind.BlockerCheck,
                $"Task is {Text.Humanize(c.Status).ToLowerInvariant()}{(c.BlockedReason is { } b ? $" ({b})" : "")} — checking on the blocker", escalateToLeader);

        if (c.IsOverdue)
            return new(FollowUpAction.Remind, FollowUpKind.Overdue, $"Overdue by {Text.Days(c.DaysOverdue)}",
                escalateToLeader || c.DaysOverdue >= 1, c.DaysOverdue >= 5 || c.Criticality >= Criticality.Critical ? "urgent" : "firm");

        if (c.DaysUntilDue == 0)
            return new(FollowUpAction.Remind, FollowUpKind.DueToday, "Due today", escalateToLeader, "firm");
        if (c.DaysUntilDue is 1 or 2)
            return new(FollowUpAction.Remind, FollowUpKind.DueSoon, c.DaysUntilDue == 1 ? "Due tomorrow" : "Due in 2 days", escalateToLeader);

        return new(FollowUpAction.Remind, FollowUpKind.UpdateRequest,
            c.RemindersSinceResponse > 0 ? $"No update since the last {c.RemindersSinceResponse} reminder(s)" : "Scheduled progress check",
            escalateToLeader);
    }
}

/// <summary>Small text helpers shared by rules and notifications.</summary>
public static class Text
{
    public static string Humanize<T>(T value) where T : struct, Enum
    {
        var s = value.ToString();
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? s[i] : char.ToLowerInvariant(s[i]));
        }
        return sb.ToString();
    }

    public static string Days(int n) => n == 1 ? "1 day" : $"{n} days";

    public static string Ago(TimeSpan span) =>
        span.TotalMinutes < 1 ? "just now"
        : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
        : span.TotalHours < 24 ? $"{(int)span.TotalHours}h ago"
        : $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago";
}
