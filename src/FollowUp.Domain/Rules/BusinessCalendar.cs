namespace FollowUp.Domain.Rules;

/// <summary>
/// Working-day calendar arithmetic (spec §73): weekends, public / company / regional holidays and the
/// business time zone. Built from a <see cref="WorkCalendar"/> row; immutable and thread-safe.
/// </summary>
public sealed class BusinessCalendar
{
    private readonly HashSet<DayOfWeek> _weekend;
    private readonly Dictionary<DateOnly, string> _holidays;

    public BusinessCalendar(TimeZoneInfo timeZone, IEnumerable<DayOfWeek> weekend, IEnumerable<(DateOnly Date, string Name)>? holidays = null,
        TimeOnly? workdayStart = null, TimeOnly? workdayEnd = null)
    {
        TimeZone = timeZone;
        _weekend = [.. weekend];
        _holidays = (holidays ?? []).GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.First().Name);
        WorkdayStart = workdayStart ?? new TimeOnly(9, 30);
        WorkdayEnd = workdayEnd ?? new TimeOnly(18, 30);
    }

    public TimeZoneInfo TimeZone { get; }
    public TimeOnly WorkdayStart { get; }
    public TimeOnly WorkdayEnd { get; }
    public IReadOnlySet<DayOfWeek> Weekend => _weekend;

    public static BusinessCalendar Default { get; } = new(ResolveTimeZone("Asia/Kolkata"), [DayOfWeek.Saturday, DayOfWeek.Sunday]);

    public static BusinessCalendar From(WorkCalendar c) => new(
        ResolveTimeZone(c.TimeZoneId),
        ParseWeekend(c.WeekendDays),
        c.Holidays.Select(h => (h.Date, h.Name)),
        c.WorkdayStart,
        c.WorkdayEnd);

    public static IEnumerable<DayOfWeek> ParseWeekend(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<DayOfWeek>(s, true, out var d) ? (DayOfWeek?)d : null)
            .Where(d => d is not null).Select(d => d!.Value);

    /// <summary>Accepts IANA ("Asia/Kolkata") or Windows ("India Standard Time") ids; falls back to UTC.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception)
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win))
                try { return TimeZoneInfo.FindSystemTimeZoneById(win); } catch (Exception) { }
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana))
                try { return TimeZoneInfo.FindSystemTimeZoneById(iana); } catch (Exception) { }
            return TimeZoneInfo.Utc;
        }
    }

    public bool IsWeekend(DateOnly d) => _weekend.Contains(d.DayOfWeek);
    public bool IsHoliday(DateOnly d) => _holidays.ContainsKey(d);
    public string? HolidayName(DateOnly d) => _holidays.GetValueOrDefault(d);
    public bool IsWorkingDay(DateOnly d) => !IsWeekend(d) && !IsHoliday(d);

    public string? NonWorkingReason(DateOnly d) =>
        IsHoliday(d) ? $"holiday ({HolidayName(d)})" : IsWeekend(d) ? $"weekend ({d.DayOfWeek})" : null;

    public DateOnly NextWorkingDay(DateOnly d, bool inclusive = true)
    {
        var x = inclusive ? d : d.AddDays(1);
        for (var i = 0; i < 400 && !IsWorkingDay(x); i++) x = x.AddDays(1);
        return x;
    }

    public DateOnly PreviousWorkingDay(DateOnly d, bool inclusive = true)
    {
        var x = inclusive ? d : d.AddDays(-1);
        for (var i = 0; i < 400 && !IsWorkingDay(x); i++) x = x.AddDays(-1);
        return x;
    }

    /// <summary>Adds working days (negative to go back). Adding 0 returns the date unchanged.</summary>
    public DateOnly AddWorkingDays(DateOnly d, int days)
    {
        var step = Math.Sign(days);
        var x = d;
        for (var remaining = Math.Abs(days); remaining > 0;)
        {
            x = x.AddDays(step);
            if (IsWorkingDay(x)) remaining--;
        }
        return x;
    }

    /// <summary>Working days strictly after <paramref name="from"/> up to and including <paramref name="to"/>.</summary>
    public int WorkingDaysBetween(DateOnly from, DateOnly to)
    {
        if (to <= from) return -WorkingDaysBetweenPositive(to, from);
        return WorkingDaysBetweenPositive(from, to);
    }

    private int WorkingDaysBetweenPositive(DateOnly from, DateOnly to)
    {
        var n = 0;
        for (var x = from.AddDays(1); x <= to; x = x.AddDays(1)) if (IsWorkingDay(x)) n++;
        return n;
    }

    public DateOnly LastWorkingDayOfMonth(int year, int month) =>
        PreviousWorkingDay(new DateOnly(year, month, DateTime.DaysInMonth(year, month)));

    public DateOnly FirstWorkingDayOfMonth(int year, int month) => NextWorkingDay(new DateOnly(year, month, 1));

    /// <summary>Applies holiday handling to an occurrence date. Returns null when the occurrence must be skipped.</summary>
    public DateOnly? Adjust(DateOnly d, HolidayHandling handling, out string? reason)
    {
        reason = NonWorkingReason(d);
        if (reason is null) return d;
        return handling switch
        {
            HolidayHandling.Keep => d,
            HolidayHandling.NextWorkingDay => NextWorkingDay(d),
            HolidayHandling.PreviousWorkingDay => PreviousWorkingDay(d),
            _ => null,
        };
    }

    // ── time zone conversion ──

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);

    public DateOnly Today(DateTime utcNow) => DateOnly.FromDateTime(ToLocal(utcNow));

    public DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
    }

    /// <summary>The instant a task due on <paramref name="dueDate"/> becomes overdue (due time, default end of working day).</summary>
    public DateTime DeadlineUtc(DateOnly dueDate, TimeOnly? dueTime) => ToUtc(dueDate, dueTime ?? WorkdayEnd);

    public static DateOnly StartOfWeek(DateOnly d, DayOfWeek first = DayOfWeek.Monday)
    {
        var diff = ((int)d.DayOfWeek - (int)first + 7) % 7;
        return d.AddDays(-diff);
    }
}
