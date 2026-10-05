using FollowUp.AI.Heuristics;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using static FollowUp.Tests.AI.AiFixtures;

namespace FollowUp.Tests.AI;

public class QuestionParserTests
{
    private readonly HeuristicQuestionParser _parser = new();

    private QuestionQuery Parse(string question) => _parser.Parse(question, Dir, Now, Cal);

    [Fact]
    public void Critical_work_requiring_attention()
    {
        var q = Parse("What critical work requires my attention?");
        Assert.Equal(QuestionIntent.AttentionRequired, q.Intent);
        Assert.Equal(Criticality.Critical, q.MinCriticality);
        Assert.Equal("Critical work that needs your attention", q.Interpretation);
    }

    [Fact]
    public void Promises_missed_this_week()
    {
        var q = Parse("What promises were missed this week?");
        Assert.Equal(QuestionIntent.MissedCommitments, q.Intent);
        Assert.Equal(D(10, 5), q.From);
        Assert.Equal(D(10, 11), q.To);
        Assert.False(q.ToMe);
    }

    [Fact]
    public void Commitments_made_to_me_that_were_missed()
    {
        var q = Parse("Which commitments made to me have been missed?");
        Assert.Equal(QuestionIntent.MissedCommitments, q.Intent);
        Assert.True(q.ToMe);
    }

    [Fact]
    public void What_development_has_not_responded_to()
    {
        var q = Parse("What has Development not responded to?");
        Assert.Equal(QuestionIntent.NoResponse, q.Intent);
        Assert.True(q.DepartmentId == DevDeptId || q.TeamId == DevTeamId);
        Assert.Equal(DevDeptId, q.DepartmentId);
    }

    [Fact]
    public void Finance_tasks_overdue_by_more_than_five_days()
    {
        var q = Parse("Show Finance tasks overdue by more than five days.");
        Assert.Equal(QuestionIntent.Overdue, q.Intent);
        Assert.Equal(FinanceDeptId, q.DepartmentId);
        Assert.Equal(6, q.MinDaysOverdue);
        Assert.Equal("Overdue Finance tasks, more than 5 days late", q.Interpretation);
    }

    [Fact]
    public void Recurring_activities_missed_this_month()
    {
        var q = Parse("Which recurring activities were missed this month?");
        Assert.Equal(QuestionIntent.RecurringMissed, q.Intent);
        Assert.Equal(D(10, 1), q.From);
        Assert.Equal(D(10, 31), q.To);
    }

    [Fact]
    public void What_is_blocking_the_einvoice_project()
    {
        var q = Parse("What is blocking the eInvoice project?");
        Assert.Equal(QuestionIntent.ProjectBlockers, q.Intent);
        Assert.Equal(EInvoiceProjectId, q.ProjectId);
        Assert.Contains("UAE e-Invoicing Launch", q.Interpretation);
    }

    [Theory]
    [InlineData("What is blocking the e-invoice project?")]
    [InlineData("What's holding up e-invoicing?")]
    public void Project_aliases_are_punctuation_insensitive(string question)
    {
        var q = Parse(question);
        Assert.Equal(QuestionIntent.ProjectBlockers, q.Intent);
        Assert.Equal(EInvoiceProjectId, q.ProjectId);
    }

    [Fact]
    public void Due_today_and_this_week()
    {
        var today = Parse("What is due today?");
        Assert.Equal(QuestionIntent.DueSoon, today.Intent);
        Assert.Equal(D(10, 7), today.From);
        Assert.Equal(D(10, 7), today.To);

        var week = Parse("What is due this week?");
        Assert.Equal(QuestionIntent.DueSoon, week.Intent);
        Assert.Equal(D(10, 5), week.From);
        Assert.Equal(D(10, 11), week.To);
    }

    [Theory]
    [InlineData("What is blocked?", QuestionIntent.Blocked)]
    [InlineData("Which tasks are stuck?", QuestionIntent.Blocked)]
    [InlineData("What is awaiting my approval?", QuestionIntent.AwaitingApproval)]
    [InlineData("What needs my approval?", QuestionIntent.AwaitingApproval)]
    [InlineData("Give me a summary", QuestionIntent.Summary)]
    [InlineData("How are we doing?", QuestionIntent.Summary)]
    [InlineData("What business critical tasks are open?", QuestionIntent.Critical)]
    [InlineData("Which commitments are due this week?", QuestionIntent.CommitmentsDue)]
    [InlineData("Banana bread recipe", QuestionIntent.Unknown)]
    public void Other_intents(string question, QuestionIntent intent) =>
        Assert.Equal(intent, Parse(question).Intent);

    [Fact]
    public void What_did_rahul_complete_this_week()
    {
        var q = Parse("What did Rahul complete this week?");
        Assert.Equal(QuestionIntent.CompletedRecently, q.Intent);
        Assert.Equal(RahulId, q.EmployeeId);
        Assert.Equal(D(10, 5), q.From);
        Assert.Equal(D(10, 11), q.To);
    }

    [Fact]
    public void Workload_of_a_person_and_a_team()
    {
        var person = Parse("What is Priya's workload?");
        Assert.Equal(QuestionIntent.Workload, person.Intent);
        Assert.Equal(PriyaId, person.EmployeeId);

        var team = Parse("What is the workload of the development team?");
        Assert.Equal(QuestionIntent.Workload, team.Intent);
        Assert.Equal(DevTeamId, team.TeamId);
        Assert.Null(team.DepartmentId);
    }

    [Fact]
    public void Task_lookup_by_number()
    {
        var q = Parse("status of TASK-2026-00142");
        Assert.Equal(QuestionIntent.TaskLookup, q.Intent);
        Assert.Equal("TASK-2026-00142", q.TaskNumber);
    }

    [Fact]
    public void Unknown_keeps_search_text()
    {
        var q = Parse("Banana bread recipe");
        Assert.Equal("banana bread recipe", q.SearchText);
        Assert.True(q.Confidence < 0.5);
    }

    [Fact]
    public void Probe_regressions()
    {
        var noReply = Parse("Who hasn't replied to my follow-ups?");
        Assert.Equal(QuestionIntent.NoResponse, noReply.Intent);

        var finished = Parse("What did the development team finish last week?");
        Assert.Equal(QuestionIntent.CompletedRecently, finished.Intent);
        Assert.Equal(DevTeamId, finished.TeamId);
        Assert.Equal(D(9, 28), finished.From);
        Assert.Equal(D(10, 4), finished.To);

        var status = Parse("How is the e-invoicing project going?");
        Assert.Equal(QuestionIntent.Summary, status.Intent);
        Assert.Equal(EInvoiceProjectId, status.ProjectId);

        var stuck = Parse("Anything stuck in QA?");
        Assert.Equal(QuestionIntent.Blocked, stuck.Intent);
        Assert.Equal(QaDeptId, stuck.DepartmentId);
        Assert.Equal("Blocked QA tasks", stuck.Interpretation);
    }

    [Fact]
    public void Overdue_at_least_and_weeks()
    {
        Assert.Equal(3, Parse("Which tasks are overdue by at least 3 days?").MinDaysOverdue);
        Assert.Equal(8, Parse("Show tasks overdue by more than a week").MinDaysOverdue);
    }
}
