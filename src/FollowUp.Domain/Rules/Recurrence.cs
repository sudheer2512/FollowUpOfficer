using System.Globalization;

namespace FollowUp.Domain.Rules;

/// <summary>
/// Recurrence rule (spec §11, §13). Stored as JSON on the definition. Examples:
/// <list type="bullet">
/// <item>"1st of every month, due by the 5th" → Monthly, DayOfMonth 1 + DueRule DayOfMonth 5.</item>
/// <item>"Every Monday morning, due Friday" → Weekly [Monday], CreateTime 08:00 + DueRule WeekdayOfSameWeek Friday.</item>
/// <item>"Last working day of every month" → Monthly, LastWorkingDay.</item>
/// <item>"Quarterly on 1 Jan, 1 Apr, 1 Jul, 1 Oct" → Quarterly, Months [1,4,7,10], DayOfMonth 1.</item>
/// <item>"15 days before licence expiry" → AnchorDates [expiry], AnchorOffsetDays −15 + DueRule AnchorDate.</item>
/// </list>
/// </summary>
public sealed record RecurrenceRule
{
    public RecurrenceFrequency Frequency { get; init; } = RecurrenceFrequency.Monthly;
    /// <summary>Every N days / weeks / months / years. Fortnightly = Weekly with Interval 2.</summary>
    public int Interval { get; init; } = 1;
    public List<DayOfWeek> DaysOfWeek { get; init; } = [];
    public MonthlyPattern MonthlyPattern { get; init; } = MonthlyPattern.DayOfMonth;
    public int DayOfMonth { get; init; } = 1;
    /// <summary>For NthWeekday: 1..4, or −1 for the last such weekday.</summary>
    public int WeekOrdinal { get; init; } = 1;
    public DayOfWeek Weekday { get; init; } = DayOfWeek.Monday;
    /// <summary>Explicit months (1–12) for quarterly / half-yearly / yearly rules.</summary>
    public List<int> Months { get; init; } = [];
    public int FiscalYearStartMonth { get; init; } = 4;
    /// <summary>Quarterly / half-yearly periods start at the fiscal year start month instead of the start date's month.</summary>
    public bool AlignToFiscalYear { get; init; }
    public List<DateOnly> AnchorDates { get; init; } = [];
    public int AnchorOffsetDays { get; init; }
    public bool RepeatAnchorsYearly { get; init; }
    /// <summary>Local time at which each occurrence is created.</summary>
    public TimeOnly CreateTime { get; init; } = new(8, 0);

    public string Describe()
    {
        var every = Interval > 1 ? $"every {Interval} " : "every ";
        string Day() => MonthlyPattern switch
        {
            MonthlyPattern.LastDay => "the last day",
            MonthlyPattern.FirstWorkingDay => "the first working day",
            MonthlyPattern.LastWorkingDay => "the last working day",
            MonthlyPattern.NthWeekday => $"the {(WeekOrdinal == -1 ? "last" : Ordinal(WeekOrdinal))} {Weekday}",
            _ => $"day {DayOfMonth}",
        };
        string MonthList(IEnumerable<int> ms) => string.Join(", ", ms.Select(m => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(m)));
        var text = Frequency switch
        {
            RecurrenceFrequency.Daily => Interval > 1 ? $"Every {Interval} days" : "Daily",
            RecurrenceFrequency.WorkingDays => "Every working day",
            RecurrenceFrequency.Weekly => Interval == 2 && DaysOfWeek.Count <= 1
                ? $"Fortnightly on {Days()}"
                : $"{Cap(every)}week{(Interval > 1 ? "s" : "")} on {Days()}",
            RecurrenceFrequency.Monthly => $"{Cap(every)}month{(Interval > 1 ? "s" : "")} on {Day()}",
            RecurrenceFrequency.Quarterly => Months.Count > 0 ? $"Quarterly ({MonthList(Months)}) on {Day()}" : $"Quarterly on {Day()}",
            RecurrenceFrequency.HalfYearly => Months.Count > 0 ? $"Half-yearly ({MonthList(Months)}) on {Day()}" : $"Half-yearly on {Day()}",
            RecurrenceFrequency.Yearly => Months.Count > 0 ? $"Annually in {MonthList(Months)} on {Day()}" : $"Annually on {Day()}",
            RecurrenceFrequency.FinancialYear => $"Once per financial year (starting {CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(FiscalYearStartMonth)}) on {Day()}",
            RecurrenceFrequency.AnchorDates => AnchorOffsetDays < 0
                ? $"{-AnchorOffsetDays} days before {string.Join(", ", AnchorDates.Select(d => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)))}{(RepeatAnchorsYearly ? " (every year)" : "")}"
                : $"On {string.Join(", ", AnchorDates.Select(d => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)))}{(AnchorOffsetDays > 0 ? $" + {AnchorOffsetDays} days" : "")}{(RepeatAnchorsYearly ? " (every year)" : "")}",
            _ => Frequency.ToString(),
        };
        return $"{text}, created at {CreateTime:HH\\:mm}";

        string Days() => DaysOfWeek.Count == 0 ? "the start day" : string.Join(" and ", DaysOfWeek.Select(d => d.ToString()));
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Ordinal(int n) => n switch { 1 => "first", 2 => "second", 3 => "third", 4 => "fourth", _ => n + "th" };
}

/// <summary>How the due date of each generated occurrence is derived (spec §12 "Due-Date Rule").</summary>
public sealed record DueRule
{
    public DueRuleKind Kind { get; init; } = DueRuleKind.SameDay;
    /// <summary>Days for AfterDays / AfterWorkingDays; day number for DayOfMonth.</summary>
    public int Value { get; init; }
    public DayOfWeek Weekday { get; init; } = DayOfWeek.Friday;
    public TimeOnly? DueTime { get; init; }
    /// <summary>What to do when the computed due date is not a working day. Default: bring it forward.</summary>
    public HolidayHandling HolidayHandling { get; init; } = HolidayHandling.PreviousWorkingDay;

    public string Describe() => Kind switch
    {
        DueRuleKind.SameDay => "due the same day",
        DueRuleKind.AfterDays => $"due {Value} day(s) later",
        DueRuleKind.AfterWorkingDays => $"due {Value} working day(s) later",
        DueRuleKind.DayOfMonth => $"due by day {Value} of the month",
        DueRuleKind.WeekdayOfSameWeek => $"due {Weekday} of the same week",
        DueRuleKind.EndOfMonth => "due at month end",
        DueRuleKind.AnchorDate => "due on the anchor date",
        _ => Kind.ToString(),
    } + (DueTime is { } t ? $" by {t:HH\\:mm}" : "");

    public DateOnly Compute(DateOnly occurrence, DateOnly? anchor, BusinessCalendar cal)
    {
        var due = Kind switch
        {
            DueRuleKind.AfterDays => occurrence.AddDays(Value),
            DueRuleKind.AfterWorkingDays => cal.AddWorkingDays(occurrence, Value),
            DueRuleKind.DayOfMonth => DayOfMonthOnOrAfter(occurrence, Value),
            DueRuleKind.WeekdayOfSameWeek => WeekdayOnOrAfter(occurrence, Weekday),
            DueRuleKind.EndOfMonth => new DateOnly(occurrence.Year, occurrence.Month, DateTime.DaysInMonth(occurrence.Year, occurrence.Month)),
            DueRuleKind.AnchorDate => anchor ?? occurrence,
            _ => occurrence,
        };
        if (!cal.IsWorkingDay(due) && HolidayHandling != HolidayHandling.Keep)
            due = HolidayHandling == HolidayHandling.NextWorkingDay ? cal.NextWorkingDay(due) : cal.PreviousWorkingDay(due);
        return due < occurrence ? occurrence : due;
    }

    private static DateOnly DayOfMonthOnOrAfter(DateOnly from, int day)
    {
        var d = new DateOnly(from.Year, from.Month, Math.Min(Math.Max(day, 1), DateTime.DaysInMonth(from.Year, from.Month)));
        if (d >= from) return d;
        var next = from.AddMonths(1);
        return new DateOnly(next.Year, next.Month, Math.Min(Math.Max(day, 1), DateTime.DaysInMonth(next.Year, next.Month)));
    }

    private static DateOnly WeekdayOnOrAfter(DateOnly from, DayOfWeek weekday)
    {
        var weekStart = BusinessCalendar.StartOfWeek(from);
        var offset = ((int)weekday - (int)DayOfWeek.Monday + 7) % 7;
        var d = weekStart.AddDays(offset);
        return d >= from ? d : d.AddDays(7);
    }
}

/// <summary>A resolved occurrence: the nominal rule date, the holiday-adjusted date, creation instant and due date.</summary>
public sealed record Occurrence(
    DateOnly NominalDate,
    DateOnly? Date,
    DateTime CreateAtUtc,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    string Key,
    DateOnly? AnchorDate,
    string? SkipReason)
{
    public bool IsSkipped => Date is null;
}

/// <summary>Pure recurrence calculator — the recurring-task generator, previews and tests all use it.</summary>
public static class RecurrenceEngine
{
    private const int MaxSearchDays = 800;

    /// <summary>Nominal rule dates within [from, to] (inclusive), honouring the series start / end.</summary>
    public static IEnumerable<(DateOnly Nominal, DateOnly? Anchor)> NominalDates(
        RecurrenceRule rule, DateOnly start, DateOnly? end, DateOnly from, DateOnly to, BusinessCalendar cal)
    {
        if (from < start) from = start;
        if (end is { } e && to > e) to = e;
        if (to < from) yield break;

        switch (rule.Frequency)
        {
            case RecurrenceFrequency.AnchorDates:
                foreach (var x in AnchorOccurrences(rule, from, to)) yield return x;
                yield break;

            case RecurrenceFrequency.Daily:
            {
                var interval = Math.Max(1, rule.Interval);
                var offset = (from.DayNumber - start.DayNumber) % interval;
                var d = offset == 0 ? from : from.AddDays(interval - offset);
                for (; d <= to; d = d.AddDays(interval)) yield return (d, null);
                yield break;
            }

            case RecurrenceFrequency.WorkingDays:
                for (var d = from; d <= to; d = d.AddDays(1))
                    if (cal.IsWorkingDay(d)) yield return (d, null);
                yield break;

            case RecurrenceFrequency.Weekly:
            {
                var interval = Math.Max(1, rule.Interval);
                var days = rule.DaysOfWeek.Count > 0 ? rule.DaysOfWeek.ToHashSet() : [start.DayOfWeek];
                var startWeek = BusinessCalendar.StartOfWeek(start);
                for (var d = from; d <= to; d = d.AddDays(1))
                {
                    if (!days.Contains(d.DayOfWeek)) continue;
                    var weekIndex = (BusinessCalendar.StartOfWeek(d).DayNumber - startWeek.DayNumber) / 7;
                    if (weekIndex % interval == 0) yield return (d, null);
                }
                yield break;
            }

            default:
            {
                // Month-based rules: enumerate candidate months, then the pattern day within each month.
                var months = MonthSet(rule, start);
                var monthInterval = rule.Frequency == RecurrenceFrequency.Monthly ? Math.Max(1, rule.Interval) : 1;
                var yearInterval = rule.Frequency == RecurrenceFrequency.Yearly ? Math.Max(1, rule.Interval) : 1;
                var cursor = new DateOnly(from.Year, from.Month, 1).AddMonths(-1);
                var last = new DateOnly(to.Year, to.Month, 1);
                for (; cursor <= last; cursor = cursor.AddMonths(1))
                {
                    var monthIndex = (cursor.Year - start.Year) * 12 + cursor.Month - start.Month;
                    if (monthIndex < 0) continue;
                    if (rule.Frequency == RecurrenceFrequency.Monthly)
                    {
                        if (monthIndex % monthInterval != 0) continue;
                    }
                    else
                    {
                        if (!months.Contains(cursor.Month)) continue;
                        if (rule.Frequency == RecurrenceFrequency.Yearly && (cursor.Year - start.Year) % yearInterval != 0) continue;
                    }
                    var d = PatternDate(rule, cursor.Year, cursor.Month, cal);
                    if (d >= from && d <= to) yield return (d, null);
                }
                yield break;
            }
        }
    }

    private static IEnumerable<(DateOnly, DateOnly?)> AnchorOccurrences(RecurrenceRule rule, DateOnly from, DateOnly to)
    {
        var list = new List<(DateOnly, DateOnly?)>();
        foreach (var anchor in rule.AnchorDates)
        {
            var years = rule.RepeatAnchorsYearly ? Enumerable.Range(0, Math.Max(1, to.Year - anchor.Year + 2)) : [0];
            foreach (var y in years)
            {
                var a = anchor.AddYears(y);
                var nominal = a.AddDays(rule.AnchorOffsetDays);
                if (nominal >= from && nominal <= to) list.Add((nominal, a));
            }
        }
        return list.OrderBy(x => x.Item1).DistinctBy(x => x.Item1);
    }

    private static HashSet<int> MonthSet(RecurrenceRule rule, DateOnly start)
    {
        if (rule.Months.Count > 0) return [.. rule.Months.Where(m => m is >= 1 and <= 12)];
        var baseMonth = rule.AlignToFiscalYear || rule.Frequency == RecurrenceFrequency.FinancialYear ? rule.FiscalYearStartMonth : start.Month;
        int[] offsets = rule.Frequency switch
        {
            RecurrenceFrequency.Quarterly => [0, 3, 6, 9],
            RecurrenceFrequency.HalfYearly => [0, 6],
            _ => [0],
        };
        return [.. offsets.Select(o => (baseMonth - 1 + o) % 12 + 1)];
    }

    public static DateOnly PatternDate(RecurrenceRule rule, int year, int month, BusinessCalendar cal)
    {
        var dim = DateTime.DaysInMonth(year, month);
        switch (rule.MonthlyPattern)
        {
            case MonthlyPattern.LastDay: return new DateOnly(year, month, dim);
            case MonthlyPattern.FirstWorkingDay: return cal.FirstWorkingDayOfMonth(year, month);
            case MonthlyPattern.LastWorkingDay: return cal.LastWorkingDayOfMonth(year, month);
            case MonthlyPattern.NthWeekday:
            {
                if (rule.WeekOrdinal == -1)
                {
                    var d = new DateOnly(year, month, dim);
                    while (d.DayOfWeek != rule.Weekday) d = d.AddDays(-1);
                    return d;
                }
                var first = new DateOnly(year, month, 1);
                var shift = ((int)rule.Weekday - (int)first.DayOfWeek + 7) % 7;
                var result = first.AddDays(shift + 7 * (Math.Clamp(rule.WeekOrdinal, 1, 5) - 1));
                return result.Month == month ? result : result.AddDays(-7);
            }
            default: return new DateOnly(year, month, Math.Min(Math.Max(rule.DayOfMonth, 1), dim));
        }
    }

    /// <summary>Resolves a nominal date into an occurrence: holiday handling, creation instant, due date and display key.</summary>
    public static Occurrence Resolve(RecurrenceRule rule, DueRule due, HolidayHandling handling, DateOnly nominal, DateOnly? anchor, BusinessCalendar cal)
    {
        var alreadyWorking = rule.Frequency == RecurrenceFrequency.WorkingDays
            || rule.MonthlyPattern is MonthlyPattern.FirstWorkingDay or MonthlyPattern.LastWorkingDay && rule.Frequency is not (RecurrenceFrequency.Daily or RecurrenceFrequency.Weekly or RecurrenceFrequency.AnchorDates);
        string? reason = null;
        var date = alreadyWorking ? nominal : cal.Adjust(nominal, handling, out reason);
        var key = OccurrenceKey(rule, nominal, anchor);
        if (date is null)
            return new Occurrence(nominal, null, cal.ToUtc(nominal, rule.CreateTime), null, null, key, anchor,
                $"Falls on a {reason}; holiday handling is Skip");
        var dueDate = due.Compute(date.Value, anchor, cal);
        return new Occurrence(nominal, date, cal.ToUtc(date.Value, rule.CreateTime), dueDate, due.DueTime, key, anchor, null);
    }

    public static IEnumerable<Occurrence> Between(RecurrenceRule rule, DueRule due, HolidayHandling handling,
        DateOnly start, DateOnly? end, DateOnly from, DateOnly to, BusinessCalendar cal) =>
        NominalDates(rule, start, end, from, to, cal).Select(n => Resolve(rule, due, handling, n.Nominal, n.Anchor, cal));

    /// <summary>The first occurrence whose nominal date is strictly after <paramref name="afterNominal"/> (or on/after start when null).</summary>
    public static Occurrence? Next(RecurrenceRule rule, DueRule due, HolidayHandling handling, DateOnly start, DateOnly? end,
        DateOnly? afterNominal, BusinessCalendar cal)
    {
        var from = afterNominal is { } a ? a.AddDays(1) : start;
        var to = from.AddDays(MaxSearchDays);
        if (rule.Frequency == RecurrenceFrequency.AnchorDates && rule.AnchorDates.Count > 0 && !rule.RepeatAnchorsYearly)
            to = rule.AnchorDates.Max().AddDays(Math.Max(0, rule.AnchorOffsetDays) + 1);
        foreach (var n in NominalDates(rule, start, end, from, to, cal))
            return Resolve(rule, due, handling, n.Nominal, n.Anchor, cal);
        return null;
    }

    /// <summary>Upcoming occurrences for previews in the UI.</summary>
    public static IReadOnlyList<Occurrence> Preview(RecurrenceRule rule, DueRule due, HolidayHandling handling,
        DateOnly start, DateOnly? end, DateOnly from, int count, BusinessCalendar cal)
    {
        var list = new List<Occurrence>();
        DateOnly? after = from.AddDays(-1);
        if (after < start) after = null;
        for (var i = 0; i < count; i++)
        {
            var next = Next(rule, due, handling, start, end, after, cal);
            if (next is null) break;
            list.Add(next);
            after = next.NominalDate;
        }
        return list;
    }

    public static string OccurrenceKey(RecurrenceRule rule, DateOnly nominal, DateOnly? anchor) => rule.Frequency switch
    {
        RecurrenceFrequency.Monthly => nominal.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        RecurrenceFrequency.Quarterly => $"{nominal.Year}-Q{(nominal.Month - 1) / 3 + 1}",
        RecurrenceFrequency.HalfYearly => $"{nominal.Year}-H{(nominal.Month - 1) / 6 + 1}",
        RecurrenceFrequency.Yearly => nominal.Year.ToString(CultureInfo.InvariantCulture),
        RecurrenceFrequency.FinancialYear => FiscalYearLabel(nominal, rule.FiscalYearStartMonth),
        RecurrenceFrequency.Weekly => $"{ISOWeek.GetYear(nominal.ToDateTime(TimeOnly.MinValue))}-W{ISOWeek.GetWeekOfYear(nominal.ToDateTime(TimeOnly.MinValue)):00}",
        RecurrenceFrequency.AnchorDates => (anchor ?? nominal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => nominal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    public static string FiscalYearLabel(DateOnly d, int fyStartMonth)
    {
        if (fyStartMonth <= 1) return $"FY{d.Year}";
        var startYear = d.Month >= fyStartMonth ? d.Year : d.Year - 1;
        return $"FY{startYear}-{(startYear + 1) % 100:00}";
    }

    /// <summary>Renders a title template for an occurrence: "GST Review — {PrevMonth} {PrevMonthYear}" etc.</summary>
    public static string RenderTitle(string template, Occurrence occ, RecurrenceRule rule)
    {
        var d = occ.Date ?? occ.NominalDate;
        var inv = CultureInfo.InvariantCulture;
        var prev = d.AddMonths(-1);
        var fyQuarter = ((d.Month - rule.FiscalYearStartMonth + 12) % 12) / 3 + 1;
        return template
            .Replace("{Date}", d.ToString("d MMM yyyy", inv))
            .Replace("{Month}", d.ToString("MMMM", inv))
            .Replace("{MonthShort}", d.ToString("MMM", inv))
            .Replace("{Year}", d.Year.ToString(inv))
            .Replace("{PrevMonthYear}", prev.ToString("MMMM yyyy", inv))
            .Replace("{PrevMonth}", prev.ToString("MMMM", inv))
            .Replace("{Quarter}", $"Q{(d.Month - 1) / 3 + 1}")
            .Replace("{FYQuarter}", $"Q{fyQuarter} {FiscalYearLabel(d, rule.FiscalYearStartMonth)}")
            .Replace("{FY}", FiscalYearLabel(d, rule.FiscalYearStartMonth))
            .Replace("{Week}", $"W{ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue)):00}")
            .Replace("{Weekday}", d.DayOfWeek.ToString())
            .Replace("{Anchor}", (occ.AnchorDate ?? d).ToString("d MMM yyyy", inv));
    }
}
