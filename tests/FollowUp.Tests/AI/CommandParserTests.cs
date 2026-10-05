using FollowUp.AI.Heuristics;
using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Domain.Rules;
using static FollowUp.Tests.AI.AiFixtures;

namespace FollowUp.Tests.AI;

public class CommandParserTests
{
    private readonly HeuristicCommandParser _parser = new();

    private TaskDraft Parse(string command) => _parser.Parse(command, Dir, Now, Cal);

    [Fact]
    public void Spec_example_ask_rahul_and_the_development_team()
    {
        var d = Parse("Ask Rahul and the development team to finish the dealer CRM screens by Friday. Follow up every two days and tell me if it gets delayed.");

        Assert.Equal("Finish the dealer CRM screens", d.Title);
        Assert.Equal(RahulId, d.PrimaryAssigneeId);
        Assert.Equal([RahulId], d.AssigneeIds);
        Assert.Equal([DevTeamId], d.TeamIds);
        Assert.Equal(D(10, 9), d.DueDate);
        Assert.NotNull(d.FollowUp);
        Assert.Equal(FollowUpFrequency.EveryNDays, d.FollowUp!.Frequency);
        Assert.Equal(2, d.FollowUp.IntervalDays);
        Assert.True(d.NotifyCeoOnDelay);
        Assert.Equal(Criticality.Important, d.Criticality);
        Assert.Equal(TaskPriority.Normal, d.Priority);
        Assert.False(d.IsRecurring);
        Assert.Empty(d.UnresolvedNames);
        Assert.False(d.NeedsConfirmation);
        Assert.True(d.Confidence >= 0.85);
        Assert.Contains(d.Notes, n => n.Field == "Criticality" && n.Message.Contains("delayed", StringComparison.Ordinal));
        Assert.Contains(d.Notes, n => n.Field == "DueDate");
        Assert.Contains(d.Notes, n => n.Field == "FollowUp");
    }

    [Fact]
    public void Spec_example_recurring_collections_status()
    {
        var d = Parse("Create a recurring task for the finance manager to send collections status every Monday by noon.");

        Assert.True(d.IsRecurring);
        Assert.Equal("Send collections status", d.Title);
        Assert.Equal(PriyaId, d.PrimaryAssigneeId);
        Assert.NotNull(d.Recurrence);
        Assert.Equal(RecurrenceFrequency.Weekly, d.Recurrence!.Frequency);
        Assert.Equal([DayOfWeek.Monday], d.Recurrence.DaysOfWeek);
        Assert.Equal(new TimeOnly(8, 0), d.Recurrence.CreateTime);
        Assert.NotNull(d.RecurrenceDue);
        Assert.Equal(DueRuleKind.SameDay, d.RecurrenceDue!.Kind);
        Assert.Equal(new TimeOnly(12, 0), d.RecurrenceDue.DueTime);
        Assert.Null(d.DueDate);
        Assert.Contains(d.Notes, n => n.Field == "Recurrence");
    }

    [Fact]
    public void Urgency_criticality_and_approval_flags()
    {
        var d = Parse("Urgently ask Priya to file the GST return by tomorrow. This is business critical and needs my approval.");

        Assert.Equal(TaskPriority.Urgent, d.Priority);
        Assert.Equal(Criticality.BusinessCritical, d.Criticality);
        Assert.True(d.RequiresApproval);
        Assert.Equal(PriyaId, d.PrimaryAssigneeId);
        Assert.Equal(D(10, 8), d.DueDate);
        Assert.Equal("File the GST return", d.Title);
    }

    [Theory]
    [InlineData("Ask Rahul to fix the login bug by Friday, high priority.", TaskPriority.High, Criticality.Normal)]
    [InlineData("Ask Rahul to fix the login bug asap.", TaskPriority.Urgent, Criticality.Normal)]
    [InlineData("Ask Rahul to fix the critical payment bug by Friday.", TaskPriority.Normal, Criticality.Critical)]
    [InlineData("Ask Rahul to tidy the wiki, low priority.", TaskPriority.Low, Criticality.Normal)]
    public void Priority_and_criticality_words(string command, TaskPriority priority, Criticality criticality)
    {
        var d = Parse(command);
        Assert.Equal(priority, d.Priority);
        Assert.Equal(criticality, d.Criticality);
    }

    [Fact]
    public void I_want_to_approve_sets_requires_approval()
    {
        var d = Parse("Ask Priya to finalise the vendor contract by 15 Oct, I want to approve it.");
        Assert.True(d.RequiresApproval);
        Assert.Equal("Finalise the vendor contract", d.Title);
        Assert.Equal(D(10, 15), d.DueDate);
    }

    [Theory]
    [InlineData("Ask Priya to send the bank balance daily.", RecurrenceFrequency.Daily, 1)]
    [InlineData("Ask Priya to send the cash position every working day.", RecurrenceFrequency.WorkingDays, 1)]
    [InlineData("Ask Rahul to share the build status weekly.", RecurrenceFrequency.Weekly, 1)]
    [InlineData("Ask Priya to review vendor payments fortnightly.", RecurrenceFrequency.Weekly, 2)]
    [InlineData("Ask Priya to review vendor payments every two weeks.", RecurrenceFrequency.Weekly, 2)]
    [InlineData("Ask Priya to prepare the MIS pack monthly.", RecurrenceFrequency.Monthly, 1)]
    [InlineData("Ask Priya to prepare the board pack quarterly.", RecurrenceFrequency.Quarterly, 1)]
    [InlineData("Ask Priya to review insurance policies half-yearly.", RecurrenceFrequency.HalfYearly, 1)]
    [InlineData("Ask Priya to renew the trade licence annually.", RecurrenceFrequency.Yearly, 1)]
    [InlineData("Ask Priya to renew the trade licence every year.", RecurrenceFrequency.Yearly, 1)]
    [InlineData("Ask Priya to complete the statutory audit every financial year.", RecurrenceFrequency.FinancialYear, 1)]
    public void Recurrence_phrases(string command, RecurrenceFrequency frequency, int interval)
    {
        var d = Parse(command);

        Assert.True(d.IsRecurring);
        Assert.Equal(frequency, d.Recurrence!.Frequency);
        Assert.Equal(interval, d.Recurrence.Interval);
        Assert.True(d.PrimaryAssigneeId == PriyaId || d.PrimaryAssigneeId == RahulId);
        foreach (var word in new[] { "every", "daily", "weekly", "fortnightly", "monthly", "quarterly", "half-yearly", "annually" })
            Assert.DoesNotContain(word, d.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_monday_and_thursday()
    {
        var d = Parse("Ask Rahul to update the sprint board every Monday and Thursday.");
        Assert.Equal(RecurrenceFrequency.Weekly, d.Recurrence!.Frequency);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Thursday], d.Recurrence.DaysOfWeek);
        Assert.Equal("Update the sprint board", d.Title);
    }

    [Fact]
    public void First_of_every_month_due_by_the_fifth()
    {
        var d = Parse("Ask Priya to file the GST return on the 1st of every month, due by the 5th.");

        Assert.Equal(RecurrenceFrequency.Monthly, d.Recurrence!.Frequency);
        Assert.Equal(MonthlyPattern.DayOfMonth, d.Recurrence.MonthlyPattern);
        Assert.Equal(1, d.Recurrence.DayOfMonth);
        Assert.Equal(DueRuleKind.DayOfMonth, d.RecurrenceDue!.Kind);
        Assert.Equal(5, d.RecurrenceDue.Value);
        Assert.Equal("File the GST return", d.Title);
    }

    [Fact]
    public void Last_working_day_of_the_month()
    {
        var d = Parse("Ask Priya to close the books on the last working day of the month.");
        Assert.Equal(RecurrenceFrequency.Monthly, d.Recurrence!.Frequency);
        Assert.Equal(MonthlyPattern.LastWorkingDay, d.Recurrence.MonthlyPattern);
        Assert.Equal("Close the books", d.Title);
    }

    [Fact]
    public void Adjective_use_is_not_recurrence()
    {
        var d = Parse("Ask Priya to send the monthly MIS report by Friday.");
        Assert.False(d.IsRecurring);
        Assert.Equal(D(10, 9), d.DueDate);
        Assert.Equal("Send the monthly MIS report", d.Title);
    }

    [Theory]
    [InlineData("follow up daily", FollowUpFrequency.Daily, 1)]
    [InlineData("follow up every 3 days", FollowUpFrequency.EveryNDays, 3)]
    [InlineData("follow up every other day", FollowUpFrequency.EveryNDays, 2)]
    [InlineData("follow up weekly", FollowUpFrequency.Weekly, 1)]
    [InlineData("no follow-up", FollowUpFrequency.None, 1)]
    public void Follow_up_phrases(string phrase, FollowUpFrequency frequency, int interval)
    {
        var d = Parse($"Ask Rahul to finish the dealer CRM screens by Friday, {phrase}.");

        Assert.Equal(frequency, d.FollowUp!.Frequency);
        if (frequency == FollowUpFrequency.EveryNDays) Assert.Equal(interval, d.FollowUp.IntervalDays);
        Assert.Equal("Finish the dealer CRM screens", d.Title);
        Assert.False(d.IsRecurring);
    }

    [Fact]
    public void Follow_up_on_specific_weekdays()
    {
        var d = Parse("Ask Rahul to finish the dealer CRM screens by 20 Oct and follow up every Monday and Thursday.");

        Assert.Equal(FollowUpFrequency.SpecificWeekdays, d.FollowUp!.Frequency);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Thursday], d.FollowUp.Weekdays);
        Assert.False(d.IsRecurring);
        Assert.Equal(D(10, 20), d.DueDate);
    }

    [Theory]
    [InlineData("Ask Raahul to fix the login bug by Friday.", "Rahul")]
    [InlineData("Ask rahul.sharma to fix the login bug by Friday.", "Rahul")]
    [InlineData("Ask Vicky to fix the login bug by Friday.", "Vikram")]
    [InlineData("Ask the QA lead to retest the login bug by Friday.", "Anita")]
    [InlineData("Ask Priya Nair to reconcile the bank by Friday.", "Priya")]
    public void Resolves_people_by_name_email_alias_title_and_typo(string command, string expectedFirstName)
    {
        var d = Parse(command);
        var expected = Dir.People.Single(p => p.DisplayName.StartsWith(expectedFirstName, StringComparison.Ordinal)).Id;
        Assert.Equal(expected, d.PrimaryAssigneeId);
        Assert.Empty(d.UnresolvedNames);
    }

    [Fact]
    public void Team_only_assigns_the_team_leader_as_primary()
    {
        var d = Parse("Ask the dev team to upgrade the build server by next Friday.");

        Assert.Equal([DevTeamId], d.TeamIds);
        Assert.Equal(VikramId, d.PrimaryAssigneeId);
        Assert.Equal(D(10, 16), d.DueDate);
        Assert.Equal(DevDeptId, d.DepartmentId);
    }

    [Fact]
    public void Department_suffix_resolves_a_department()
    {
        var d = Parse("Ask the QA department to retest the release by Friday.");
        Assert.Equal(QaDeptId, d.DepartmentId);
        Assert.Contains(d.Notes, n => n.Field == "Department");
    }

    [Fact]
    public void Unknown_names_are_reported_and_need_confirmation()
    {
        var d = Parse("Ask Suresh and Rahul to prepare the demo by Friday.");

        Assert.Equal(["Suresh"], d.UnresolvedNames);
        Assert.Equal(RahulId, d.PrimaryAssigneeId);
        Assert.True(d.NeedsConfirmation);
        Assert.Contains(d.Notes, n => n.Field == "Assignee" && n.Message.Contains("Suresh", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_mention_sets_the_project_and_is_dropped_from_the_title()
    {
        var d = Parse("Ask Vikram to finish the PINT mapping for the eInvoice project by 30 Oct.");

        Assert.Equal(EInvoiceProjectId, d.ProjectId);
        Assert.Equal("Finish the PINT mapping", d.Title);
        Assert.Equal(D(10, 30), d.DueDate);
    }

    [Theory]
    [InlineData("Rahul, please fix the login bug by Friday.")]
    [InlineData("Rahul should fix the login bug by Friday.")]
    [InlineData("Create a task for Rahul to fix the login bug by Friday.")]
    [InlineData("ask rahul to fix the login bug by friday")]
    public void Other_command_shapes(string command)
    {
        var d = Parse(command);
        Assert.Equal(RahulId, d.PrimaryAssigneeId);
        Assert.Equal("Fix the login bug", d.Title);
        Assert.Equal(D(10, 9), d.DueDate);
    }

    [Fact]
    public void Missing_due_date_and_assignee_need_confirmation()
    {
        var d = Parse("Prepare the investor deck.");

        Assert.True(d.NeedsConfirmation);
        Assert.Null(d.PrimaryAssigneeId);
        Assert.Null(d.DueDate);
        Assert.Equal("Prepare the investor deck", d.Title);
        Assert.True(d.Confidence < 0.85);
    }

    [Fact]
    public void Assign_action_to_person()
    {
        var d = Parse("Assign the vendor onboarding checklist to Priya, due 20/10");
        Assert.Equal(PriyaId, d.PrimaryAssigneeId);
        Assert.Equal("The vendor onboarding checklist", d.Title);
        Assert.Equal(D(10, 20), d.DueDate);
        Assert.Empty(d.UnresolvedNames);
    }

    [Fact]
    public void Department_named_as_a_team_resolves_to_the_department()
    {
        var d = Parse("Create a task for the QA team to run regression tests every Friday");
        Assert.Equal(QaDeptId, d.DepartmentId);
        Assert.Empty(d.UnresolvedNames);
        Assert.True(d.IsRecurring);
        Assert.Equal([DayOfWeek.Friday], d.Recurrence!.DaysOfWeek);
        Assert.True(d.NeedsConfirmation); // no person to own it
    }

    [Fact]
    public void Monthly_reminder_is_recurring_with_a_day_of_month_due_rule()
    {
        var d = Parse("Schedule a monthly reminder for Priya to submit the TDS return by the 7th");

        Assert.True(d.IsRecurring);
        Assert.Equal(RecurrenceFrequency.Monthly, d.Recurrence!.Frequency);
        Assert.Equal(DueRuleKind.DayOfMonth, d.RecurrenceDue!.Kind);
        Assert.Equal(7, d.RecurrenceDue.Value);
        Assert.Equal(PriyaId, d.PrimaryAssigneeId);
        Assert.Equal("Submit the TDS return", d.Title);
    }

    [Fact]
    public void No_follow_up_needed_leaves_a_clean_title()
    {
        var d = Parse("Ask Anita to prepare the audit file by end of month, no follow-up needed");
        Assert.Equal("Prepare the audit file", d.Title);
        Assert.Equal(FollowUpFrequency.None, d.FollowUp!.Frequency);
        Assert.Equal(D(10, 31), d.DueDate);
    }

    [Fact]
    public void Every_mention_of_the_project_phrase_is_removed_from_the_title()
    {
        var d = Parse("Please ask Vikram to finalise the e-invoicing go-live plan for the eInvoice project by 15 October, high priority, I want to approve it.");

        Assert.Equal("Finalise the e-invoicing go-live plan", d.Title);
        Assert.Equal(EInvoiceProjectId, d.ProjectId);
        Assert.Equal(TaskPriority.High, d.Priority);
        Assert.True(d.RequiresApproval);
        Assert.Equal(D(10, 15), d.DueDate);
    }

    [Fact]
    public void The_ceo_is_never_an_assignee()
    {
        var d = Parse("Ask Arjun Mehta to sign the board minutes by Friday.");
        Assert.Null(d.PrimaryAssigneeId);
        Assert.Empty(d.AssigneeIds);
    }
}
