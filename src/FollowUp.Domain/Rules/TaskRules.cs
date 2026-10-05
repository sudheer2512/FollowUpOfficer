using System.Globalization;
using System.Text.RegularExpressions;

namespace FollowUp.Domain.Rules;

/// <summary>Status classification and allowed transitions (spec §7).</summary>
public static class TaskStatusRules
{
    public static bool IsClosed(WorkStatus s) => s is WorkStatus.Completed or WorkStatus.Cancelled;

    public static bool IsOpen(WorkStatus s) => !IsClosed(s);

    /// <summary>Statuses counted as "open work" on dashboards (excludes drafts and deferred work).</summary>
    public static bool IsActive(WorkStatus s) => !IsClosed(s) && s is not (WorkStatus.Draft or WorkStatus.Deferred);

    public static bool IsWaiting(WorkStatus s) =>
        s is WorkStatus.WaitingForInformation or WorkStatus.WaitingForExternalParty or WorkStatus.WaitingForApproval or WorkStatus.Blocked;

    public static bool IsAwaitingConfirmation(WorkStatus s) => s is WorkStatus.CompletedByAssignee or WorkStatus.CompletionAwaitingApproval;

    public static readonly WorkStatus[] WorkingStatuses =
    [
        WorkStatus.Assigned, WorkStatus.AcknowledgementPending, WorkStatus.Accepted, WorkStatus.InProgress,
        WorkStatus.WaitingForInformation, WorkStatus.WaitingForExternalParty, WorkStatus.WaitingForApproval,
        WorkStatus.Blocked, WorkStatus.PartiallyCompleted,
    ];

    public static readonly WorkStatus[] ClosedStatuses = [WorkStatus.Completed, WorkStatus.Cancelled];

    public static bool CanTransition(WorkStatus from, WorkStatus to)
    {
        if (from == to) return false;
        return from switch
        {
            WorkStatus.Draft => to is WorkStatus.Assigned or WorkStatus.AcknowledgementPending or WorkStatus.Cancelled,
            WorkStatus.CompletedByAssignee => to is WorkStatus.CompletionAwaitingApproval or WorkStatus.Completed or WorkStatus.InProgress or WorkStatus.Cancelled,
            WorkStatus.CompletionAwaitingApproval => to is WorkStatus.Completed or WorkStatus.InProgress or WorkStatus.Cancelled,
            WorkStatus.Completed => to is WorkStatus.InProgress,
            WorkStatus.Cancelled => to is WorkStatus.Assigned or WorkStatus.InProgress,
            WorkStatus.Deferred => Array.IndexOf(WorkingStatuses, to) >= 0 || to == WorkStatus.Cancelled,
            _ => to != WorkStatus.Draft && to != WorkStatus.CompletionAwaitingApproval && to != WorkStatus.Completed,
        };
    }
}

/// <summary>Deadline classification and wording (spec §17): due today / tomorrow / this week, overdue, severely overdue.</summary>
public static class DeadlineRules
{
    public static bool IsOverdue(DateOnly? due, TimeOnly? dueTime, WorkStatus status, DateTime nowUtc, BusinessCalendar cal) =>
        due is { } d && !TaskStatusRules.IsClosed(status) && !TaskStatusRules.IsAwaitingConfirmation(status)
        && status != WorkStatus.Deferred && nowUtc > cal.DeadlineUtc(d, dueTime);

    /// <summary>Calendar days overdue (at least 1 once past the deadline).</summary>
    public static int DaysOverdue(DateOnly due, DateOnly today) => Math.Max(today.DayNumber - due.DayNumber, 0);

    public static DeadlineBucket Bucket(DateOnly? due, TimeOnly? dueTime, WorkStatus status, DateTime nowUtc, BusinessCalendar cal, int severeDays)
    {
        if (due is not { } d || TaskStatusRules.IsClosed(status)) return DeadlineBucket.None;
        var today = cal.Today(nowUtc);
        if (IsOverdue(due, dueTime, status, nowUtc, cal))
            return DaysOverdue(d, today) >= severeDays ? DeadlineBucket.SeverelyOverdue : DeadlineBucket.Overdue;
        if (d == today) return DeadlineBucket.DueToday;
        if (d == today.AddDays(1)) return DeadlineBucket.DueTomorrow;
        var weekEnd = BusinessCalendar.StartOfWeek(today).AddDays(6);
        return d <= weekEnd ? DeadlineBucket.DueThisWeek : DeadlineBucket.Later;
    }

    public static string Describe(DateOnly? due, TimeOnly? dueTime, WorkStatus status, DateTime nowUtc, BusinessCalendar cal)
    {
        if (due is not { } d) return "No due date";
        if (TaskStatusRules.IsClosed(status)) return $"Due {d.ToString("d MMM", CultureInfo.InvariantCulture)}";
        var today = cal.Today(nowUtc);
        if (IsOverdue(due, dueTime, status, nowUtc, cal))
        {
            var days = DaysOverdue(d, today);
            return days == 0 ? "Overdue today" : $"Overdue by {Text.Days(days)}";
        }
        var left = d.DayNumber - today.DayNumber;
        return left switch
        {
            0 => "Due today",
            1 => "Due tomorrow",
            _ => $"Due in {left} days",
        };
    }
}

/// <summary>Human-readable task numbers: TASK-2026-00142 (spec §1).</summary>
public static partial class TaskNumbers
{
    public const string Prefix = "TASK";

    public static string Format(int year, int sequence) => $"{Prefix}-{year}-{sequence:00000}";

    [GeneratedRegex(@"\bTASK-(\d{4})-(\d{3,6})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    /// <summary>All task numbers mentioned in a text, normalised to upper case, in order of appearance.</summary>
    public static IReadOnlyList<string> FindAll(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return NumberPattern().Matches(text)
            .Select(m => Format(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .Distinct().ToList();
    }

    public static bool TryParse(string? text, out int year, out int sequence)
    {
        year = sequence = 0;
        var m = NumberPattern().Match(text ?? "");
        if (!m.Success) return false;
        year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        sequence = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }
}

/// <summary>Who must confirm completion for a task (spec §31).</summary>
public static class ApprovalRules
{
    /// <summary>Approval is required for important work or when explicitly requested.</summary>
    public static bool IsRequired(bool explicitFlag, Criticality criticality) => explicitFlag || criticality >= Criticality.Critical;

    /// <summary>Default approval flow: team leader for important work, plus the delegator; the CEO for business-critical work.</summary>
    public static IReadOnlyList<ApproverRole> DefaultFlow(Criticality criticality) => criticality switch
    {
        Criticality.BusinessCritical => [ApproverRole.Delegator, ApproverRole.Ceo],
        Criticality.Critical => [ApproverRole.Delegator],
        Criticality.Important => [ApproverRole.TeamLeader, ApproverRole.Delegator],
        _ => [ApproverRole.Delegator],
    };
}
