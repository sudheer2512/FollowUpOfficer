using System.Globalization;
using FollowUp.AI.Dates;
using static FollowUp.Tests.AI.AiFixtures;

namespace FollowUp.Tests.AI;

/// <summary>Reference instant: Wednesday 7 Oct 2026 10:00 IST, Saturday / Sunday weekend, work day ends 18:30.</summary>
public class DateExpressionParserTests
{
    [Theory]
    [InlineData("by Friday", "2026-10-09", null)]
    [InlineData("on Friday", "2026-10-09", null)]
    [InlineData("this Friday", "2026-10-09", null)]
    [InlineData("next Friday", "2026-10-16", null)]
    [InlineData("Friday next week", "2026-10-16", null)]
    [InlineData("by Mon", "2026-10-12", null)]
    [InlineData("this Wednesday", "2026-10-07", null)]
    [InlineData("today", "2026-10-07", null)]
    [InlineData("tonight", "2026-10-07", "20:00")]
    [InlineData("by EOD", "2026-10-07", "18:30")]
    [InlineData("end of day", "2026-10-07", "18:30")]
    [InlineData("tomorrow", "2026-10-08", null)]
    [InlineData("tomorrow by noon", "2026-10-08", "12:00")]
    [InlineData("tomorrow EOD", "2026-10-08", "18:30")]
    [InlineData("tomorrow morning", "2026-10-08", "10:00")]
    [InlineData("Friday evening", "2026-10-09", "18:00")]
    [InlineData("day after tomorrow", "2026-10-09", null)]
    [InlineData("end of week", "2026-10-09", null)]
    [InlineData("EOW", "2026-10-09", null)]
    [InlineData("end of the month", "2026-10-31", null)]
    [InlineData("EOM", "2026-10-31", null)]
    [InlineData("next week", "2026-10-12", null)]
    [InlineData("within 3 days", "2026-10-10", null)]
    [InlineData("in 5 days", "2026-10-12", null)]
    [InlineData("in two weeks", "2026-10-21", null)]
    [InlineData("within two working days", "2026-10-09", null)]
    [InlineData("by the 15th", "2026-10-15", null)]
    [InlineData("on 5th", "2026-11-05", null)]
    [InlineData("15 Oct", "2026-10-15", null)]
    [InlineData("Oct 20", "2026-10-20", null)]
    [InlineData("15 October 2026", "2026-10-15", null)]
    [InlineData("15/10", "2026-10-15", null)]
    [InlineData("15-10-2026", "2026-10-15", null)]
    [InlineData("2026-10-15", "2026-10-15", null)]
    [InlineData("1 Oct", "2027-10-01", null)]
    [InlineData("by 5 pm", "2026-10-07", "17:00")]
    [InlineData("at 17:00", "2026-10-07", "17:00")]
    [InlineData("by 5:30pm", "2026-10-07", "17:30")]
    [InlineData("by 9 am", "2026-10-08", "09:00")]
    public void Resolves_expression(string text, string expectedDate, string? expectedTime)
    {
        var matches = DateExpressionParser.Parse(text, Now, Cal);

        var m = Assert.Single(matches);
        Assert.Equal(DateOnly.ParseExact(expectedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), m.Date);
        Assert.Equal(expectedTime is null ? null : TimeOnly.ParseExact(expectedTime, "HH:mm", CultureInfo.InvariantCulture), m.Time);
        Assert.InRange(m.Confidence, 0.5, 1.0);
    }

    [Fact]
    public void Finds_every_expression_with_its_span()
    {
        const string text = "I'll send the draft by tomorrow EOD and the final version next Friday.";

        var matches = DateExpressionParser.Parse(text, Now, Cal);

        Assert.Equal(2, matches.Count);
        Assert.Equal("tomorrow EOD", matches[0].Text);
        Assert.Equal(text.IndexOf("tomorrow", StringComparison.Ordinal), matches[0].Index);
        Assert.Equal(D(10, 8), matches[0].Date);
        Assert.Equal("next Friday", matches[1].Text);
        Assert.Equal(D(10, 16), matches[1].Date);
    }

    [Fact]
    public void By_friday_on_a_friday_is_today_until_the_work_day_ends()
    {
        var fridayMorning = new DateTime(2026, 10, 9, 4, 30, 0, DateTimeKind.Utc);   // 10:00 IST
        var fridayNight = new DateTime(2026, 10, 9, 14, 30, 0, DateTimeKind.Utc);    // 20:00 IST

        Assert.Equal(D(10, 9), DateExpressionParser.ParseFirst("by Friday", fridayMorning, Cal)!.Date);
        Assert.Equal(D(10, 16), DateExpressionParser.ParseFirst("by Friday", fridayNight, Cal)!.Date);
    }

    [Fact]
    public void Past_day_month_rolls_to_next_year()
    {
        var december = new DateTime(2026, 12, 10, 4, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2027, 10, 15), DateExpressionParser.ParseFirst("by 15 Oct", december, Cal)!.Date);
        Assert.Equal(new DateOnly(2026, 10, 15), DateExpressionParser.ParseFirst("15 Oct 2026", december, Cal)!.Date);
    }

    [Theory]
    [InlineData("Good morning team, hope you are well")]
    [InlineData("We provide support 24/7")]
    [InlineData("The 3rd party vendor replied")]
    [InlineData("Version 2 is ready and 50/50 split agreed")]
    [InlineData("You may proceed with the plan")]
    public void Ignores_text_without_dates(string text) =>
        Assert.Empty(DateExpressionParser.Parse(text, Now, Cal));

    [Fact]
    public void Number_words_one_to_twelve_are_supported()
    {
        Assert.Equal(D(10, 19), DateExpressionParser.ParseFirst("within twelve days", Now, Cal)!.Date);
        Assert.Equal(D(10, 8), DateExpressionParser.ParseFirst("in one day", Now, Cal)!.Date);
        Assert.Equal(D(10, 9), DateExpressionParser.ParseFirst("in a couple of days", Now, Cal)!.Date);
    }
}
