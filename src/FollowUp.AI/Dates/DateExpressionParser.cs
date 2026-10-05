using System.Globalization;
using System.Text.RegularExpressions;
using FollowUp.AI.Text;
using FollowUp.Domain.Rules;

namespace FollowUp.AI.Dates;

/// <summary>What kind of expression produced a <see cref="DateMatch"/>.</summary>
public enum DateExpressionKind
{
    /// <summary>today, tonight, tomorrow, day after tomorrow.</summary>
    Relative,
    /// <summary>(by / on / this / next) Monday … Sunday.</summary>
    Weekday,
    /// <summary>end of week / month / quarter / year, next week, this week.</summary>
    EndOfPeriod,
    /// <summary>in / within N days, weeks or months.</summary>
    Offset,
    /// <summary>"the 15th" — a day of the current or next month.</summary>
    DayOfMonth,
    /// <summary>15 Oct, Oct 15, 15/10, 15-10-2026, 2026-10-15.</summary>
    Explicit,
    /// <summary>A time of day without a date ("by noon", "5 pm", "EOD"): dated today, or tomorrow when already past.</summary>
    TimeOfDay,
}

/// <summary>A date (and optional time) recognised in text, with its character span and a 0–1 confidence.</summary>
/// <param name="Index">Start offset of the expression in the parsed text.</param>
/// <param name="Length">Length of the expression (the date and any attached time).</param>
/// <param name="Text">The matched text.</param>
/// <param name="Date">Resolved local date in the business calendar.</param>
/// <param name="Time">Local time when one was stated or implied (EOD = calendar work-day end).</param>
/// <param name="Confidence">How unambiguous the expression is.</param>
/// <param name="Kind">The family of expression.</param>
public sealed record DateMatch(int Index, int Length, string Text, DateOnly Date, TimeOnly? Time, double Confidence, DateExpressionKind Kind)
{
    /// <summary>Offset just past the expression.</summary>
    public int End => Index + Length;
}

/// <summary>
/// Deterministic parser for natural date / time expressions relative to a reference instant and a
/// <see cref="BusinessCalendar"/> (local time zone, weekend, work-day end). Day-first numeric dates (15/10) follow the
/// Indian / UK convention. Pure and thread-safe.
/// </summary>
/// <remarks>
/// Rules: "by Friday" / "on Friday" / "this Friday" / "Friday" = the coming Friday (today when today is Friday and the
/// work day has not ended); "next Friday" / "Friday next week" = Friday of next week; "next week" = next Monday;
/// "end of week" = last working day of the week; "end of month" = last calendar day; "in two weeks" = +14 days;
/// "the 15th" = the 15th of this month or next when already past; a day-month without a year that is already past
/// rolls to next year. Times: noon 12:00, morning 10:00, afternoon 15:00, evening 18:00, tonight 20:00, EOD / COB =
/// the calendar's work-day end.
/// </remarks>
public static partial class DateExpressionParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Months = "january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec";
    private const string Days = "monday|tuesday|wednesday|thursday|friday|saturday|sunday";

    /// <summary>Morning / afternoon / evening default times.</summary>
    public static readonly TimeOnly Morning = new(10, 0), Afternoon = new(15, 0), Evening = new(18, 0), Noon = new(12, 0), Tonight = new(20, 0);

    /// <summary>All date expressions in <paramref name="text"/>, in order of appearance, without overlaps.</summary>
    public static IReadOnlyList<DateMatch> Parse(string? text, DateTime referenceUtc, BusinessCalendar calendar)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var ctx = new Ctx(calendar, calendar.ToLocal(referenceUtc));
        var candidates = new List<Candidate>();
        Collect(text, ctx, candidates);

        // Greedy overlap resolution: longest first, then priority, then position.
        var accepted = new List<Candidate>();
        foreach (var c in candidates.OrderByDescending(c => c.Length).ThenByDescending(c => c.Priority).ThenBy(c => c.Index))
            if (!accepted.Any(a => c.Index < a.Index + a.Length && a.Index < c.Index + c.Length))
                accepted.Add(c);
        accepted.Sort((a, b) => a.Index.CompareTo(b.Index));

        return Merge(text, accepted, ctx);
    }

    /// <summary>The first date expression, or null.</summary>
    public static DateMatch? ParseFirst(string? text, DateTime referenceUtc, BusinessCalendar calendar) =>
        Parse(text, referenceUtc, calendar).FirstOrDefault();

    /// <summary>The coming occurrence of a weekday: today when today is that day and the work day has not ended.</summary>
    public static DateOnly Coming(DayOfWeek day, DateTime referenceUtc, BusinessCalendar calendar) =>
        new Ctx(calendar, calendar.ToLocal(referenceUtc)).Coming(day);

    /// <summary>The weekday of next week (weeks start on Monday).</summary>
    public static DateOnly NextWeeks(DayOfWeek day, DateTime referenceUtc, BusinessCalendar calendar) =>
        new Ctx(calendar, calendar.ToLocal(referenceUtc)).NextWeeks(day);

    /// <summary>Parses a weekday name or abbreviation ("Mon", "thurs", "Friday").</summary>
    public static DayOfWeek? ParseWeekday(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        var w = word.Trim().TrimEnd('.', 's', 'S').ToLowerInvariant();
        if (w.Length < 3) return null;
        return w[..3] switch
        {
            "mon" => DayOfWeek.Monday,
            "tue" => DayOfWeek.Tuesday,
            "wed" => DayOfWeek.Wednesday,
            "thu" => DayOfWeek.Thursday,
            "fri" => DayOfWeek.Friday,
            "sat" => DayOfWeek.Saturday,
            "sun" => DayOfWeek.Sunday,
            _ => null,
        };
    }

    /// <summary>Parses a month name or abbreviation into 1–12.</summary>
    public static int? ParseMonth(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        var w = word.Trim().TrimEnd('.').ToLowerInvariant();
        if (w.Length < 3) return null;
        return w[..3] switch
        {
            "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6,
            "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, "dec" => 12,
            _ => null,
        };
    }

    // ───────────────────────────── collection ─────────────────────────────

    private static void Collect(string text, Ctx ctx, List<Candidate> list)
    {
        foreach (Match m in RelativeRegex().Matches(text))
        {
            var w = TextTools.Squash(m.Groups["w"].Value).ToLowerInvariant();
            if (w.StartsWith("end of", StringComparison.Ordinal))
                list.Add(Date(m, ctx.Today, 0.95, DateExpressionKind.Relative, ctx.Cal.WorkdayEnd));
            else if (w.Contains("after", StringComparison.Ordinal))
                list.Add(Date(m, ctx.Today.AddDays(2), 0.95, DateExpressionKind.Relative));
            else if (w.StartsWith("ton", StringComparison.Ordinal))
                list.Add(Date(m, ctx.Today, 0.9, DateExpressionKind.Relative, Tonight));
            else if (w == "today")
                list.Add(Date(m, ctx.Today, 0.95, DateExpressionKind.Relative));
            else
                list.Add(Date(m, ctx.Today.AddDays(1), 0.95, DateExpressionKind.Relative));
        }

        foreach (Match m in WeekdayRegex().Matches(text))
        {
            if (ParseWeekday(m.Groups["wd"].Value) is not { } wd) continue;
            var mod = TextTools.Squash(m.Groups["mod"].Value).ToLowerInvariant();
            var weekRef = TextTools.Squash(m.Groups["nw"].Value + " " + m.Groups["nwp"].Value).ToLowerInvariant();
            var isNext = mod == "next" || weekRef.Contains("next", StringComparison.Ordinal);
            var date = isNext ? ctx.NextWeeks(wd) : ctx.Coming(wd);
            list.Add(Date(m, date, 0.9, DateExpressionKind.Weekday));
        }

        foreach (Match m in WeekdayAbbrevRegex().Matches(text))
        {
            if (ParseWeekday(m.Groups["wd"].Value) is not { } wd) continue;
            var isNext = m.Groups["mod"].Value.Equals("next", StringComparison.OrdinalIgnoreCase);
            list.Add(Date(m, isNext ? ctx.NextWeeks(wd) : ctx.Coming(wd), 0.8, DateExpressionKind.Weekday));
        }

        foreach (Match m in EndOfPeriodRegex().Matches(text))
        {
            var p = m.Groups["p"].Value.ToLowerInvariant();
            var next = m.Groups["next"].Success;
            DateOnly date;
            switch (p)
            {
                case "week" or "eow":
                    date = next ? ctx.EndOfWeek(ctx.Today.AddDays(7)) : ctx.EndOfThisOrNextWeek();
                    break;
                case "month" or "eom":
                {
                    var b = next ? ctx.Today.AddMonths(1) : ctx.Today;
                    date = new DateOnly(b.Year, b.Month, DateTime.DaysInMonth(b.Year, b.Month));
                    break;
                }
                case "quarter" or "eoq":
                {
                    var b = next ? ctx.Today.AddMonths(3) : ctx.Today;
                    var qEndMonth = ((b.Month - 1) / 3 + 1) * 3;
                    date = new DateOnly(b.Year, qEndMonth, DateTime.DaysInMonth(b.Year, qEndMonth));
                    break;
                }
                default:
                    date = new DateOnly(ctx.Today.Year + (next ? 1 : 0), 12, 31);
                    break;
            }
            list.Add(Date(m, date, 0.88, DateExpressionKind.EndOfPeriod));
        }

        foreach (Match m in WeekRefRegex().Matches(text))
        {
            var isNext = m.Groups["m"].Value.Equals("next", StringComparison.OrdinalIgnoreCase);
            list.Add(isNext
                ? Date(m, ctx.NextWeeks(DayOfWeek.Monday), 0.7, DateExpressionKind.EndOfPeriod)
                : Date(m, ctx.EndOfThisOrNextWeek(), 0.6, DateExpressionKind.EndOfPeriod));
        }

        foreach (Match m in OffsetRegex().Matches(text))
        {
            if (m.Groups["fortnight"].Success)
            {
                list.Add(Date(m, ctx.Today.AddDays(14), 0.85, DateExpressionKind.Offset));
                continue;
            }
            var nText = m.Groups["n"].Value;
            if (TextTools.ParseNumber(nText) is not { } n || n <= 0 || n > 366) continue;
            var vague = nText.Contains("few", StringComparison.OrdinalIgnoreCase);
            var unit = m.Groups["u"].Value.ToLowerInvariant();
            var business = m.Groups["biz"].Success;
            var date = unit.StartsWith("week", StringComparison.Ordinal) ? ctx.Today.AddDays(7 * n)
                : unit.StartsWith("month", StringComparison.Ordinal) ? ctx.Today.AddMonths(n)
                : business ? ctx.Cal.AddWorkingDays(ctx.Today, n) : ctx.Today.AddDays(n);
            list.Add(Date(m, date, vague ? 0.6 : 0.85, DateExpressionKind.Offset));
        }

        foreach (Match m in DayMonthRegex().Matches(text))
            AddExplicit(m, ctx, list, m.Groups["d"].Value, ParseMonth(m.Groups["mon"].Value), m.Groups["y"].Value, 0.92);

        foreach (Match m in MonthDayRegex().Matches(text))
            AddExplicit(m, ctx, list, m.Groups["d"].Value, ParseMonth(m.Groups["mon"].Value), m.Groups["y"].Value, 0.92);

        foreach (Match m in NumericDateRegex().Matches(text))
        {
            if (m.Value == "24/7") continue; // "support 24/7" is not 24 July
            var hasYear = m.Groups["y"].Success || m.Groups["y2"].Success;
            var month = int.Parse(m.Groups["m"].Success ? m.Groups["m"].Value : m.Groups["m2"].Value, CultureInfo.InvariantCulture);
            AddExplicit(m, ctx, list, m.Groups["d"].Value, month, m.Groups["y"].Success ? m.Groups["y"].Value : m.Groups["y2"].Value, hasYear ? 0.9 : 0.72);
        }

        foreach (Match m in IsoDateRegex().Matches(text))
            AddExplicit(m, ctx, list, m.Groups["d"].Value, int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture), m.Groups["y"].Value, 0.95);

        foreach (Match m in OrdinalDayRegex().Matches(text))
        {
            var day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            if (day is < 1 or > 31) continue;
            var t = ctx.Today;
            var date = new DateOnly(t.Year, t.Month, Math.Min(day, DateTime.DaysInMonth(t.Year, t.Month)));
            if (date < t)
            {
                var n = t.AddMonths(1);
                date = new DateOnly(n.Year, n.Month, Math.Min(day, DateTime.DaysInMonth(n.Year, n.Month)));
            }
            list.Add(Date(m, date, 0.75, DateExpressionKind.DayOfMonth));
        }

        // ── times of day ──
        foreach (Match m in Clock12Regex().Matches(text))
        {
            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            var min = m.Groups["min"].Success ? int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture) : 0;
            var pm = m.Groups["ap"].Value.StartsWith('p') || m.Groups["ap"].Value.StartsWith('P');
            if (h == 12) h = 0;
            if (pm) h += 12;
            list.Add(Time(m, new TimeOnly(h, min), standalone: true));
        }

        foreach (Match m in Clock24Regex().Matches(text))
        {
            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture);
            list.Add(Time(m, new TimeOnly(h, min), standalone: true));
        }

        foreach (Match m in NoonRegex().Matches(text))
        {
            var midnight = m.Value.Contains("midnight", StringComparison.OrdinalIgnoreCase);
            list.Add(Time(m, midnight ? new TimeOnly(23, 59) : Noon, standalone: true));
        }

        foreach (Match m in EodRegex().Matches(text))
            list.Add(Time(m, ctx.Cal.WorkdayEnd, standalone: true));

        foreach (Match m in PeriodRegex().Matches(text))
        {
            var p = m.Groups["p"].Value.ToLowerInvariant();
            var time = p switch { "morning" => Morning, "afternoon" => Afternoon, _ => Evening };
            list.Add(Time(m, time, standalone: m.Groups["pre"].Success, period: true));
        }
    }

    private static void AddExplicit(Match m, Ctx ctx, List<Candidate> list, string dayText, int? month, string yearText, double confidence)
    {
        if (month is not { } mo || !int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out var day)) return;
        int year;
        var explicitYear = !string.IsNullOrEmpty(yearText);
        if (explicitYear)
        {
            var y = yearText.TrimStart('\'');
            year = int.Parse(y, CultureInfo.InvariantCulture);
            if (y.Length == 2) year += 2000;
            if (year is < 2000 or > 2100) return;
        }
        else year = ctx.Today.Year;
        if (day < 1 || day > DateTime.DaysInMonth(year, mo)) return;
        var date = new DateOnly(year, mo, day);
        if (!explicitYear && date < ctx.Today)
        {
            // A day-month that has already passed this year means next year ("15 Oct" said in December).
            if (day > DateTime.DaysInMonth(year + 1, mo)) return;
            date = new DateOnly(year + 1, mo, day);
        }
        list.Add(new Candidate(m.Index, m.Length, date, null, explicitYear ? Math.Min(0.97, confidence + 0.03) : confidence,
            DateExpressionKind.Explicit, IsTime: false, Standalone: false, Period: false, Priority: 3));
    }

    private static Candidate Date(Match m, DateOnly d, double conf, DateExpressionKind kind, TimeOnly? time = null) =>
        new(m.Index, m.Length, d, time, conf, kind, IsTime: false, Standalone: false, Period: false, Priority: 2);

    private static Candidate Time(Match m, TimeOnly t, bool standalone, bool period = false) =>
        new(m.Index, m.Length, null, t, 0.6, DateExpressionKind.TimeOfDay, IsTime: true, Standalone: standalone, Period: period, Priority: 1);

    // ───────────────────────────── merging dates and times ─────────────────────────────

    private static List<DateMatch> Merge(string text, List<Candidate> accepted, Ctx ctx)
    {
        var dates = accepted.Where(c => !c.IsTime).ToList();
        var times = accepted.Where(c => c.IsTime).ToList();
        var used = new HashSet<int>();
        var result = new List<DateMatch>();

        foreach (var d in dates)
        {
            var start = d.Index;
            var end = d.Index + d.Length;
            var time = d.Time;
            for (var i = 0; i < times.Count; i++)
            {
                if (used.Contains(i)) continue;
                var t = times[i];
                if (t.Index >= end && Joinable(text[end..t.Index]))
                {
                    end = t.Index + t.Length;
                    time = t.Time;
                    used.Add(i);
                    break;
                }
                if (!t.Period && t.Index + t.Length <= start && Joinable(text[(t.Index + t.Length)..start]))
                {
                    start = t.Index;
                    time = t.Time;
                    used.Add(i);
                    break;
                }
            }
            result.Add(new DateMatch(start, end - start, text[start..end], d.Date!.Value, time, d.Confidence, d.Kind));
        }

        for (var i = 0; i < times.Count; i++)
        {
            if (used.Contains(i) || !times[i].Standalone) continue;
            var t = times[i];
            var date = TimeOnly.FromDateTime(ctx.Now) < t.Time!.Value ? ctx.Today : ctx.Today.AddDays(1);
            result.Add(new DateMatch(t.Index, t.Length, text.Substring(t.Index, t.Length), date, t.Time, t.Confidence, DateExpressionKind.TimeOfDay));
        }

        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    private static bool Joinable(string gap) => gap.Length <= 16 && JoinGapRegex().IsMatch(gap);

    // ───────────────────────────── calendar context ─────────────────────────────

    private sealed class Ctx(BusinessCalendar cal, DateTime nowLocal)
    {
        public BusinessCalendar Cal { get; } = cal;
        public DateTime Now { get; } = nowLocal;
        public DateOnly Today { get; } = DateOnly.FromDateTime(nowLocal);

        public DateOnly Coming(DayOfWeek day)
        {
            var diff = ((int)day - (int)Today.DayOfWeek + 7) % 7;
            if (diff == 0 && TimeOnly.FromDateTime(Now) >= Cal.WorkdayEnd) diff = 7;
            return Today.AddDays(diff);
        }

        public DateOnly NextWeeks(DayOfWeek day) =>
            BusinessCalendar.StartOfWeek(Today).AddDays(7 + ((int)day + 6) % 7);

        public DateOnly EndOfWeek(DateOnly anyDay)
        {
            var start = BusinessCalendar.StartOfWeek(anyDay);
            for (var i = 6; i >= 0; i--)
                if (Cal.IsWorkingDay(start.AddDays(i))) return start.AddDays(i);
            return start.AddDays(4);
        }

        public DateOnly EndOfThisOrNextWeek()
        {
            var eow = EndOfWeek(Today);
            return eow >= Today ? eow : EndOfWeek(Today.AddDays(7));
        }
    }

    private readonly record struct Candidate(int Index, int Length, DateOnly? Date, TimeOnly? Time, double Confidence,
        DateExpressionKind Kind, bool IsTime, bool Standalone, bool Period, int Priority);

    // ───────────────────────────── patterns ─────────────────────────────

    [GeneratedRegex(@"\b(?<w>(?:the\s+)?day\s+after\s+tomorrow|end\s+of\s+(?:the\s+)?today|tomorrow|tomorow|tmrw|tmr|today|tonight|tonite)\b", Opts)]
    private static partial Regex RelativeRegex();

    [GeneratedRegex(@"\b(?:(?<mod>this\s+coming|next|this|coming)\s+|(?<nwp>next\s+week|this\s+week)\s+(?:on\s+)?)?(?<wd>" + Days + @")\b(?:\s+(?<nw>next\s+week|this\s+week))?", Opts)]
    private static partial Regex WeekdayRegex();

    [GeneratedRegex(@"(?:\b(?<mod>next|this|coming)\s+|(?<=\b(?:by|on|before|till|until|due|from|starting|eta)\s+))(?<wd>mon|tues|tue|wed|thurs|thur|thu|fri|sat|sun)\b\.?", Opts)]
    private static partial Regex WeekdayAbbrevRegex();

    [GeneratedRegex(@"\b(?:the\s+)?end\s+of\s+(?:the\s+|this\s+|(?<next>next)\s+)?(?<p>week|month|quarter|year)\b|\b(?<p>eow|eom|eoq|eoy)\b|\b(?:(?<next>next)\s+)?(?<p>week|month|quarter|year)[\s-]end\b", Opts)]
    private static partial Regex EndOfPeriodRegex();

    [GeneratedRegex(@"\b(?<m>next|this|the\s+coming|coming)\s+week\b", Opts)]
    private static partial Regex WeekRefRegex();

    [GeneratedRegex(@"\b(?:with)?in\s+(?:the\s+)?(?:next\s+)?(?<n>" + TextTools.NumberWordPattern + @")\s+(?<biz>working\s+|business\s+)?(?<u>days?|weeks?|months?)\b|\b(?<n>" + TextTools.NumberWordPattern + @")\s+(?<biz>working\s+|business\s+)?(?<u>days?|weeks?)\s+from\s+(?:now|today)\b|\bafter\s+(?<n>" + TextTools.NumberWordPattern + @")\s+(?<biz>working\s+|business\s+)?(?<u>days?|weeks?)\b|\b(?<fortnight>(?:with)?in\s+a\s+fortnight)\b", Opts)]
    private static partial Regex OffsetRegex();

    [GeneratedRegex(@"\b(?<d>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)?(?:\s+of)?[\s\-/.]*(?<mon>" + Months + @")\.?(?:[\s,\-/.]+(?<y>\d{4}|'\d{2}))?\b", Opts)]
    private static partial Regex DayMonthRegex();

    [GeneratedRegex(@"\b(?<mon>" + Months + @")\.?\s+(?<d>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)?\b(?![:%]|\.\d|\s*(?:am|pm|a\.m|p\.m)\b)(?:,?\s+(?<y>\d{4})\b)?", Opts)]
    private static partial Regex MonthDayRegex();

    [GeneratedRegex(@"(?<![\w/.\-:])(?<d>3[01]|[12]\d|0?[1-9])(?:/(?<m>1[0-2]|0?[1-9])(?:/(?<y>\d{4}|\d{2}))?|(?<sep>[.\-])(?<m2>1[0-2]|0?[1-9])\k<sep>(?<y2>\d{4}|\d{2}))(?![\w/:%]|[.\-]\d)", Opts)]
    private static partial Regex NumericDateRegex();

    [GeneratedRegex(@"\b(?<y>20\d{2})-(?<m>1[0-2]|0[1-9])-(?<d>3[01]|[12]\d|0[1-9])\b", Opts)]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\b(?:the\s+)?(?<d>3[01]|[12]\d|0?[1-9])(?:st|nd|rd|th)\b(?!\s*(?:of\s+)?(?:" + Months + @")\b)(?!\s+(?:of\s+)?(?:every|each|the\s+month|party|parties|floor|phase|time|round|level|stage|step|attempt|version|line|quarter|half|week|place|item|option|reminder|follow|call|meeting|batch|invoice|instal+ment|year|month|day))", Opts)]
    private static partial Regex OrdinalDayRegex();

    [GeneratedRegex(@"\b(?<h>1[0-2]|0?[1-9])(?:[:.](?<min>[0-5]\d))?\s*(?<ap>a\.?m\.?|p\.?m\.?)(?!\w)", Opts)]
    private static partial Regex Clock12Regex();

    [GeneratedRegex(@"(?<![\d:.])\b(?<h>[01]?\d|2[0-3]):(?<min>[0-5]\d)\b(?:\s*(?:hrs|hours|h)\b)?(?!\s*(?:[ap]\.?m\b|:))", Opts)]
    private static partial Regex Clock24Regex();

    [GeneratedRegex(@"\b(?:12\s*)?(?:noon|midday|mid-day|midnight)\b", Opts)]
    private static partial Regex NoonRegex();

    [GeneratedRegex(@"\b(?:eod|eob|cob|e\.o\.d|end\s+of\s+(?:the\s+)?(?:business\s+|working\s+|work\s+)?day|close\s+of\s+(?:the\s+)?(?:business|day|play))\b\.?", Opts)]
    private static partial Regex EodRegex();

    [GeneratedRegex(@"\b(?<pre>(?:by|before|till|until|this|in\s+the)\s+)?(?<p>morning|afternoon|evening)\b", Opts)]
    private static partial Regex PeriodRegex();

    [GeneratedRegex(@"^[\s,]*(?:(?:by|at|before|around|on|till|until|latest\s+by|-)[\s,]*)?$", Opts)]
    private static partial Regex JoinGapRegex();
}
