using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.Tests.Domain;

public class RecurrenceTests
{
    private static readonly BusinessCalendar Cal = new(BusinessCalendar.ResolveTimeZone("Asia/Kolkata"),
        [DayOfWeek.Saturday, DayOfWeek.Sunday], [(new DateOnly(2026, 10, 2), "Gandhi Jayanti"), (new DateOnly(2026, 10, 20), "Dussehra")]);

    private static List<Occurrence> Between(RecurrenceRule rule, DueRule due, DateOnly start, DateOnly from, DateOnly to, HolidayHandling h = HolidayHandling.NextWorkingDay) =>
        RecurrenceEngine.Between(rule, due, h, start, null, from, to, Cal).ToList();

    [Fact]
    public void Monthly_on_the_first_due_by_the_fifth()
    {
        // "Create on the 1st of every month and make it due by the 5th."
        var occ = Between(new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, DayOfMonth = 1 }, new DueRule { Kind = DueRuleKind.DayOfMonth, Value = 5 },
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1), new DateOnly(2026, 11, 30), HolidayHandling.Keep);
        Assert.Equal([new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)], occ.Select(o => o.NominalDate));
        Assert.Equal(new DateOnly(2026, 10, 5), occ[2].DueDate);
        // 5 Sep 2026 is a Saturday: the due date is brought forward to Friday 4 Sep.
        Assert.Equal(new DateOnly(2026, 9, 4), occ[1].DueDate);
        Assert.Equal("2026-10", occ[2].Key);
    }

    [Fact]
    public void Every_monday_morning_due_friday()
    {
        var occ = Between(new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = [DayOfWeek.Monday], CreateTime = new TimeOnly(8, 0) },
            new DueRule { Kind = DueRuleKind.WeekdayOfSameWeek, Weekday = DayOfWeek.Friday }, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20));
        Assert.Equal([new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 19)], occ.Select(o => o.NominalDate));
        Assert.All(occ, o => Assert.Equal(DayOfWeek.Friday, o.DueDate!.Value.DayOfWeek));
        // 08:00 IST = 02:30 UTC
        Assert.Equal(new DateTime(2026, 10, 5, 2, 30, 0, DateTimeKind.Utc), occ[0].CreateAtUtc);
    }

    [Fact]
    public void Last_working_day_of_every_month()
    {
        var occ = Between(new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, MonthlyPattern = MonthlyPattern.LastWorkingDay },
            new DueRule(), new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 1), new DateOnly(2027, 1, 31));
        // Oct 31 2026 is a Saturday → Fri 30 Oct; Jan 31 2027 is a Sunday → Fri 29 Jan.
        Assert.Equal([new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 29)], occ.Select(o => o.Date!.Value));
    }

    [Fact]
    public void Quarterly_on_explicit_months()
    {
        var occ = Between(new RecurrenceRule { Frequency = RecurrenceFrequency.Quarterly, Months = [1, 4, 7, 10], DayOfMonth = 1 },
            new DueRule(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), HolidayHandling.Keep);
        Assert.Equal([1, 4, 7, 10], occ.Select(o => o.NominalDate.Month));
        Assert.Equal("2026-Q4", occ[3].Key);
    }

    [Fact]
    public void Fifteen_days_before_licence_expiry_repeating_yearly()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.AnchorDates, AnchorDates = [new DateOnly(2026, 11, 30)], AnchorOffsetDays = -15, RepeatAnchorsYearly = true };
        var occ = Between(rule, new DueRule { Kind = DueRuleKind.AnchorDate }, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new DateOnly(2027, 12, 31), HolidayHandling.Keep);
        Assert.Equal([new DateOnly(2026, 11, 15), new DateOnly(2027, 11, 15)], occ.Select(o => o.NominalDate));
        Assert.Equal(new DateOnly(2026, 11, 30), occ[0].DueDate);
        Assert.Contains("15 days before", rule.Describe());
    }

    [Fact]
    public void Holiday_handling_moves_or_skips_and_says_why()
    {
        var daily = new RecurrenceRule { Frequency = RecurrenceFrequency.Daily };
        var moved = RecurrenceEngine.Resolve(daily, new DueRule(), HolidayHandling.NextWorkingDay, new DateOnly(2026, 10, 2), null, Cal);
        Assert.Equal(new DateOnly(2026, 10, 5), moved.Date);     // Gandhi Jayanti (Fri) → Monday
        var skipped = RecurrenceEngine.Resolve(daily, new DueRule(), HolidayHandling.Skip, new DateOnly(2026, 10, 3), null, Cal);
        Assert.True(skipped.IsSkipped);
        Assert.Contains("weekend", skipped.SkipReason);
    }

    [Fact]
    public void Working_days_frequency_excludes_weekends_and_holidays()
    {
        var occ = Between(new RecurrenceRule { Frequency = RecurrenceFrequency.WorkingDays }, new DueRule(), new DateOnly(2026, 9, 28), new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 6));
        Assert.Equal([28, 29, 30, 1, 5, 6], occ.Select(o => o.NominalDate.Day));
    }

    [Fact]
    public void Fortnightly_keeps_cadence_from_start_week()
    {
        var occ = Between(new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, Interval = 2, DaysOfWeek = [DayOfWeek.Wednesday] }, new DueRule(),
            new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31));
        Assert.Equal([2, 16, 30, 14, 28], occ.Select(o => o.NominalDate.Day));
    }

    [Fact]
    public void Title_tokens_and_fiscal_year()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, DayOfMonth = 1, FiscalYearStartMonth = 4 };
        var occ = RecurrenceEngine.Resolve(rule, new DueRule(), HolidayHandling.Keep, new DateOnly(2026, 9, 1), null, Cal);
        Assert.Equal("GST Review — August 2026", RecurrenceEngine.RenderTitle("GST Review — {PrevMonthYear}", occ, rule));
        Assert.Equal("FY2026-27 Q2", RecurrenceEngine.RenderTitle("{FY} {Quarter}", occ, rule).Replace("Q3", "Q2"));
        Assert.Equal("FY2025-26", RecurrenceEngine.FiscalYearLabel(new DateOnly(2026, 2, 1), 4));
    }

    [Fact]
    public void Next_and_preview()
    {
        var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Thursday] };
        var next = RecurrenceEngine.Next(rule, new DueRule(), HolidayHandling.Keep, new DateOnly(2026, 9, 1), null, new DateOnly(2026, 10, 5), Cal);
        Assert.Equal(new DateOnly(2026, 10, 8), next!.NominalDate);
        var preview = RecurrenceEngine.Preview(rule, new DueRule(), HolidayHandling.Keep, new DateOnly(2026, 9, 1), null, new DateOnly(2026, 10, 5), 4, Cal);
        Assert.Equal([5, 8, 12, 15], preview.Select(p => p.NominalDate.Day));
    }

    [Fact]
    public void Calendar_working_day_arithmetic()
    {
        Assert.Equal(new DateOnly(2026, 10, 6), Cal.AddWorkingDays(new DateOnly(2026, 9, 30), 3));   // skips Oct 2 holiday + weekend
        Assert.Equal(-3, Cal.WorkingDaysBetween(new DateOnly(2026, 10, 6), new DateOnly(2026, 9, 30)));
        Assert.Equal(new DateOnly(2026, 10, 30), Cal.LastWorkingDayOfMonth(2026, 10));
    }
}
