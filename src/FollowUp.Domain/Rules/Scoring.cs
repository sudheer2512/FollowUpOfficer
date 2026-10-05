namespace FollowUp.Domain.Rules;

/// <summary>Monitoring signals for one task, gathered by the background processor.</summary>
public sealed record TaskSignals
{
    public DateTime Now { get; init; }
    public DateOnly Today { get; init; }
    public WorkStatus Status { get; init; }
    public Criticality Criticality { get; init; }
    public TaskPriority Priority { get; init; }
    public int ProgressPercent { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public bool IsOverdue { get; init; }
    public int DaysOverdue { get; init; }
    public int? DaysUntilDue { get; init; }
    public int SeverelyOverdueDays { get; init; } = 7;
    public DateTime? LastActivityAt { get; init; }
    public DateTime? NoResponseSince { get; init; }
    public int RemindersSinceResponse { get; init; }
    public int MissedCommitments { get; init; }
    public bool IsBlocked { get; init; }
    public int BlockedDays { get; init; }
    public int PendingDependencies { get; init; }
    /// <summary>Open tasks that depend on this one (dependency impact).</summary>
    public int DependentTasks { get; init; }
    public int EscalationLevel { get; init; }
    public int EscalationCount { get; init; }
    public bool CustomerImpact { get; init; }
    public decimal? FinancialImpact { get; init; }
    public bool AwaitingCeoDecision { get; init; }
    public AcknowledgementState Acknowledgement { get; init; }
    public double AcknowledgementPendingHours { get; init; }
}

public sealed record HealthResult(TaskHealth Health, int RiskPoints, IReadOnlyList<ScoreFactor> Factors);

public sealed record AttentionResult(int Score, IReadOnlyList<ScoreFactor> Factors);

/// <summary>
/// Task health (spec §36) and executive Attention Score (spec §37). Both return their contributing factors
/// so every number on the CEO workbench can be explained.
/// </summary>
public static class TaskScoring
{
    public static int NoResponseDays(TaskSignals s) =>
        s.NoResponseSince is { } since ? Math.Max(0, (int)Math.Floor((s.Now - since).TotalDays)) : 0;

    public static HealthResult Health(TaskSignals s)
    {
        if (TaskStatusRules.IsClosed(s.Status) || s.Status == WorkStatus.Deferred)
            return new(TaskHealth.Healthy, 0, []);

        var f = new List<ScoreFactor>();
        if (s.IsOverdue)
        {
            f.Add(new("overdue", $"Overdue by {Text.Days(s.DaysOverdue)}", 25 + Math.Min(s.DaysOverdue * 4, 25)));
            if (s.DaysOverdue >= s.SeverelyOverdueDays) f.Add(new("severely-overdue", "Severely overdue", 10));
        }
        else if (s.DaysUntilDue is { } dtd)
        {
            if (dtd <= 2 && s.ProgressPercent < 50) f.Add(new("due-soon-low-progress", $"Due in {Text.Days(dtd)} with {s.ProgressPercent}% done", 15));
            else if (dtd <= 1 && s.ProgressPercent < 80) f.Add(new("due-soon", $"Due {(dtd == 0 ? "today" : "tomorrow")} with {s.ProgressPercent}% done", 10));

            if (s.StartDate is { } start && s.DueDate is { } due && due > start && s.ProgressPercent > 0)
            {
                var total = due.DayNumber - start.DayNumber;
                var elapsed = Math.Clamp(s.Today.DayNumber - start.DayNumber, 0, total);
                var expected = (int)Math.Round(100.0 * elapsed / total);
                var gap = expected - s.ProgressPercent;
                if (gap > 40) f.Add(new("behind-schedule", $"Behind schedule ({s.ProgressPercent}% vs {expected}% expected)", 15));
                else if (gap > 25) f.Add(new("behind-schedule", $"Slightly behind schedule ({s.ProgressPercent}% vs {expected}% expected)", 8));
            }
        }

        var noResponse = NoResponseDays(s);
        if (noResponse >= 5) f.Add(new("no-response", $"No response for {Text.Days(noResponse)}", 30));
        else if (noResponse >= 3) f.Add(new("no-response", $"No response for {Text.Days(noResponse)}", 20));
        else if (noResponse >= 1) f.Add(new("no-response", $"No response for {Text.Days(noResponse)}", 8));

        if (s.MissedCommitments > 0) f.Add(new("missed-commitment", $"{s.MissedCommitments} missed commitment(s)", Math.Min(15 * s.MissedCommitments, 30)));
        if (s.IsBlocked || s.Status == WorkStatus.Blocked) f.Add(new("blocked", s.BlockedDays >= 3 ? $"Blocked for {Text.Days(s.BlockedDays)}" : "Blocked", s.BlockedDays >= 3 ? 25 : 15));
        if (s.PendingDependencies > 0) f.Add(new("dependencies", $"{s.PendingDependencies} pending dependenc{(s.PendingDependencies == 1 ? "y" : "ies")}", Math.Min(5 * s.PendingDependencies, 10)));

        if (s.LastActivityAt is { } last)
        {
            var idle = (s.Now - last).TotalDays;
            if (idle >= 14) f.Add(new("stale", $"No update for {(int)idle} days", 15));
            else if (idle >= 7) f.Add(new("stale", $"No update for {(int)idle} days", 10));
        }

        if (s.Acknowledgement == AcknowledgementState.Pending && s.AcknowledgementPendingHours >= 24)
            f.Add(new("unacknowledged", $"Not acknowledged for {(int)(s.AcknowledgementPendingHours / 24)} day(s)", 10));
        if (s.Acknowledgement == AcknowledgementState.Declined) f.Add(new("declined", "Assignee could not accept the task", 20));
        if (s.EscalationCount > 0) f.Add(new("escalations", $"{s.EscalationCount} escalation(s)", Math.Min(5 * s.EscalationCount, 15)));

        var raw = f.Sum(x => x.Points);
        var multiplier = s.Criticality switch
        {
            Criticality.Informational => 0.7,
            Criticality.Critical => 1.2,
            Criticality.BusinessCritical => 1.3,
            _ => 1.0,
        };
        var points = (int)Math.Round(raw * multiplier);
        var health = points switch
        {
            < 15 => TaskHealth.Healthy,
            < 35 => TaskHealth.Watch,
            < 60 => TaskHealth.AtRisk,
            _ => TaskHealth.Critical,
        };
        if (s.IsOverdue && health < TaskHealth.AtRisk) health = TaskHealth.AtRisk;
        if (s.IsOverdue && s.Criticality >= Criticality.Critical) health = TaskHealth.Critical;
        return new(health, points, f);
    }

    public static AttentionResult Attention(TaskSignals s, TaskHealth health)
    {
        if (TaskStatusRules.IsClosed(s.Status) || s.Status == WorkStatus.Deferred) return new(0, []);
        var f = new List<ScoreFactor>();

        var crit = s.Criticality switch
        {
            Criticality.Informational => 0,
            Criticality.Normal => 6,
            Criticality.Important => 14,
            Criticality.Critical => 24,
            _ => 32,
        };
        if (crit > 0) f.Add(new("criticality", Text.Humanize(s.Criticality), crit));
        if (s.Priority >= TaskPriority.High) f.Add(new("priority", Text.Humanize(s.Priority) + " priority", s.Priority == TaskPriority.Urgent ? 4 : 2));
        if (s.IsOverdue) f.Add(new("overdue", $"Overdue by {Text.Days(s.DaysOverdue)}", Math.Min(6 + s.DaysOverdue * 2, 20)));
        if (s.MissedCommitments > 0) f.Add(new("missed-commitment", $"{s.MissedCommitments} missed commitment(s)", Math.Min(10 + 4 * (s.MissedCommitments - 1), 16)));
        var noResponse = NoResponseDays(s);
        if (noResponse > 0) f.Add(new("no-response", $"No response for {Text.Days(noResponse)}", Math.Min(noResponse * 3, 15)));
        if (s.CustomerImpact) f.Add(new("customer-impact", "Customer impact", 8));
        if (s.DependentTasks > 0) f.Add(new("dependency-impact", $"Blocks {s.DependentTasks} other task(s)", Math.Min(s.DependentTasks * 3, 9)));
        if (s.EscalationLevel > 0) f.Add(new("escalation-level", $"Escalation level {s.EscalationLevel}", Math.Min(s.EscalationLevel * 2, 8)));
        if (s.FinancialImpact is { } money && money > 0)
        {
            var pts = money >= 1_000_000m ? 10 : money >= 100_000m ? 6 : money >= 10_000m ? 3 : 1;
            f.Add(new("financial-impact", $"Financial impact {money:N0}", pts));
        }
        if (s.IsBlocked || s.Status == WorkStatus.Blocked) f.Add(new("blocked", "Blocked", 6));
        if (s.AwaitingCeoDecision) f.Add(new("awaiting-ceo", "Awaiting your decision", 12));
        if (health == TaskHealth.Critical) f.Add(new("health", "Health critical", 6));
        else if (health == TaskHealth.AtRisk) f.Add(new("health", "Health at risk", 3));

        return new(Math.Min(100, f.Sum(x => x.Points)), f);
    }
}
