using System.Globalization;
using FollowUp.AI.Heuristics;
using static FollowUp.Tests.AI.AiFixtures;

namespace FollowUp.Tests.AI;

public class CommitmentExtractorTests
{
    private readonly HeuristicCommitmentExtractor _extractor = new();

    [Theory]
    [InlineData("We will complete this by Friday.", "2026-10-09", "complete this", 0.85)]
    [InlineData("I'll send it by tomorrow EOD.", "2026-10-08", "send it", 0.85)]
    [InlineData("The report should be done by Thursday.", "2026-10-08", "The report", 0.7)]
    [InlineData("We should finish by Friday.", "2026-10-09", "finish", 0.7)]
    [InlineData("ETA Monday.", "2026-10-12", null, 0.8)]
    [InlineData("We expect to close it by 15 Oct.", "2026-10-15", "close it", 0.8)]
    [InlineData("Will be completed by Thursday.", "2026-10-08", null, 0.8)]
    [InlineData("Target date is 20th.", "2026-10-20", null, 0.7)]
    [InlineData("Committed for next week.", "2026-10-12", null, 0.7)]
    [InlineData("We are going to deliver the build next Friday.", "2026-10-16", "deliver the build", 0.8)]
    public void Detects_promises_by_the_writer(string text, string date, string? what, double minConfidence)
    {
        var result = _extractor.Extract(text, Now, Cal);

        var c = Assert.Single(result);
        Assert.Equal(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture), c.Date);
        Assert.Equal(what, c.What);
        Assert.True(c.Confidence >= minConfidence, $"confidence {c.Confidence} < {minConfidence}");
        Assert.InRange(c.Confidence, 0, 1);
    }

    [Fact]
    public void Keeps_the_time_of_day()
    {
        var c = Assert.Single(_extractor.Extract("I'll send it by tomorrow EOD", Now, Cal));
        Assert.Equal(new TimeOnly(18, 30), c.Time);
    }

    [Theory]
    [InlineData("Can you finish by Friday?")]
    [InlineData("Please complete by Friday.")]
    [InlineData("Could you send the file by tomorrow")]
    [InlineData("It won't be done by Friday.")]
    [InlineData("We will not finish by Friday.")]
    [InlineData("We can't finish by Friday.")]
    [InlineData("The vendor will send the credentials by Friday.")]
    [InlineData("The meeting is on Friday.")]
    [InlineData("We will complete this soon.")]
    public void Ignores_requests_questions_negations_and_third_parties(string text) =>
        Assert.Empty(_extractor.Extract(text, Now, Cal));

    [Fact]
    public void Hypothetical_promises_are_excluded_or_weak()
    {
        var result = _extractor.Extract("If we get credentials we could finish by Friday.", Now, Cal);

        Assert.True(result.Count == 0 || result.All(c => c.Confidence < 0.5));
    }

    [Fact]
    public void Firm_promises_score_higher_than_hedged_ones()
    {
        var firm = Assert.Single(_extractor.Extract("We will complete this by Friday.", Now, Cal));
        var hedged = Assert.Single(_extractor.Extract("We will try to complete this by Friday.", Now, Cal));

        Assert.True(firm.Confidence > hedged.Confidence);
    }

    [Fact]
    public void Negated_date_does_not_hide_the_new_promise()
    {
        var c = Assert.Single(_extractor.Extract("It won't be done by Friday, but we will finish by Monday.", Now, Cal));
        Assert.Equal(D(10, 12), c.Date);
    }

    [Fact]
    public void Finds_the_promise_in_a_longer_message_and_skips_quoted_history()
    {
        const string body = """
            Hi sir,
            We are waiting for API credentials from the provider. We should finish by Friday.
            Regards, Rahul

            On Mon, 5 Oct 2026 at 10:00, Arjun wrote:
            > Please complete this by 12 Oct.
            """;

        var c = Assert.Single(_extractor.Extract(body, Now, Cal));
        Assert.Equal(D(10, 9), c.Date);
        Assert.Contains("We should finish by Friday", c.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Async_port_returns_the_same_result()
    {
        var sync = _extractor.Extract("We will complete this by Friday.", Now, Cal);
        var async = await _extractor.ExtractAsync("We will complete this by Friday.", Now, Cal, CancellationToken.None);
        Assert.Equal(sync, async);
    }
}
