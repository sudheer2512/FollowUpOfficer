using FollowUp.Domain.Rules;

namespace FollowUp.Domain;

/// <summary>Project / initiative grouping tasks (spec §46). Overall status is derived from its tasks.</summary>
public sealed class Project : Entity, IStamped
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? DepartmentId { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? TargetDate { get; set; }
    public TaskVisibility Visibility { get; set; } = TaskVisibility.Normal;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Meeting that produced decisions and action items (spec §47).</summary>
public sealed class Meeting : Entity, IStamped
{
    public string Title { get; set; } = "";
    public DateTime HeldAt { get; set; }
    public Guid? OrganizerId { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public Guid? ProjectId { get; set; }
    public string? ExternalEventId { get; set; }
    public List<Guid> ParticipantIds { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<MeetingActionItem> ActionItems { get; set; } = [];
}

public sealed class MeetingActionItem : Entity
{
    public Guid MeetingId { get; set; }
    public string? Decision { get; set; }
    public string ActionText { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public DateOnly? DueDate { get; set; }
    public Criticality Criticality { get; set; } = Criticality.Normal;
    public Guid? TaskId { get; set; }
    public MeetingActionStatus Status { get; set; } = MeetingActionStatus.Open;
}

/// <summary>Reusable task template (spec §51): standard tasks, subtasks, role placeholders, due/reminder/escalation rules and approval flow.</summary>
public sealed class TaskTemplate : Entity, IStamped
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Description { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public Criticality Criticality { get; set; } = Criticality.Normal;
    public bool RequiresApproval { get; set; }
    public Guid? EscalationPolicyId { get; set; }
    public FollowUpSchedule FollowUp { get; set; } = FollowUpSchedule.NoneSchedule;
    /// <summary>Role placeholders the user maps to people when instantiating ("Owner", "QA Lead").</summary>
    public List<string> Roles { get; set; } = [];
    public List<TemplateItem> Items { get; set; } = [];
    /// <summary>Completion approval flow, in order.</summary>
    public List<ApproverRole> ApprovalFlow { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed record TemplateItem
{
    public string Key { get; init; } = "";
    public string? ParentKey { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? AssigneeRole { get; init; }
    /// <summary>Due offset in working days from the anchor (start) date.</summary>
    public int DueOffsetWorkingDays { get; init; }
    public Criticality? Criticality { get; init; }
    public List<string> DependsOn { get; init; } = [];
    public FollowUpSchedule? FollowUp { get; init; }
}
