using FollowUp.AI.Heuristics;
using FollowUp.Domain;
using static FollowUp.Tests.AI.AiFixtures;

namespace FollowUp.Tests.AI;

public class ClassifierTests
{
    private readonly HeuristicClassifier _classifier = new();

    [Fact]
    public void Spec_example_blocker_with_commitment()
    {
        var r = _classifier.Classify(Message("We are waiting for API credentials from the provider. We should finish by Friday."), null, Cal);

        Assert.Equal(MessageCategory.Blocker, r.Category);
        Assert.True(r.Confidence >= 0.85);
        Assert.Equal(WorkStatus.WaitingForExternalParty, r.Facts.SuggestedStatus);
        Assert.Equal("API credentials from the provider", r.Facts.Blocker);
        Assert.Equal(D(10, 9), r.Facts.CommitmentDate);
        Assert.Contains("We should finish by Friday", r.Facts.CommitmentText);
        Assert.NotEmpty(r.Signals);
        Assert.Contains("Commits to", r.Summary);
    }

    [Theory]
    [InlineData("The dealer CRM screens have been completed and deployed.", MessageCategory.Completion)]
    [InlineData("Done.", MessageCategory.Completion)]
    [InlineData("We have finished the migration.", MessageCategory.Completion)]
    [InlineData("The release is delayed because testing will take longer than planned.", MessageCategory.Delay)]
    [InlineData("Sorry, this is slipping; we need more time.", MessageCategory.Delay)]
    [InlineData("Can we move the deadline to 20 Oct?", MessageCategory.DeadlineChange)]
    [InlineData("What do you mean by the summary tab? Please clarify the scope.", MessageCategory.Clarification)]
    [InlineData("Approved, go ahead.", MessageCategory.Approval)]
    [InlineData("Looks good, proceed.", MessageCategory.Approval)]
    [InlineData("Not approved. Please redo the cash flow sheet.", MessageCategory.Rejection)]
    [InlineData("This is rejected, the numbers do not match.", MessageCategory.Rejection)]
    [InlineData("The customer is unhappy and we need management intervention.", MessageCategory.Escalation)]
    [InlineData("Please escalate this to the vendor's management.", MessageCategory.Escalation)]
    [InlineData("Need help from the infra team to set up the server.", MessageCategory.RequestForHelp)]
    [InlineData("Can someone help with the bank API certificate?", MessageCategory.RequestForHelp)]
    [InlineData("We are 60% done with the integration.", MessageCategory.ProgressUpdate)]
    [InlineData("Started working on the API today, making good progress.", MessageCategory.ProgressUpdate)]
    [InlineData("FYI, the vendor has shared the revised quotation.", MessageCategory.Information)]
    [InlineData("Anyone up for lunch at 1 pm?", MessageCategory.Unrelated)]
    [InlineData("We will complete the GST reconciliation by Friday.", MessageCategory.Commitment)]
    [InlineData("Blocked on the database migration.", MessageCategory.Blocker)]
    public void Classifies_each_category(string body, MessageCategory expected)
    {
        var r = _classifier.Classify(Message(body), null, Cal);

        Assert.Equal(expected, r.Category);
        Assert.InRange(r.Confidence, 0.5, 1.0);
        Assert.NotEmpty(r.Signals);
        Assert.False(string.IsNullOrWhiteSpace(r.Summary));
    }

    [Theory]
    [InlineData("Waiting for clarification on the GST rates.", WorkStatus.WaitingForInformation)]
    [InlineData("We are waiting for your approval on the budget.", WorkStatus.WaitingForApproval)]
    [InlineData("Still waiting for the customer to share the data.", WorkStatus.WaitingForExternalParty)]
    [InlineData("Stuck with the build server.", WorkStatus.Blocked)]
    [InlineData("All screens are completed.", WorkStatus.CompletedByAssignee)]
    [InlineData("I have started on the payroll changes.", WorkStatus.InProgress)]
    [InlineData("The module is partially completed.", WorkStatus.PartiallyCompleted)]
    public void Suggests_a_status(string body, WorkStatus expected) =>
        Assert.Equal(expected, _classifier.Classify(Message(body), null, Cal).Facts.SuggestedStatus);

    [Fact]
    public void Deadline_change_carries_the_proposed_date()
    {
        Assert.Equal(D(10, 20), _classifier.Classify(Message("Can we move the deadline to 20 Oct?"), null, Cal).Facts.ProposedDueDate);
        var r = _classifier.Classify(Message("We need extension till 25th."), null, Cal);
        Assert.Equal(MessageCategory.DeadlineChange, r.Category);
        Assert.Equal(D(10, 25), r.Facts.ProposedDueDate);
    }

    [Fact]
    public void Progress_percent_is_extracted()
    {
        var r = _classifier.Classify(Message("Update: 75% complete, testing next."), null, Cal);
        Assert.Equal(75, r.Facts.ProgressPercent);
        Assert.Equal(WorkStatus.InProgress, r.Facts.SuggestedStatus);
    }

    [Theory]
    [InlineData("URGENT: the customer portal is down.", true)]
    [InlineData("This is critical, please look asap.", true)]
    [InlineData("Not urgent, whenever you can.", false)]
    [InlineData("Shared the minutes of the meeting.", false)]
    public void Flags_urgent_language(string body, bool urgent) =>
        Assert.Equal(urgent, _classifier.Classify(Message(body), null, Cal).Facts.Urgent);

    [Theory]
    [InlineData("No blockers so far, all good.")]
    [InlineData("It will be done by Friday.")]
    [InlineData("Once it is done we will deploy.")]
    public void Negated_or_future_phrases_are_not_blockers_or_completions(string body)
    {
        var r = _classifier.Classify(Message(body), null, Cal);
        Assert.NotEqual(MessageCategory.Blocker, r.Category);
        Assert.NotEqual(MessageCategory.Completion, r.Category);
        Assert.NotEqual(WorkStatus.CompletedByAssignee, r.Facts.SuggestedStatus);
    }

    [Fact]
    public void Strong_phrases_are_confident_and_weak_signals_are_not()
    {
        Assert.True(_classifier.Classify(Message("Approved, go ahead."), null, Cal).Confidence >= 0.85);
        var weak = _classifier.Classify(Message("ok thanks"), null, Cal);
        Assert.Equal(MessageCategory.Unrelated, weak.Category);
        Assert.InRange(weak.Confidence, 0.4, 0.6);
    }

    [Theory]
    [InlineData("The report hasn't been completed yet.", MessageCategory.ProgressUpdate)]
    [InlineData("No issues so far, will update tomorrow.", MessageCategory.ProgressUpdate)]
    [InlineData("Production is down, critical issue, need immediate help from the infra team.", MessageCategory.RequestForHelp)]
    [InlineData("Happy Diwali to the whole team!", MessageCategory.Unrelated)]
    public void Probe_regressions(string body, MessageCategory expected)
    {
        var r = _classifier.Classify(Message(body), null, Cal);
        Assert.Equal(expected, r.Category);
        Assert.NotEqual(WorkStatus.CompletedByAssignee, r.Facts.SuggestedStatus);
    }

    [Fact]
    public void Partial_completion_with_something_pending_is_a_blocker()
    {
        var r = _classifier.Classify(Message("We completed part 1, part 2 is pending from the customer."), null, Cal);

        Assert.Equal(MessageCategory.Blocker, r.Category);
        Assert.Equal(WorkStatus.WaitingForExternalParty, r.Facts.SuggestedStatus);
        Assert.Contains("completion is partial: something is still outstanding", r.Signals);
    }

    [Fact]
    public void Something_not_provided_by_a_vendor_is_the_blocker()
    {
        var r = _classifier.Classify(Message("Hi sir, the vendor has not shared the API keys yet, we are stuck. Can you escalate?"), null, Cal);

        Assert.Equal(MessageCategory.Escalation, r.Category);
        Assert.Equal(WorkStatus.WaitingForExternalParty, r.Facts.SuggestedStatus);
        Assert.Equal("vendor has not shared the API keys yet", r.Facts.Blocker);
    }

    [Fact]
    public void Request_for_more_time_is_a_deadline_change_not_a_commitment()
    {
        var r = _classifier.Classify(Message("We may need till 25 Oct to finish the migration because the data is bigger than expected."), null, Cal);

        Assert.Equal(MessageCategory.DeadlineChange, r.Category);
        Assert.Equal(D(10, 25), r.Facts.ProposedDueDate);
        Assert.Null(r.Facts.CommitmentDate);
    }

    [Fact]
    public void Uses_the_task_context_as_a_signal()
    {
        var task = Candidate("Finish the dealer CRM screens", "TASK-2026-00142", ["Rahul Sharma"]);
        var r = _classifier.Classify(Message("The CRM screens are now done."), task, Cal);

        Assert.Equal(MessageCategory.Completion, r.Category);
        Assert.Contains(r.Signals, s => s.StartsWith("mentions task words", StringComparison.Ordinal));
    }
}
