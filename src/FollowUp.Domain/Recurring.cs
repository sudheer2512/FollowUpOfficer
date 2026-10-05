using FollowUp.Domain.Rules;

namespace FollowUp.Domain;

/// <summary>
/// Permanent recurring task definition (spec §11–§14). Each occurrence generates a separate task
/// instance; previous occurrences are never overwritten. A series can be split ("this and future")
/// into a new definition that shares the SeriesId so occurrence history and uniqueness span the split.
/// </summary>
public sealed class RecurringTaskDefinition : Entity, IStamped
{
    public Guid SeriesId { get; set; }
    public string Code { get; set; } = "";
    /// <summary>Title template; tokens: {Date} {Month} {MonthShort} {Year} {PrevMonth} {PrevMonthYear} {Quarter} {FY} {Week}.</summary>
    public string TitleTemplate { get; set; } = "";
    public string? Description { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? PrimaryAssigneeId { get; set; }
    public List<Guid> AdditionalAssigneeIds { get; set; } = [];
    public Guid? TeamId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? TemplateId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public Criticality Criticality { get; set; } = Criticality.Normal;
    public TaskVisibility Visibility { get; set; } = TaskVisibility.Normal;

    public RecurrenceRule Rule { get; set; } = new();
    public DueRule Due { get; set; } = new();
    public FollowUpSchedule FollowUp { get; set; } = FollowUpSchedule.NoneSchedule;
    public Guid? EscalationPolicyId { get; set; }
    public HolidayHandling HolidayHandling { get; set; } = HolidayHandling.NextWorkingDay;
    public CatchUpPolicy CatchUp { get; set; } = CatchUpPolicy.GenerateAll;
    /// <summary>Window for <see cref="CatchUpPolicy.GenerateWithinWindow"/>, in days.</summary>
    public int CatchUpWindowDays { get; set; } = 3;
    public bool RequiresApproval { get; set; }
    public bool RequiresAcknowledgement { get; set; }
    /// <summary>Subtasks created with every occurrence.</summary>
    public List<string> Checklist { get; set; } = [];

    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? MaxOccurrences { get; set; }
    /// <summary>Definition split: this version is effective from this nominal date (inclusive).</summary>
    public DateOnly EffectiveFrom { get; set; }
    public Guid? PreviousVersionId { get; set; }

    public RecurrenceState State { get; set; } = RecurrenceState.Active;
    public DateTime? PausedAt { get; set; }
    public string? PauseReason { get; set; }
    public DateOnly? ResumeOn { get; set; }
    public DateTime? EndedAt { get; set; }

    // Durable progress checkpoint (spec §60 "Last Generated Recurring Occurrence")
    /// <summary>Last nominal occurrence date that was processed (created or recorded as skipped).</summary>
    public DateOnly? LastProcessedOccurrence { get; set; }
    public DateTime? NextOccurrenceAt { get; set; }
    public int GeneratedCount { get; set; }
    public int Version { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>One occurrence of a recurring definition — created, skipped or missed, always with a reason (spec §15).</summary>
public sealed class RecurringTaskInstance : Entity
{
    public Guid DefinitionId { get; set; }
    public Guid SeriesId { get; set; }
    /// <summary>The nominal occurrence date from the rule (before holiday adjustment). Unique per series.</summary>
    public DateOnly NominalDate { get; set; }
    /// <summary>Occurrence date after holiday handling.</summary>
    public DateOnly? OccurrenceDate { get; set; }
    public string OccurrenceKey { get; set; } = "";
    public DateTime ScheduledCreateAt { get; set; }
    public DateOnly? DueDate { get; set; }
    public OccurrenceOutcome Outcome { get; set; }
    public string? Reason { get; set; }
    public Guid? TaskId { get; set; }
    public DateTime ProcessedAt { get; set; }
    /// <summary>Minutes between the scheduled creation time and actual processing (downtime catch-up).</summary>
    public int LatenessMinutes { get; set; }
}
