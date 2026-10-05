using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.Tests.Domain;

public class FollowUpRulesTests
{
    private static readonly BusinessCalendar Cal = new(BusinessCalendar.ResolveTimeZone("Asia/Kolkata"), [DayOfWeek.Saturday, DayOfWeek.Sunday],
        [(new DateOnly(2026, 10, 2), "Gandhi Jayanti")]);

    private static DateTime Ist(int month, int day, int hour, int minute = 0) => Cal.ToUtc(new DateOnly(2026, month, day), new TimeOnly(hour, minute));

    [Fact]
    public void Every_two_days_on_working_days()
    {
        var s = FollowUpSchedule.EveryDays(2);
        var next = FollowUpScheduler.Next(s, Ist(9, 30, 10), new DateOnly(2026, 9, 28), Cal);
        Assert.Equal(Ist(10, 5, 10), next);   // Oct 2 is a holiday and 3–4 are the weekend → Monday
    }

    [Fact]
    public void Every_monday_and_thursday_until_completed()
    {
        var s = FollowUpSchedule.OnWeekdays(DayOfWeek.Monday, DayOfWeek.Thursday);
        var a = FollowUpScheduler.Next(s, Ist(10, 5, 11), new DateOnly(2026, 10, 1), Cal);
        var b = FollowUpScheduler.Next(s, a!.Value, new DateOnly(2026, 10, 1), Cal);
        Assert.Equal(Ist(10, 8, 10), a);
        Assert.Equal(Ist(10, 12, 10), b);
        Assert.Equal("Every Monday and Thursday at 10:00 until completed", s.Describe());
    }

    [Fact]
    public void Custom_hours_stay_inside_working_hours()
    {
        var s = new FollowUpSchedule { Frequency = FollowUpFrequency.Custom, IntervalHours = 4 };
        Assert.Equal(Ist(10, 6, 9, 30), FollowUpScheduler.Next(s, Ist(10, 5, 17), new DateOnly(2026, 10, 5), Cal));
    }

    [Fact]
    public void Missed_points_are_counted_for_coalescing()
    {
        var s = FollowUpSchedule.Daily();
        // Server down from Friday 9 Oct 09:00 to Wednesday 14 Oct 11:00 → Fri, Mon, Tue, Wed points missed.
        Assert.Equal(4, FollowUpScheduler.CountPoints(s, Ist(10, 9, 9), Ist(10, 14, 11), new DateOnly(2026, 10, 1), Cal));
    }

    private static FollowUpContext Ctx(Func<FollowUpContext, FollowUpContext>? tweak = null)
    {
        var c = new FollowUpContext
        {
            Now = Ist(10, 7, 10), Today = new DateOnly(2026, 10, 7), Status = WorkStatus.InProgress, Criticality = Criticality.Important,
            Acknowledgement = AcknowledgementState.Accepted, DueDate = new DateOnly(2026, 10, 9), DaysUntilDue = 2,
        };
        return tweak is null ? c : tweak(c);
    }

    [Fact]
    public void Recent_update_suppresses_reminder()
    {
        var d = FollowUpDecisionEngine.Decide(Ctx(c => c with { LastAssigneeActivityAt = c.Now.AddHours(-3), LastActivityChannel = "Teams" }));
        Assert.Equal(FollowUpAction.Skip, d.Action);
        Assert.Contains("via Teams", d.Reason);
    }

    [Fact]
    public void Future_commitment_suppresses_reminder_and_missed_commitment_triggers_check()
    {
        Assert.Equal(FollowUpAction.Skip, FollowUpDecisionEngine.Decide(Ctx(c => c with { OpenCommitmentDate = new DateOnly(2026, 10, 9) })).Action);
        var missed = FollowUpDecisionEngine.Decide(Ctx(c => c with { HasMissedCommitment = true }));
        Assert.Equal(FollowUpKind.CommitmentCheck, missed.Kind);
        Assert.True(missed.IncludeTeamLeader);
    }

    [Fact]
    public void Overdue_reminder_escalates_tone_and_includes_team_leader()
    {
        var d = FollowUpDecisionEngine.Decide(Ctx(c => c with { IsOverdue = true, DaysOverdue = 6, DaysUntilDue = null }));
        Assert.Equal(FollowUpKind.Overdue, d.Kind);
        Assert.Equal("urgent", d.Tone);
        Assert.True(d.IncludeTeamLeader);
        Assert.Equal("Overdue by 6 days", d.Reason);
    }

    [Theory]
    [InlineData(WorkStatus.Completed, FollowUpAction.Stop)]
    [InlineData(WorkStatus.Deferred, FollowUpAction.Skip)]
    [InlineData(WorkStatus.CompletionAwaitingApproval, FollowUpAction.Remind)]
    public void Status_rules(WorkStatus status, FollowUpAction expected) =>
        Assert.Equal(expected, FollowUpDecisionEngine.Decide(Ctx(c => c with { Status = status })).Action);

    [Fact]
    public void Paused_and_unacknowledged()
    {
        Assert.Equal(FollowUpAction.Skip, FollowUpDecisionEngine.Decide(Ctx(c => c with { PausedUntil = new DateOnly(2026, 10, 12), PauseReason = FollowUpPauseReason.Vendor })).Action);
        Assert.Equal(FollowUpKind.AcknowledgementReminder, FollowUpDecisionEngine.Decide(Ctx(c => c with { Acknowledgement = AcknowledgementState.Pending })).Kind);
        Assert.Equal(FollowUpKind.BlockerCheck, FollowUpDecisionEngine.Decide(Ctx(c => c with { Status = WorkStatus.WaitingForExternalParty, IsBlocked = true })).Kind);
        Assert.Equal(FollowUpKind.DueSoon, FollowUpDecisionEngine.Decide(Ctx()).Kind);
    }

    [Fact]
    public void Deadline_wording_and_buckets()
    {
        var now = Ist(10, 7, 19);   // after the 18:30 end of day
        Assert.Equal("Overdue today", DeadlineRules.Describe(new DateOnly(2026, 10, 7), null, WorkStatus.InProgress, now, Cal));
        Assert.Equal("Overdue by 7 days", DeadlineRules.Describe(new DateOnly(2026, 9, 30), null, WorkStatus.InProgress, now, Cal));
        Assert.Equal(DeadlineBucket.SeverelyOverdue, DeadlineRules.Bucket(new DateOnly(2026, 9, 30), null, WorkStatus.InProgress, now, Cal, 7));
        Assert.Equal(DeadlineBucket.DueTomorrow, DeadlineRules.Bucket(new DateOnly(2026, 10, 8), null, WorkStatus.Assigned, now, Cal, 7));
        Assert.False(DeadlineRules.IsOverdue(new DateOnly(2026, 9, 30), null, WorkStatus.CompletionAwaitingApproval, now, Cal));
    }

    [Fact]
    public void Task_numbers()
    {
        Assert.Equal("TASK-2026-00142", TaskNumbers.Format(2026, 142));
        Assert.Equal(["TASK-2026-00142", "TASK-2026-00007"], TaskNumbers.FindAll("re: task-2026-00142 and TASK-2026-00007 (also TASK-2026-00142)"));
        Assert.True(TaskNumbers.TryParse("TASK-2026-00142 will be completed by Thursday", out var y, out var n));
        Assert.Equal((2026, 142), (y, n));
    }

    [Fact]
    public void Status_transitions()
    {
        Assert.True(TaskStatusRules.CanTransition(WorkStatus.InProgress, WorkStatus.Blocked));
        Assert.False(TaskStatusRules.CanTransition(WorkStatus.InProgress, WorkStatus.Completed));   // completion goes through confirmation
        Assert.True(TaskStatusRules.CanTransition(WorkStatus.CompletionAwaitingApproval, WorkStatus.Completed));
        Assert.True(TaskStatusRules.CanTransition(WorkStatus.Completed, WorkStatus.InProgress));
        Assert.False(TaskStatusRules.CanTransition(WorkStatus.Completed, WorkStatus.Cancelled));
    }
}
