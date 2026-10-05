using FollowUp.AI.Heuristics;
using FollowUp.Core.Abstractions;
using static FollowUp.Tests.AI.AiFixtures;

namespace FollowUp.Tests.AI;

public class TaskMatcherTests
{
    private readonly HeuristicTaskMatcher _matcher = new();

    private static readonly TaskCandidate Crm = Candidate("Finish the dealer CRM screens", "TASK-2026-00142", ["Rahul Sharma"],
        description: "Lead list, test-drive booking and quotation screens for the dealer CRM.", project: "Dealer CRM", keywords: ["test drive", "quotation"], due: D(10, 9));

    private static readonly TaskCandidate Gst = Candidate("File the GST return for September", "TASK-2026-00150", ["Priya Nair"],
        description: "GSTR-3B filing and reconciliation.", project: "Finance Compliance", keywords: ["GSTR-3B"], due: D(10, 20));

    private static readonly TaskCandidate EInvoice = Candidate("Integrate the e-invoicing API", "TASK-2026-00151", ["Vikram Rao"],
        description: "Connect ERP to the UAE e-invoicing provider.", project: "UAE e-Invoicing Launch", keywords: ["FTA", "PINT"], due: D(10, 30));

    private static readonly TaskCandidate[] All = [Crm, Gst, EInvoice];

    [Fact]
    public void Clear_match_is_confident_and_ranked_first()
    {
        var scores = _matcher.Score(Message("Layouts are ready, will share for review.", subject: "Dealer CRM screens", sender: "Rahul Sharma"), All, Cal);

        Assert.Equal(Crm.TaskId, scores[0].TaskId);
        Assert.True(scores[0].Confidence >= 0.85, $"top confidence {scores[0].Confidence}");
        Assert.Contains("subject matches title words: dealer, crm, screens", scores[0].Signals);
        Assert.Contains("sender is the assignee", scores[0].Signals);
        Assert.All(scores.Skip(1), s => Assert.True(s.Confidence <= 0.5, $"{s.Confidence} for a non-matching task"));
        Assert.False(string.IsNullOrWhiteSpace(scores[0].Explanation));
    }

    [Fact]
    public void Vague_message_scores_low_everywhere()
    {
        var scores = _matcher.Score(Message("Quick update on the work, will call you later.", subject: "Update", sender: "Anita Desai"), All, Cal);

        Assert.All(scores, s => Assert.True(s.Confidence <= 0.5, $"{s.Confidence}"));
    }

    [Fact]
    public void Ranks_by_content_when_the_sender_is_not_an_assignee()
    {
        var scores = _matcher.Score(Message("The GSTR-3B reconciliation is pending for the September GST return.", sender: "Anita Desai"), All, Cal);

        Assert.Equal(Gst.TaskId, scores[0].TaskId);
        Assert.True(scores[0].Confidence > scores[1].Confidence + 0.2);
        Assert.Contains(scores[0].Signals, s => s.StartsWith("keywords matched", StringComparison.Ordinal));
    }

    [Fact]
    public void Task_number_in_the_text_is_decisive()
    {
        var scores = _matcher.Score(Message("Any news on TASK-2026-00151?", sender: "Anita Desai"), All, Cal);

        Assert.Equal(EInvoice.TaskId, scores[0].TaskId);
        Assert.True(scores[0].Confidence >= 0.95);
        Assert.Contains("task number TASK-2026-00151 mentioned", scores[0].Signals);
    }

    [Fact]
    public void Prior_score_and_signals_from_core_are_combined()
    {
        var withPrior = Gst with { PriorScore = 0.6, PriorSignals = ["same conversation thread"] };

        var scores = _matcher.Score(Message("Thanks, noted.", sender: "Anita Desai"), [withPrior, Crm], Cal);

        var gst = scores.Single(s => s.TaskId == Gst.TaskId);
        Assert.True(gst.Confidence >= 0.6);
        Assert.Equal("same conversation thread", gst.Signals[0]);
        Assert.Equal(Gst.TaskId, scores[0].TaskId);
    }

    [Fact]
    public void Sender_given_as_an_email_address_is_recognised()
    {
        var scores = _matcher.Score(Message("CRM screens done.", sender: "rahul.sharma@contoso.in"), All, Cal);
        Assert.Contains("sender is the assignee", scores.Single(s => s.TaskId == Crm.TaskId).Signals);
    }

    [Fact]
    public void Date_near_the_due_date_is_a_small_boost()
    {
        var without = _matcher.Score(Message("Dealer CRM screens on track.", sender: "Anita Desai"), [Crm], Cal)[0].Confidence;
        var with = _matcher.Score(Message("Dealer CRM screens on track for Friday.", sender: "Anita Desai"), [Crm], Cal)[0];

        Assert.True(with.Confidence > without);
        Assert.Contains(with.Signals, s => s.Contains("near the due date", StringComparison.Ordinal));
    }

    [Fact]
    public void No_candidates_returns_empty() =>
        Assert.Empty(_matcher.Score(Message("hello"), [], Cal));
}
