using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.Tests.Domain;

public class EscalationScoringAccessTests
{
    private static readonly BusinessCalendar Cal = new(BusinessCalendar.ResolveTimeZone("Asia/Kolkata"), [DayOfWeek.Saturday, DayOfWeek.Sunday]);
    private static DateTime Ist(int month, int day, int hour) => Cal.ToUtc(new DateOnly(2026, month, day), new TimeOnly(hour, 0));

    private static List<EscalationStep> StandardLadder() =>
    [
        new() { Order = 1, Trigger = EscalationTrigger.OnDue, Threshold = 0, Targets = EscalationTarget.Assignee, Level = 1 },
        new() { Order = 2, Trigger = EscalationTrigger.Overdue, Threshold = 1, Targets = EscalationTarget.Assignee | EscalationTarget.TeamLeader, Level = 2 },
        new() { Order = 3, Trigger = EscalationTrigger.Overdue, Threshold = 3, Targets = EscalationTarget.DepartmentHead, Level = 3 },
        new() { Order = 4, Trigger = EscalationTrigger.Overdue, Threshold = 5, Targets = EscalationTarget.Ceo, Level = 4 },
        new() { Order = 5, Trigger = EscalationTrigger.NoResponse, Threshold = 3, Targets = EscalationTarget.TeamLeader, Level = 2 },
    ];

    private static EscalationSnapshot Snap(DateTime now, DateOnly due, DateTime? noResponseSince = null) => new()
    {
        TaskId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Now = now, DueDate = due, Criticality = Criticality.Important, NoResponseSince = noResponseSince,
    };

    [Fact]
    public void Ladder_fires_step_by_step_with_stable_keys()
    {
        var steps = StandardLadder();
        var due = new DateOnly(2026, 10, 5);
        var onDue = EscalationEvaluator.Evaluate(Snap(Ist(10, 5, 11), due), steps, Cal);
        Assert.Single(onDue);
        Assert.Equal(1, onDue[0].Step.Level);

        var dayTwo = EscalationEvaluator.Evaluate(Snap(Ist(10, 6, 20), due), steps, Cal);
        Assert.Contains(dayTwo, d => d.Step.Level == 2 && !d.Superseded);
        // The same step always yields the same idempotency key within a cycle.
        Assert.Equal(EscalationEvaluator.Evaluate(Snap(Ist(10, 7, 9), due), steps, Cal).Single(d => d.Step.Order == 1).IdempotencyKey,
            onDue[0].IdempotencyKey);
    }

    [Fact]
    public void After_downtime_only_the_highest_step_notifies()
    {
        var due = EscalationEvaluator.Evaluate(Snap(Ist(10, 12, 10), new DateOnly(2026, 10, 5)), StandardLadder(), Cal);
        var live = due.Where(d => !d.Superseded).ToList();
        Assert.Single(live);
        Assert.Equal(4, live[0].Step.Level);
        Assert.Equal(3, due.Count(d => d.Superseded));
    }

    [Fact]
    public void Changing_the_due_date_starts_a_new_cycle_and_pause_suppresses()
    {
        var steps = StandardLadder();
        var a = EscalationEvaluator.Evaluate(Snap(Ist(10, 5, 11), new DateOnly(2026, 10, 5)), steps, Cal)[0].IdempotencyKey;
        var b = EscalationEvaluator.Evaluate(Snap(Ist(10, 9, 11), new DateOnly(2026, 10, 9)), steps, Cal)[0].IdempotencyKey;
        Assert.NotEqual(a, b);
        var paused = Snap(Ist(10, 12, 10), new DateOnly(2026, 10, 5)) with { IsFollowUpPaused = true };
        Assert.Empty(EscalationEvaluator.Evaluate(paused, steps, Cal));
    }

    [Fact]
    public void No_response_escalation_uses_the_silence_episode()
    {
        var since = Ist(10, 1, 10);
        var due = EscalationEvaluator.Evaluate(Snap(Ist(10, 5, 10), new DateOnly(2026, 10, 30), since), StandardLadder(), Cal);
        var nr = Assert.Single(due);
        Assert.Equal(EscalationTrigger.NoResponse, nr.Step.Trigger);
        Assert.EndsWith(since.Ticks.ToString(), nr.IdempotencyKey);
    }

    [Fact]
    public void Health_and_attention_reflect_risk()
    {
        var now = Ist(10, 12, 10);
        var critical = new TaskSignals
        {
            Now = now, Today = new DateOnly(2026, 10, 12), Status = WorkStatus.InProgress, Criticality = Criticality.Critical, IsOverdue = true, DaysOverdue = 3,
            NoResponseSince = now.AddDays(-5), MissedCommitments = 1, CustomerImpact = true, FinancialImpact = 2_000_000m,
        };
        var health = TaskScoring.Health(critical);
        Assert.Equal(TaskHealth.Critical, health.Health);
        Assert.Contains(health.Factors, f => f.Label == "No response for 5 days");

        var attention = TaskScoring.Attention(critical, health.Health);
        var calm = TaskScoring.Attention(critical with { Criticality = Criticality.Normal, IsOverdue = false, DaysOverdue = 0, NoResponseSince = null, MissedCommitments = 0, CustomerImpact = false, FinancialImpact = null }, TaskHealth.Healthy);
        Assert.True(attention.Score > 70);
        Assert.True(calm.Score < 15);
        Assert.Equal(0, TaskScoring.Attention(critical with { Status = WorkStatus.Completed }, TaskHealth.Healthy).Score);
    }

    private static readonly Guid Ceo = Guid.NewGuid(), Delegator = Guid.NewGuid(), Assignee = Guid.NewGuid(), Follower = Guid.NewGuid(),
        Leader = Guid.NewGuid(), Member = Guid.NewGuid(), Outsider = Guid.NewGuid(), Admin = Guid.NewGuid(), Team = Guid.NewGuid(), Dept = Guid.NewGuid();

    private static Viewer V(Guid id, AppRole roles, Guid[]? teams = null, Guid[]? led = null, Guid[]? depts = null) =>
        new(id, roles, (teams ?? []).Concat(led ?? []).ToHashSet(), (led ?? []).ToHashSet(), (depts ?? []).ToHashSet());

    [Theory]
    [InlineData(TaskVisibility.Normal, new[] { true, true, true, true, true, false, false, true })]
    [InlineData(TaskVisibility.TeamOnly, new[] { true, true, true, true, true, true, false, false })]
    [InlineData(TaskVisibility.ManagementOnly, new[] { true, true, true, false, true, false, false, false })]
    [InlineData(TaskVisibility.CeoOnly, new[] { true, true, true, false, false, false, false, false })]
    [InlineData(TaskVisibility.SelectedParticipants, new[] { true, true, true, true, false, false, false, false })]
    public void Visibility_matrix(TaskVisibility visibility, bool[] expected)
    {
        var facts = new TaskAccessFacts(visibility, Dept, [Team], [Delegator, Assignee], [Follower]);
        var viewers = new[]
        {
            V(Ceo, AppRole.Executive), V(Delegator, AppRole.SeniorManager), V(Assignee, AppRole.TeamMember), V(Follower, AppRole.TeamMember),
            V(Leader, AppRole.TeamLeader, led: [Team]), V(Member, AppRole.TeamMember, teams: [Team]), V(Outsider, AppRole.TeamMember), V(Admin, AppRole.Administrator),
        };
        Assert.Equal(expected, viewers.Select(v => TaskAccess.CanView(v, facts)).ToArray());
    }
}
