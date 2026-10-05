using FollowUp.Domain.Rules;

namespace FollowUp.Domain;

/// <summary>
/// A delegated task or subtask (spec §4–§8, §17). Subtasks are tasks with a parent (unlimited depth);
/// every task carries a human-readable number such as TASK-2026-00142.
/// Named WorkTask to avoid clashing with System.Threading.Tasks.Task.
/// </summary>
public sealed class WorkTask : Entity, IStamped
{
    // Identity
    public string Number { get; set; } = "";
    public int NumberYear { get; set; }
    public int NumberSequence { get; set; }

    // Hierarchy (spec §6)
    public Guid? ParentTaskId { get; set; }
    public WorkTask? ParentTask { get; set; }
    public Guid? RootTaskId { get; set; }
    public int Depth { get; set; }
    public int SortOrder { get; set; }

    // Definition (spec §4)
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Objective { get; set; }
    public string? ExpectedResult { get; set; }
    public string? Deliverable { get; set; }
    public string? SuccessCriteria { get; set; }
    /// <summary>Supporting links as JSON array of {title,url}.</summary>
    public List<TaskLink> Links { get; set; } = [];

    // Organisation / responsibility (spec §5, §43)
    public Guid? DepartmentId { get; set; }
    public Guid? TeamId { get; set; }
    /// <summary>Current primary responsible person.</summary>
    public Guid? PrimaryAssigneeId { get; set; }
    public Employee? PrimaryAssignee { get; set; }
    public Guid? AccountableManagerId { get; set; }
    /// <summary>Who delegated the task to the current responsible person.</summary>
    public Guid? DelegatorId { get; set; }
    /// <summary>Who first delegated the task — never changes, even when the task is re-delegated.</summary>
    public Guid? OriginalDelegatorId { get; set; }
    public Guid? CreatedById { get; set; }

    // Classification
    public Guid? ProjectId { get; set; }
    public Guid? CategoryId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public Criticality Criticality { get; set; } = Criticality.Normal;
    public WorkStatus Status { get; set; } = WorkStatus.Draft;
    public TaskVisibility Visibility { get; set; } = TaskVisibility.Normal;
    public TaskSource Source { get; set; } = TaskSource.Manual;
    public int ProgressPercent { get; set; }

    // Dates (spec §17) — business dates are local DateOnly; instants are UTC
    public DateTime? AssignedAt { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>Time of day the task is due (local). Null = end of the working day.</summary>
    public TimeOnly? DueTime { get; set; }
    public DateOnly? HardDeadline { get; set; }
    public DateOnly? ReviewDate { get; set; }
    /// <summary>The first committed due date — kept for analytics when extensions are granted (spec §44).</summary>
    public DateOnly? OriginalDueDate { get; set; }
    public int DueDateChangeCount { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    // Follow-up (spec §9, §10, §45)
    public FollowUpSchedule FollowUp { get; set; } = FollowUpSchedule.NoneSchedule;
    public DateTime? NextFollowUpAt { get; set; }
    public DateTime? LastFollowUpAt { get; set; }
    public DateOnly? FollowUpPausedUntil { get; set; }
    public FollowUpPauseReason? FollowUpPauseReason { get; set; }
    public string? FollowUpPauseNote { get; set; }
    public bool NotifyCeoOnDelay { get; set; }

    // Acknowledgement (spec §8)
    public AcknowledgementState Acknowledgement { get; set; } = AcknowledgementState.NotRequired;
    public DateTime? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedById { get; set; }
    public string? AcknowledgementComment { get; set; }

    // Completion & approval (spec §30, §31)
    public bool RequiresApproval { get; set; }
    public int CompletionRound { get; set; }

    // Impact (spec §37)
    public bool CustomerImpact { get; set; }
    public string? CustomerName { get; set; }
    public decimal? FinancialImpact { get; set; }

    // Monitoring state (spec §35–§37) — recalculated by the background processor
    public TaskHealth Health { get; set; } = TaskHealth.Healthy;
    public int AttentionScore { get; set; }
    /// <summary>Explanation of health and attention score.</summary>
    public List<ScoreFactor> AttentionFactors { get; set; } = [];
    public int EscalationLevel { get; set; }
    public int EscalationCount { get; set; }
    public bool IsBlocked { get; set; }
    public string? BlockedReason { get; set; }
    /// <summary>Last meaningful activity by anyone (comment, status change, email, Teams).</summary>
    public DateTime? LastActivityAt { get; set; }
    /// <summary>Last activity by the responsible people — drives no-response detection.</summary>
    public DateTime? LastAssigneeActivityAt { get; set; }
    public DateTime? LastReminderAt { get; set; }
    /// <summary>Number of reminders sent since the assignee last responded.</summary>
    public int RemindersSinceResponse { get; set; }
    /// <summary>Set when a reminder went unanswered; cleared on any assignee activity.</summary>
    public DateTime? NoResponseSince { get; set; }
    public string? LastUpdateSummary { get; set; }

    // Origin links
    public Guid? RecurringDefinitionId { get; set; }
    public Guid? RecurringInstanceId { get; set; }
    public Guid? MeetingActionItemId { get; set; }
    public Guid? TemplateId { get; set; }
    public Guid? EscalationPolicyId { get; set; }
    public Guid? SourceMessageId { get; set; }

    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<TaskAssignee> Assignees { get; set; } = [];
    public List<TaskFollower> Followers { get; set; } = [];
    public List<TaskTag> Tags { get; set; } = [];
    public List<WorkTask> Subtasks { get; set; } = [];

    public bool IsClosed => TaskStatusRules.IsClosed(Status);
    public bool IsOpen => !IsClosed;
}

public sealed record TaskLink(string Title, string Url);

public sealed record ScoreFactor(string Code, string Label, int Points);

/// <summary>Assignment of a person or a whole team to a task (spec §5).</summary>
public sealed class TaskAssignee : Entity
{
    public Guid TaskId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }
    public AssigneeRole Role { get; set; }
    public DateTime AssignedAt { get; set; }
    public Guid? AssignedById { get; set; }
    public DateTime? RemovedAt { get; set; }
}

public sealed class TaskFollower : Entity
{
    public Guid TaskId { get; set; }
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateTime AddedAt { get; set; }
}

public sealed class TaskCategory : Entity
{
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Tag : Entity
{
    public string Name { get; set; } = "";
}

public sealed class TaskTag
{
    public Guid TaskId { get; set; }
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
}

/// <summary>Task / subtask / external dependency (spec §16).</summary>
public sealed class TaskDependency : Entity
{
    public Guid TaskId { get; set; }
    public Guid? DependsOnTaskId { get; set; }
    public WorkTask? DependsOnTask { get; set; }
    public DependencyType Type { get; set; }
    /// <summary>For external dependencies: what we are waiting for and from whom.</summary>
    public string? ExternalDescription { get; set; }
    public string? ExternalParty { get; set; }
    public DateOnly? ExpectedBy { get; set; }
    public bool IsResolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedById { get; set; }
}

public sealed class TaskComment : Entity
{
    public Guid TaskId { get; set; }
    public Guid? AuthorId { get; set; }
    public Employee? Author { get; set; }
    public string Body { get; set; } = "";
    public bool IsSystem { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }
}

public sealed class TaskStatusHistory : Entity
{
    public Guid TaskId { get; set; }
    public WorkStatus? FromStatus { get; set; }
    public WorkStatus ToStatus { get; set; }
    public Guid? ChangedById { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? Reason { get; set; }
    public ChangeSource Source { get; set; }
}

/// <summary>Delegation chain entry (spec §43): CEO → CTO → Development Manager → Developer.</summary>
public sealed class TaskDelegation : Entity
{
    public Guid TaskId { get; set; }
    public int Sequence { get; set; }
    public Guid? FromEmployeeId { get; set; }
    public Guid ToEmployeeId { get; set; }
    public DateTime DelegatedAt { get; set; }
    public string? Note { get; set; }
}

public sealed class TaskAcknowledgement : Entity
{
    public Guid TaskId { get; set; }
    public Guid EmployeeId { get; set; }
    public AcknowledgementResponse Response { get; set; }
    public DateOnly? ProposedDueDate { get; set; }
    public string? Comment { get; set; }
    public DateTime RespondedAt { get; set; }
}

/// <summary>A promise made by a person about a task (spec §28). Separate from the official deadline.</summary>
public sealed class Commitment : Entity
{
    public Guid TaskId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    /// <summary>Who the commitment was made to (usually the delegator / CEO).</summary>
    public Guid? CommittedToId { get; set; }
    public DateOnly CommitmentDate { get; set; }
    public TimeOnly? CommitmentTime { get; set; }
    public string? What { get; set; }
    public string OriginalText { get; set; } = "";
    public CommunicationSource Source { get; set; }
    public Guid? SourceMessageId { get; set; }
    public Guid? SourceCommentId { get; set; }
    public double? Confidence { get; set; }
    public CommitmentStatus Status { get; set; } = CommitmentStatus.Open;
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTime? MissedNotifiedAt { get; set; }
}

/// <summary>Log of every follow-up point evaluation (spec §10) — the decision and why.</summary>
public sealed class TaskFollowUpLog : Entity
{
    public Guid TaskId { get; set; }
    public DateTime ScheduledAt { get; set; }
    public DateTime ProcessedAt { get; set; }
    public FollowUpAction Action { get; set; }
    public FollowUpKind Kind { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>Number of earlier follow-up points folded into this one after downtime (catch-up).</summary>
    public int CoalescedMissedPoints { get; set; }
    public int NotificationCount { get; set; }
}

/// <summary>Escalation fired for a task (spec §34). The idempotency key makes each ladder step fire once per cycle.</summary>
public sealed class TaskEscalation : Entity
{
    public Guid TaskId { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? StepId { get; set; }
    public int Level { get; set; }
    public EscalationTrigger Trigger { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public EscalationTarget Targets { get; set; }
    public string Reason { get; set; } = "";
    public DateTime TriggeredAt { get; set; }
    public Guid? TriggeredById { get; set; }
    public bool IsManual { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>A completion submission by the assignee (spec §30).</summary>
public sealed class CompletionSubmission : Entity
{
    public Guid TaskId { get; set; }
    public int Round { get; set; }
    public Guid SubmittedById { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateOnly CompletionDate { get; set; }
    public string? Note { get; set; }
    public ChangeSource Source { get; set; }
    public List<CompletionEvidence> Evidence { get; set; } = [];
}

/// <summary>Completion evidence (spec §32).</summary>
public sealed class CompletionEvidence : Entity
{
    public Guid TaskId { get; set; }
    public Guid? SubmissionId { get; set; }
    public EvidenceKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public string? Reference { get; set; }
    public Guid? AttachmentId { get; set; }
    public DateTime AddedAt { get; set; }
    public Guid? AddedById { get; set; }
}

/// <summary>Approval step for completion confirmation (spec §31).</summary>
public sealed class TaskApproval : Entity
{
    public Guid TaskId { get; set; }
    public int Round { get; set; }
    public int Stage { get; set; }
    public ApproverRole ApproverRole { get; set; }
    public Guid ApproverId { get; set; }
    public Employee? Approver { get; set; }
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public string? Comment { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}

public sealed class Attachment : Entity
{
    public Guid? TaskId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public Guid? UploadedById { get; set; }
    public DateTime UploadedAt { get; set; }
    public CommunicationSource Source { get; set; } = CommunicationSource.Manual;
    public bool IsEvidence { get; set; }
}

/// <summary>Deadline extension request (spec §44). The original deadline stays on the task for analytics.</summary>
public sealed class ExtensionRequest : Entity
{
    public Guid TaskId { get; set; }
    public Guid RequestedById { get; set; }
    public DateOnly? OriginalDueDate { get; set; }
    public DateOnly? CurrentDueDate { get; set; }
    public DateOnly ProposedDueDate { get; set; }
    public string Reason { get; set; } = "";
    public Guid? ApproverId { get; set; }
    public ExtensionStatus Status { get; set; } = ExtensionStatus.Pending;
    public string? DecisionNote { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedById { get; set; }
    public CommunicationSource Source { get; set; } = CommunicationSource.Manual;
}

/// <summary>AI-generated suggestion awaiting human confirmation (spec §27, §33).</summary>
public sealed class TaskSuggestion : Entity
{
    public Guid TaskId { get; set; }
    public Guid? MessageId { get; set; }
    public SuggestionKind Kind { get; set; }
    public WorkStatus? SuggestedStatus { get; set; }
    public DateOnly? SuggestedDate { get; set; }
    public string? Detail { get; set; }
    public double Confidence { get; set; }
    public string Explanation { get; set; } = "";
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedById { get; set; }
}
