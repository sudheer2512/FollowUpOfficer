using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.Core.Contracts;

// ───────────────────────────── Commands ─────────────────────────────

public sealed record DependencyInput(Guid? TaskId, DependencyType Type = DependencyType.FinishToStart, string? ExternalDescription = null,
    string? ExternalParty = null, DateOnly? ExpectedBy = null);

/// <summary>Fast task delegation (spec §4, §5).</summary>
public sealed record CreateTaskRequest
{
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Objective { get; init; }
    public string? ExpectedResult { get; init; }
    public string? Deliverable { get; init; }
    public string? SuccessCriteria { get; init; }
    public Guid? ParentTaskId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? PrimaryAssigneeId { get; init; }
    public List<Guid> AdditionalAssigneeIds { get; init; } = [];
    /// <summary>Whole teams assigned (spec §5 "one team / multiple teams").</summary>
    public List<Guid> TeamIds { get; init; } = [];
    public bool IncludeTeamLeader { get; init; }
    public List<Guid> SupportingMemberIds { get; init; } = [];
    public List<Guid> FollowerIds { get; init; } = [];
    public Guid? AccountableManagerId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? CategoryId { get; init; }
    public List<string> Tags { get; init; } = [];
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public Criticality Criticality { get; init; } = Criticality.Normal;
    public TaskVisibility Visibility { get; init; } = TaskVisibility.Normal;
    public DateOnly? StartDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public DateOnly? HardDeadline { get; init; }
    public DateOnly? ReviewDate { get; init; }
    public FollowUpSchedule? FollowUp { get; init; }
    public bool? RequiresApproval { get; init; }
    public bool? RequiresAcknowledgement { get; init; }
    public List<TaskLink> Links { get; init; } = [];
    public List<DependencyInput> Dependencies { get; init; } = [];
    public bool CustomerImpact { get; init; }
    public string? CustomerName { get; init; }
    public decimal? FinancialImpact { get; init; }
    public bool NotifyCeoOnDelay { get; init; }
    public Guid? EscalationPolicyId { get; init; }
    public bool IsDraft { get; init; }
    public TaskSource Source { get; init; } = TaskSource.Manual;
    public Guid? MeetingActionItemId { get; init; }
    public Guid? SourceMessageId { get; init; }
    public List<Guid> AttachmentIds { get; init; } = [];
}

/// <summary>Editable task fields; null = unchanged.</summary>
public sealed record UpdateTaskRequest
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Objective { get; init; }
    public string? ExpectedResult { get; init; }
    public string? Deliverable { get; init; }
    public string? SuccessCriteria { get; init; }
    public TaskPriority? Priority { get; init; }
    public Criticality? Criticality { get; init; }
    public TaskVisibility? Visibility { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? AccountableManagerId { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public bool ClearDueTime { get; init; }
    public DateOnly? HardDeadline { get; init; }
    public DateOnly? ReviewDate { get; init; }
    public FollowUpSchedule? FollowUp { get; init; }
    public bool? RequiresApproval { get; init; }
    public bool? CustomerImpact { get; init; }
    public string? CustomerName { get; init; }
    public decimal? FinancialImpact { get; init; }
    public bool? NotifyCeoOnDelay { get; init; }
    public Guid? EscalationPolicyId { get; init; }
    public List<string>? Tags { get; init; }
    public List<TaskLink>? Links { get; init; }
    public string? ChangeReason { get; init; }
}

public sealed record ReassignRequest(Guid NewPrimaryAssigneeId, string? Note, bool KeepPreviousAsSupporting = false);

public sealed record AssigneeChange(Guid? EmployeeId, Guid? TeamId, AssigneeRole Role);

public sealed record AcknowledgeRequest(AcknowledgementResponse Response, string? Comment, DateOnly? ProposedDueDate);

public sealed record StatusChangeRequest(WorkStatus Status, string? Reason, int? ProgressPercent = null);

public sealed record ProgressRequest(int ProgressPercent, string? Note);

public sealed record CommentRequest(string Body);

public sealed record EvidenceInput(EvidenceKind Kind, string Title, string? Url = null, string? Reference = null, Guid? AttachmentId = null);

/// <summary>Assignee marks work completed (spec §30).</summary>
public sealed record CompleteRequest(DateOnly? CompletionDate, string? Note, List<EvidenceInput>? Evidence, bool ForceWithOpenSubtasks = false);

public enum ApprovalAction { Confirm, Reopen, RequestMoreWork }

public sealed record ApprovalRequest(ApprovalAction Action, string? Comment);

public sealed record ExtensionRequestInput(DateOnly ProposedDueDate, string Reason);

public sealed record ExtensionDecisionRequest(bool Approve, string? Note, DateOnly? OverrideDate = null);

public sealed record SnoozeRequest(FollowUpPauseReason Reason, DateOnly ResumeOn, string? Note);

public sealed record ReminderRequest(bool Email = true, bool Teams = true, string? Message = null, bool RequestUpdate = true, bool IncludeTeamLeader = false);

public sealed record EscalateRequest(EscalationTarget Targets, string Note);

public sealed record LinkMessageRequest(Guid MessageId);

public sealed record CommitmentInput(Guid? EmployeeId, DateOnly CommitmentDate, string Text);

// ───────────────────────────── Queries ─────────────────────────────

/// <summary>Task list filter (spec §52, §53). Serialisable so it can be stored as a saved view.</summary>
public sealed record TaskFilter
{
    public string? Text { get; init; }
    public string? Scope { get; init; }               // all | mine | delegated | following | team
    public List<WorkStatus>? Statuses { get; init; }
    public bool? Open { get; init; }
    public Guid? AssignedToId { get; init; }
    public Guid? AssignedById { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? ProjectId { get; init; }
    public List<TaskPriority>? Priorities { get; init; }
    public List<Criticality>? Criticalities { get; init; }
    public Criticality? MinCriticality { get; init; }
    public List<TaskHealth>? Health { get; init; }
    public DeadlineBucket? Bucket { get; init; }
    public DateOnly? DueFrom { get; init; }
    public DateOnly? DueTo { get; init; }
    public int? MinDaysOverdue { get; init; }
    public bool? Recurring { get; init; }
    public bool? Overdue { get; init; }
    public bool? Blocked { get; init; }
    public bool? WaitingForCeo { get; init; }
    public bool? NoResponse { get; init; }
    public bool? AwaitingApproval { get; init; }
    public bool? TopLevelOnly { get; init; }
    public string? Tag { get; init; }
    public DateOnly? CompletedFrom { get; init; }
    public DateOnly? CompletedTo { get; init; }
    public string? Sort { get; init; }                // attention | due | updated | number | priority
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed record PersonRef(Guid Id, string Name, string? Title = null, string? Email = null);

public sealed record NamedRef(Guid Id, string Name);

public sealed record TaskListItem
{
    public Guid Id { get; init; }
    public string Number { get; init; } = "";
    public string Title { get; init; } = "";
    public WorkStatus Status { get; init; }
    public string DisplayStatus { get; init; } = "";
    public TaskPriority Priority { get; init; }
    public Criticality Criticality { get; init; }
    public TaskHealth Health { get; init; }
    public int AttentionScore { get; init; }
    public int ProgressPercent { get; init; }
    public DateOnly? DueDate { get; init; }
    public string DeadlineText { get; init; } = "";
    public DeadlineBucket Bucket { get; init; }
    public bool IsOverdue { get; init; }
    public int DaysOverdue { get; init; }
    public PersonRef? PrimaryAssignee { get; init; }
    public PersonRef? Delegator { get; init; }
    public string? TeamName { get; init; }
    public string? DepartmentName { get; init; }
    public string? ProjectName { get; init; }
    public Guid? ParentTaskId { get; init; }
    public string? ParentNumber { get; init; }
    public bool IsBlocked { get; init; }
    public string? BlockedReason { get; init; }
    public int NoResponseDays { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public DateTime? NextFollowUpAt { get; init; }
    public bool IsRecurring { get; init; }
    public int SubtaskCount { get; init; }
    public int OpenSubtaskCount { get; init; }
    public int EscalationLevel { get; init; }
    public TaskVisibility Visibility { get; init; }
    public DateOnly? OpenCommitmentDate { get; init; }
    public int MissedCommitments { get; init; }
    public string? LastUpdateSummary { get; init; }
    public DateTime? CompletedAt { get; init; }
    public List<string> Tags { get; init; } = [];
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record AssigneeDto(Guid Id, Guid? EmployeeId, string? EmployeeName, Guid? TeamId, string? TeamName, AssigneeRole Role, DateTime AssignedAt);

public sealed record DependencyDto(Guid Id, DependencyType Type, Guid? TaskId, string? Number, string? Title, WorkStatus? Status,
    string? ExternalDescription, string? ExternalParty, DateOnly? ExpectedBy, bool IsResolved, bool IsBlocking, string Direction);

public sealed record CommitmentDto(Guid Id, Guid TaskId, string TaskNumber, string TaskTitle, PersonRef? Person, PersonRef? CommittedTo,
    DateOnly CommitmentDate, string? What, string OriginalText, CommunicationSource Source, Guid? SourceMessageId, double? Confidence,
    CommitmentStatus Status, DateTime CreatedAt, DateTime? ResolvedAt);

public sealed record ApprovalDto(Guid Id, int Round, int Stage, ApproverRole Role, PersonRef Approver, ApprovalDecision Decision,
    string? Comment, DateTime RequestedAt, DateTime? DecidedAt, bool CanDecide);

public sealed record ExtensionDto(Guid Id, PersonRef RequestedBy, DateOnly? OriginalDueDate, DateOnly? CurrentDueDate, DateOnly ProposedDueDate,
    string Reason, PersonRef? Approver, ExtensionStatus Status, string? DecisionNote, DateTime RequestedAt, DateTime? DecidedAt,
    CommunicationSource Source, bool CanDecide);

public sealed record SuggestionDto(Guid Id, SuggestionKind Kind, WorkStatus? SuggestedStatus, DateOnly? SuggestedDate, string? Detail,
    double Confidence, string Explanation, SuggestionStatus Status, Guid? MessageId, DateTime CreatedAt);

public sealed record DelegationDto(int Sequence, PersonRef? From, PersonRef To, DateTime DelegatedAt, string? Note);

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAt, PersonRef? UploadedBy, bool IsEvidence);

public sealed record EvidenceDto(Guid Id, EvidenceKind Kind, string Title, string? Url, string? Reference, Guid? AttachmentId, DateTime AddedAt);

public sealed record SubmissionDto(Guid Id, int Round, PersonRef SubmittedBy, DateTime SubmittedAt, DateOnly CompletionDate, string? Note,
    ChangeSource Source, List<EvidenceDto> Evidence);

public sealed record EscalationDto(Guid Id, int Level, EscalationTrigger Trigger, EscalationTarget Targets, string Reason, DateTime TriggeredAt, bool IsManual);

public sealed record TimelineItem(DateTime At, string Kind, string Title, string? Body, PersonRef? Actor, string? Channel, Guid? RefId,
    Dictionary<string, string>? Meta = null);

public sealed record LinkedMessageDto(Guid LinkId, Guid MessageId, CommunicationChannel Channel, string? Subject, string Preview, string? SenderName,
    DateTime SentAt, MatchMethod Method, double Confidence, MessageCategory Category, string Explanation, LinkStatus Status, string? WebUrl);

public sealed record TaskPermissions(bool CanView, bool CanManage, bool CanUpdate, bool CanComplete, bool CanAcknowledge, bool CanApprove,
    bool CanComment, bool CanDecideExtension);

public sealed record FollowUpInfo(FollowUpSchedule Schedule, string Description, DateTime? NextAt, DateTime? LastAt, DateOnly? PausedUntil,
    FollowUpPauseReason? PauseReason, string? PauseNote, int RemindersSinceResponse, DateTime? LastReminderAt, DateTime? NoResponseSince);

public sealed record SubtaskNode(Guid Id, string Number, string Title, WorkStatus Status, string DisplayStatus, PersonRef? Assignee, DateOnly? DueDate,
    string DeadlineText, int ProgressPercent, TaskHealth Health, int Depth, Guid? ParentTaskId, bool IsBlocked, List<SubtaskNode> Children);

/// <summary>Full task view (detail page).</summary>
public sealed record TaskDetail
{
    public required TaskListItem Summary { get; init; }
    public string? Description { get; init; }
    public string? Objective { get; init; }
    public string? ExpectedResult { get; init; }
    public string? Deliverable { get; init; }
    public string? SuccessCriteria { get; init; }
    public List<TaskLink> Links { get; init; } = [];
    public PersonRef? OriginalDelegator { get; init; }
    public PersonRef? AccountableManager { get; init; }
    public PersonRef? CreatedBy { get; init; }
    public NamedRef? Project { get; init; }
    public NamedRef? Category { get; init; }
    public NamedRef? Team { get; init; }
    public NamedRef? Department { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? CategoryId { get; init; }
    public DateTime? AssignedAt { get; init; }
    public DateOnly? StartDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public DateOnly? HardDeadline { get; init; }
    public DateOnly? ReviewDate { get; init; }
    public DateOnly? OriginalDueDate { get; init; }
    public int DueDateChangeCount { get; init; }
    public DateTime? DeadlineAt { get; init; }
    public AcknowledgementState Acknowledgement { get; init; }
    public DateTime? AcknowledgedAt { get; init; }
    public string? AcknowledgementComment { get; init; }
    public bool RequiresApproval { get; init; }
    public bool CustomerImpact { get; init; }
    public string? CustomerName { get; init; }
    public decimal? FinancialImpact { get; init; }
    public bool NotifyCeoOnDelay { get; init; }
    public TaskSource Source { get; init; }
    public List<ScoreFactor> AttentionFactors { get; init; } = [];
    public FollowUpInfo FollowUp { get; init; } = null!;
    public List<AssigneeDto> Assignees { get; init; } = [];
    public List<PersonRef> Followers { get; init; } = [];
    public List<SubtaskNode> Subtasks { get; init; } = [];
    public List<DependencyDto> Dependencies { get; init; } = [];
    public List<CommitmentDto> Commitments { get; init; } = [];
    public List<ApprovalDto> Approvals { get; init; } = [];
    public List<ExtensionDto> Extensions { get; init; } = [];
    public List<SuggestionDto> Suggestions { get; init; } = [];
    public List<DelegationDto> DelegationChain { get; init; } = [];
    public List<AttachmentDto> Attachments { get; init; } = [];
    public List<SubmissionDto> Submissions { get; init; } = [];
    public List<EscalationDto> Escalations { get; init; } = [];
    public List<LinkedMessageDto> Messages { get; init; } = [];
    public List<TimelineItem> Timeline { get; init; } = [];
    public TaskPermissions Permissions { get; init; } = null!;
    public Guid? RecurringDefinitionId { get; init; }
    public string? RecurringTitle { get; init; }
    public string? OccurrenceKey { get; init; }
    public Guid? MeetingId { get; init; }
    public string? MeetingTitle { get; init; }
    public TaskListItem? Parent { get; init; }
}

/// <summary>Workload of a person before delegation (spec §42).</summary>
public sealed record WorkloadDto(Guid EmployeeId, string Name, string? JobTitle, int OpenTasks, int DueThisWeek, int Overdue, int Critical,
    int Blocked, int NoResponse, string Load);
