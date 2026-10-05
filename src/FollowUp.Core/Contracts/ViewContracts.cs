using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.Core.Contracts;

/// <summary>Where a number or sentence drills down to in the UI: a route plus query parameters (spec §56 "drilldown").</summary>
public sealed record Drilldown(string Route, Dictionary<string, string> Query)
{
    public static Drilldown Tasks(params (string Key, string Value)[] q) => new("/tasks", q.ToDictionary(x => x.Key, x => x.Value));
    public static Drilldown To(string route, params (string Key, string Value)[] q) => new(route, q.ToDictionary(x => x.Key, x => x.Value));
}

public sealed record Tile(string Key, string Label, int Value, string Tone, Drilldown Drilldown, string? Hint = null);

// ───────────────────────────── Attention ─────────────────────────────

public sealed record AttentionItemDto(Guid Id, AttentionType Type, string TypeLabel, string Title, string Detail, int Score,
    AttentionStatus Status, DateTime CreatedAt, DateTime UpdatedAt, DateTime? SnoozedUntil, TaskListItem? Task,
    Guid? CommitmentId, Guid? ExtensionRequestId, Guid? ApprovalId, Guid? MessageId, Guid? SuggestionId, List<string> Actions);

public sealed record AttentionDecision(string Action, string? Note, DateTime? SnoozeUntil);

// ───────────────────────────── Dashboards ─────────────────────────────

public sealed record WorkbenchSection(string Key, string Title, string Description, int Count, Drilldown Drilldown, List<TaskListItem> Tasks,
    List<AttentionItemDto>? Attention = null, List<CommitmentDto>? Commitments = null, List<ApprovalQueueItem>? Approvals = null);

public sealed record ApprovalQueueItem(string Kind, Guid Id, TaskListItem Task, string Title, string Detail, DateTime RequestedAt);

/// <summary>CEO workbench (spec §38, §79).</summary>
public sealed record CeoWorkbench(DateTime GeneratedAt, string Greeting, List<Tile> Tiles, List<WorkbenchSection> Sections, ExecutiveSummary Summary);

public sealed record MemberLoad(PersonRef Person, int Open, int DueThisWeek, int Overdue, int Critical, int Blocked, int NoRecentUpdate, int Completed);

/// <summary>Team dashboard (spec §40).</summary>
public sealed record TeamDashboard(NamedRef Team, PersonRef? Leader, List<Tile> Tiles, List<MemberLoad> Members, List<TaskListItem> AttentionTasks,
    List<TaskListItem> Overdue, List<TaskListItem> Blocked, List<TaskListItem> NoRecentUpdate, List<RecurringDefinitionDto> Recurring);

/// <summary>Individual dashboard (spec §41).</summary>
public sealed record MyDashboard(PersonRef Me, List<Tile> Tiles, List<WorkbenchSection> Sections);

public sealed record ProjectSummary(Guid Id, string Code, string Name, string? Description, PersonRef? Owner, NamedRef? Department,
    DateOnly? StartDate, DateOnly? TargetDate, string Status, int TotalTasks, int OpenTasks, int CompletedTasks, int OverdueTasks,
    int BlockedTasks, int CriticalTasks, int PercentComplete, TaskHealth WorstHealth, TaskListItem? NextDeadline, bool IsActive);

public sealed record ProjectDetail(ProjectSummary Summary, List<TaskListItem> Tasks, List<TaskListItem> Blockers, List<CommitmentDto> Commitments);

// ───────────────────────────── Executive summary, digest, review ─────────────────────────────

public sealed record SummaryPoint(string Text, int? Count, string Tone, Drilldown? Drilldown);

/// <summary>AI executive summary (spec §56): deterministic, drill-down-able points plus an optional narrative.</summary>
public sealed record ExecutiveSummary(string Headline, List<SummaryPoint> Points, string? Narrative, string Provider, DateTime GeneratedAt);

public sealed record DigestDto(DateTime AsOf, int Open, int Overdue, int Critical, int NeedAttention, int DueToday, int MissedCommitments,
    int CompletedYesterday, List<AttentionItemDto> TopAttention, ExecutiveSummary Summary);

public sealed record ReviewSection(string Key, string Title, List<TaskListItem> Tasks, List<CommitmentDto>? Commitments = null,
    List<RecurringInstanceDto>? Instances = null, List<string>? Lines = null);

/// <summary>Weekly executive review (spec §55).</summary>
public sealed record WeeklyReviewDto(DateOnly WeekStart, DateOnly WeekEnd, ExecutiveSummary Summary, List<ReviewSection> Sections,
    List<TeamStatusRow> Teams);

public sealed record TeamStatusRow(Guid TeamId, string Team, int Open, int Overdue, int Completed, int Blocked, int OnTimePercent, TaskHealth Health);

// ───────────────────────────── Natural language ─────────────────────────────

public sealed record ParseCommandRequest(string Text, bool Voice = false);

public sealed record ResolvedDraft(TaskDraft Draft, PersonRef? PrimaryAssignee, List<PersonRef> Assignees, List<NamedRef> Teams,
    NamedRef? Department, NamedRef? Project, string? FollowUpText, string? RecurrenceText, string? DueRuleText,
    List<WorkloadDto> Workload, string Provider);

public sealed record AskRequest(string Question);

public sealed record AskAnswer(string Question, QuestionQuery Query, string Interpretation, string Answer, List<TaskListItem> Tasks,
    List<CommitmentDto> Commitments, List<RecurringInstanceDto> Instances, List<AttentionItemDto> Attention, Drilldown? Drilldown, string Provider);

// ───────────────────────────── Recurring ─────────────────────────────

public sealed record RecurringDefinitionDto
{
    public Guid Id { get; init; }
    public Guid SeriesId { get; init; }
    public string Code { get; init; } = "";
    public string TitleTemplate { get; init; } = "";
    public string? Description { get; init; }
    public PersonRef? Owner { get; init; }
    public PersonRef? PrimaryAssignee { get; init; }
    public List<PersonRef> AdditionalAssignees { get; init; } = [];
    public NamedRef? Team { get; init; }
    public NamedRef? Department { get; init; }
    public NamedRef? Project { get; init; }
    public TaskPriority Priority { get; init; }
    public Criticality Criticality { get; init; }
    public TaskVisibility Visibility { get; init; }
    public RecurrenceRule Rule { get; init; } = new();
    public string RuleText { get; init; } = "";
    public DueRule Due { get; init; } = new();
    public string DueText { get; init; } = "";
    public FollowUpSchedule FollowUp { get; init; } = FollowUpSchedule.NoneSchedule;
    public string FollowUpText { get; init; } = "";
    public Guid? EscalationPolicyId { get; init; }
    public HolidayHandling HolidayHandling { get; init; }
    public CatchUpPolicy CatchUp { get; init; }
    public int CatchUpWindowDays { get; init; }
    public bool RequiresApproval { get; init; }
    public bool RequiresAcknowledgement { get; init; }
    public List<string> Checklist { get; init; } = [];
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public int? MaxOccurrences { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public RecurrenceState State { get; init; }
    public string? PauseReason { get; init; }
    public DateOnly? ResumeOn { get; init; }
    public DateOnly? LastProcessedOccurrence { get; init; }
    public DateTime? NextOccurrenceAt { get; init; }
    public int GeneratedCount { get; init; }
    public int MissedCount { get; init; }
    public int OpenInstances { get; init; }
    public int CompletedOnTime { get; init; }
    public int CompletedLate { get; init; }
    public int CompliancePercent { get; init; }
}

public sealed record RecurringDefinitionInput
{
    public string? Code { get; init; }
    public string TitleTemplate { get; init; } = "";
    public string? Description { get; init; }
    public Guid? OwnerId { get; init; }
    public Guid? PrimaryAssigneeId { get; init; }
    public List<Guid> AdditionalAssigneeIds { get; init; } = [];
    public Guid? TeamId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? CategoryId { get; init; }
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;
    public Criticality Criticality { get; init; } = Criticality.Normal;
    public TaskVisibility Visibility { get; init; } = TaskVisibility.Normal;
    public RecurrenceRule Rule { get; init; } = new();
    public DueRule Due { get; init; } = new();
    public FollowUpSchedule? FollowUp { get; init; }
    public Guid? EscalationPolicyId { get; init; }
    public HolidayHandling HolidayHandling { get; init; } = HolidayHandling.NextWorkingDay;
    public CatchUpPolicy CatchUp { get; init; } = CatchUpPolicy.GenerateAll;
    public int CatchUpWindowDays { get; init; } = 3;
    public bool RequiresApproval { get; init; }
    public bool RequiresAcknowledgement { get; init; }
    public List<string> Checklist { get; init; } = [];
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public int? MaxOccurrences { get; init; }
}

public sealed record RecurringUpdateRequest(RecurringDefinitionInput Definition, ChangeScope Scope, Guid? FromInstanceId, DateOnly? FromDate);

public sealed record RecurringPauseRequest(string? Reason, DateOnly? ResumeOn);

public sealed record SkipOccurrenceRequest(DateOnly? NominalDate, Guid? InstanceId, string? Reason);

public sealed record OccurrencePreview(DateOnly NominalDate, DateOnly? Date, DateTime CreateAt, DateOnly? DueDate, TimeOnly? DueTime,
    string Key, string Title, string? Note);

public sealed record PreviewRequest(RecurrenceRule Rule, DueRule Due, HolidayHandling HolidayHandling, DateOnly? StartDate, DateOnly? EndDate,
    string? TitleTemplate, int Count = 8);

public sealed record RecurringInstanceDto(Guid Id, Guid DefinitionId, string DefinitionTitle, DateOnly NominalDate, DateOnly? OccurrenceDate,
    string OccurrenceKey, DateTime ScheduledCreateAt, DateOnly? DueDate, OccurrenceOutcome Outcome, string? Reason, Guid? TaskId,
    string? TaskNumber, WorkStatus? TaskStatus, DateTime ProcessedAt, int LatenessMinutes, Criticality Criticality);

// ───────────────────────────── Communications review ─────────────────────────────

public sealed record MessageDto(Guid Id, CommunicationChannel Channel, string? Subject, string Body, string? SenderName, string? SenderAddress,
    PersonRef? Sender, List<MessageParty> Recipients, DateTime SentAt, DateTime ImportedAt, MessageDirection Direction,
    MessageProcessingStatus ProcessingStatus, MessageCategory Category, double? CategoryConfidence, string? AnalysisSummary,
    MessageFacts? Facts, string SourceKey, string? WebUrl, bool IsSystemGenerated, List<LinkedTaskRef> Links);

public sealed record LinkedTaskRef(Guid LinkId, Guid TaskId, string Number, string Title, MatchMethod Method, double Confidence,
    string Explanation, List<string> Signals, LinkStatus Status);

public sealed record MessageFilter(string? Text, CommunicationChannel? Channel, MessageProcessingStatus? Status, bool? Unlinked,
    DateOnly? From, DateOnly? To, int Page = 1, int PageSize = 50);

public sealed record ReviewDecision(string Action, Guid? TaskId);

// ───────────────────────────── Reports ─────────────────────────────

public sealed record ReportRequest(DateOnly? From, DateOnly? To, Guid? DepartmentId, Guid? TeamId);

public sealed record MetricValue(string Key, string Label, double Value, string Unit, string? Hint, Drilldown? Drilldown);

public sealed record GroupPerformance(Guid Id, string Name, int Assigned, int Completed, int CompletedOnTime, int OnTimePercent,
    double AvgCompletionDays, double AvgDelayDays, int Overdue, int Escalations, int CommitmentsMet, int CommitmentsMissed, int Reopened);

public sealed record AgeingBucket(string Label, int Count, Drilldown Drilldown);

public sealed record EscalationBreakdown(string Key, string Label, int Count);

public sealed record TrendPoint(DateOnly Date, int Created, int Completed, int Overdue);

public sealed record ReportDto(DateOnly From, DateOnly To, List<MetricValue> Metrics, List<GroupPerformance> Departments,
    List<GroupPerformance> Teams, List<GroupPerformance> People, List<AgeingBucket> OverdueAgeing, List<EscalationBreakdown> EscalationsByLevel,
    List<EscalationBreakdown> EscalationsByTrigger, List<TrendPoint> Trend, List<RecurringDefinitionDto> RecurringCompliance,
    List<CommitmentDto> MissedCommitments, List<TaskListItem> NoResponseTasks);

// ───────────────────────────── Search ─────────────────────────────

public sealed record SearchHit(string Kind, Guid Id, string Title, string Snippet, Guid? TaskId, string? TaskNumber, DateTime? At, string Route);

public sealed record SearchResult(string Query, List<SearchHit> Hits, Dictionary<string, int> Counts);

// ───────────────────────────── Notifications ─────────────────────────────

public sealed record InboxItem(Guid Id, NotificationCategory Category, string Subject, string Body, Guid? TaskId, string? TaskNumber,
    DateTime CreatedAt, DateTime? ReadAt);

public sealed record NotificationRow(Guid Id, string IdempotencyKey, Guid? TaskId, string? TaskNumber, string? Recipient, NotificationChannel Channel,
    NotificationCategory Category, string Subject, NotificationStatus Status, int AttemptCount, DateTime ScheduledAt, DateTime? LastAttemptAt,
    DateTime? NextAttemptAt, DateTime? SentAt, string? Error, DateTime CreatedAt, DateTime? ExpiresAt);
