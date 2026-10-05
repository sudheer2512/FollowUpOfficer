using System.Globalization;

namespace FollowUp.Domain.Rules;

/// <summary>State of a task relevant to escalation, gathered by the escalation processor.</summary>
public sealed record EscalationSnapshot
{
    public Guid TaskId { get; init; }
    public DateTime Now { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public bool IsClosed { get; init; }
    public bool IsFollowUpPaused { get; init; }
    public Criticality Criticality { get; init; }
    public DateTime? AssignedAt { get; init; }
    public AcknowledgementState Acknowledgement { get; init; }
    public DateTime? NoResponseSince { get; init; }
    public bool IsBlocked { get; init; }
    public DateTime? BlockedSince { get; init; }
    public IReadOnlyList<MissedCommitmentRef> MissedCommitments { get; init; } = [];
}

public sealed record MissedCommitmentRef(Guid CommitmentId, DateOnly CommitmentDate, DateTime MissedAt);

/// <summary>An escalation step that is due. Superseded steps are recorded (for idempotency) but not notified.</summary>
public sealed record EscalationDue(EscalationStep Step, string IdempotencyKey, string Reason, DateTime DueAt, bool Superseded);

/// <summary>
/// Escalation ladder evaluation (spec §34). Each step fires at most once per "cycle" — the idempotency key
/// includes the due date / no-response episode / commitment, so changing the deadline restarts the ladder
/// and a restart never fires the same step twice. When several steps of the same trigger are due at once
/// (e.g. after downtime) only the highest fires; the lower ones are recorded as superseded (no flood).
/// </summary>
public static class EscalationEvaluator
{
    public static IReadOnlyList<EscalationDue> Evaluate(EscalationSnapshot s, IEnumerable<EscalationStep> steps, BusinessCalendar cal)
    {
        if (s.IsClosed) return [];
        var due = new List<EscalationDue>();

        foreach (var step in steps.OrderBy(x => x.Order))
        {
            // Pausing follow-up (legitimately waiting) suspends deadline / no-response escalation,
            // except for business-critical work.
            if (s.IsFollowUpPaused && s.Criticality < Criticality.BusinessCritical
                && step.Trigger is EscalationTrigger.BeforeDue or EscalationTrigger.OnDue or EscalationTrigger.Overdue or EscalationTrigger.NoResponse)
                continue;

            switch (step.Trigger)
            {
                case EscalationTrigger.BeforeDue or EscalationTrigger.OnDue or EscalationTrigger.Overdue when s.DueDate is { } dueDate:
                {
                    var deadline = cal.DeadlineUtc(dueDate, s.DueTime);
                    var at = step.Trigger switch
                    {
                        EscalationTrigger.BeforeDue => Shift(deadline, -step.Threshold, step.Unit, cal),
                        EscalationTrigger.OnDue => cal.ToUtc(dueDate, cal.WorkdayStart),
                        _ => Shift(deadline, step.Threshold, step.Unit, cal),
                    };
                    if (s.Now >= at)
                    {
                        var cycle = dueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                        var reason = step.Trigger switch
                        {
                            EscalationTrigger.BeforeDue => $"Due {dueDate:d MMM} — {Describe(step.Threshold, step.Unit)} reminder",
                            EscalationTrigger.OnDue => $"Due today ({dueDate:d MMM})",
                            _ => $"Overdue by {Describe(step.Threshold, step.Unit)} or more (due {dueDate:d MMM})",
                        };
                        due.Add(new(step, Key(s.TaskId, step, cycle), reason, at, false));
                    }
                    break;
                }
                case EscalationTrigger.NoResponse when s.NoResponseSince is { } since:
                {
                    var at = Shift(since, step.Threshold, step.Unit, cal);
                    if (s.Now >= at)
                        due.Add(new(step, Key(s.TaskId, step, since.Ticks.ToString(CultureInfo.InvariantCulture)),
                            $"No response for {Describe(step.Threshold, step.Unit)} after a reminder", at, false));
                    break;
                }
                case EscalationTrigger.AcknowledgementPending when s.Acknowledgement == AcknowledgementState.Pending && s.AssignedAt is { } assigned:
                {
                    var at = Shift(assigned, step.Threshold, step.Unit, cal);
                    if (s.Now >= at)
                        due.Add(new(step, Key(s.TaskId, step, assigned.Ticks.ToString(CultureInfo.InvariantCulture)),
                            $"Not acknowledged within {Describe(step.Threshold, step.Unit)}", at, false));
                    break;
                }
                case EscalationTrigger.CommitmentMissed:
                    foreach (var c in s.MissedCommitments)
                    {
                        var at = Shift(c.MissedAt, step.Threshold, step.Unit, cal);
                        if (s.Now >= at)
                            due.Add(new(step, Key(s.TaskId, step, c.CommitmentId.ToString("N")),
                                $"Commitment for {c.CommitmentDate:d MMM} was missed", at, false));
                    }
                    break;
                case EscalationTrigger.Blocked when s.IsBlocked && s.BlockedSince is { } blockedSince:
                {
                    var at = Shift(blockedSince, step.Threshold, step.Unit, cal);
                    if (s.Now >= at)
                        due.Add(new(step, Key(s.TaskId, step, blockedSince.Ticks.ToString(CultureInfo.InvariantCulture)),
                            $"Blocked for {Describe(step.Threshold, step.Unit)}", at, false));
                    break;
                }
            }
        }

        // Within one trigger family and cycle, only the highest step fires; lower ones are superseded.
        static string Family(EscalationTrigger t) => t is EscalationTrigger.BeforeDue or EscalationTrigger.OnDue or EscalationTrigger.Overdue ? "deadline" : t.ToString();
        var result = new List<EscalationDue>();
        foreach (var group in due.GroupBy(d => Family(d.Step.Trigger) + "|" + CycleOf(d.IdempotencyKey)))
        {
            var ordered = group.OrderBy(d => d.DueAt).ThenBy(d => d.Step.Order).ToList();
            for (var i = 0; i < ordered.Count; i++)
                result.Add(i == ordered.Count - 1 ? ordered[i] : ordered[i] with { Superseded = true });
        }
        return result;
    }

    private static string CycleOf(string key) => key[(key.LastIndexOf(':') + 1)..];

    public static string Key(Guid taskId, EscalationStep step, string cycle) => $"esc:{taskId:N}:{step.Id:N}:{cycle}";

    public static DateTime Shift(DateTime fromUtc, double amount, ThresholdUnit unit, BusinessCalendar cal) => unit switch
    {
        ThresholdUnit.Hours => fromUtc.AddHours(amount),
        ThresholdUnit.WorkingDays => ShiftWorkingDays(fromUtc, (int)Math.Round(amount), cal),
        _ => fromUtc.AddDays(amount),
    };

    private static DateTime ShiftWorkingDays(DateTime fromUtc, int days, BusinessCalendar cal)
    {
        var local = cal.ToLocal(fromUtc);
        var date = cal.AddWorkingDays(DateOnly.FromDateTime(local), days);
        return cal.ToUtc(date, TimeOnly.FromDateTime(local));
    }

    public static string Describe(double amount, ThresholdUnit unit)
    {
        var n = amount % 1 == 0 ? ((int)amount).ToString(CultureInfo.InvariantCulture) : amount.ToString("0.#", CultureInfo.InvariantCulture);
        return unit switch
        {
            ThresholdUnit.Hours => $"{n} hour{(amount == 1 ? "" : "s")}",
            ThresholdUnit.WorkingDays => $"{n} working day{(amount == 1 ? "" : "s")}",
            _ => $"{n} day{(amount == 1 ? "" : "s")}",
        };
    }
}
