using FollowUp.Core.Abstractions;
using FollowUp.Domain;
using FollowUp.Domain.Rules;

namespace FollowUp.Core.Contracts;

// ───────────────────────────── Organisation ─────────────────────────────

public sealed record DepartmentDto(Guid Id, string Code, string Name, PersonRef? Head, Guid? ParentDepartmentId, bool IsActive, int EmployeeCount, int TeamCount);

public sealed record DepartmentInput(string Code, string Name, Guid? HeadId, Guid? ParentDepartmentId, bool IsActive = true);

public sealed record TeamMemberDto(Guid EmployeeId, string Name, string? JobTitle, TeamMemberRole Role);

public sealed record TeamDto(Guid Id, string Name, NamedRef? Department, PersonRef? Leader, PersonRef? DeputyLeader, string? Email,
    string? TeamsTeamId, string? TeamsChannelId, string? TeamsChannelName, bool IsActive, List<TeamMemberDto> Members);

public sealed record TeamInput(string Name, Guid? DepartmentId, Guid? LeaderId, Guid? DeputyLeaderId, string? Email, string? TeamsTeamId,
    string? TeamsChannelId, string? TeamsChannelName, bool IsActive, List<Guid> MemberIds);

public sealed record EmployeeDto(Guid Id, string EmployeeCode, string DisplayName, string Email, string? JobTitle, string? Phone,
    NamedRef? Department, PersonRef? Manager, List<NamedRef> Teams, bool IsActive, string? UserName, AppRole Roles, bool HasLogin,
    bool LoginEnabled, DateTime? LastLoginAt, string? ExternalUserId, NotifyChannels PreferredChannels);

public sealed record EmployeeInput(string EmployeeCode, string DisplayName, string Email, string? JobTitle, string? Phone, Guid? DepartmentId,
    Guid? ManagerId, bool IsActive, NotifyChannels PreferredChannels = NotifyChannels.Email | NotifyChannels.Teams | NotifyChannels.InApp);

public sealed record AccountInput(string UserName, AppRole Roles, bool Enabled, string? NewPassword);

public sealed record CalendarDto(Guid Id, string Name, string? Region, string TimeZoneId, string WeekendDays, TimeOnly WorkdayStart,
    TimeOnly WorkdayEnd, bool IsDefault, List<HolidayDto> Holidays);

public sealed record HolidayDto(Guid Id, DateOnly Date, string Name, HolidayKind Kind);

public sealed record CalendarInput(string Name, string? Region, string TimeZoneId, string WeekendDays, TimeOnly WorkdayStart, TimeOnly WorkdayEnd, bool IsDefault);

public sealed record HolidayInput(DateOnly Date, string Name, HolidayKind Kind);

/// <summary>Lookup lists for forms (people, teams, departments, projects, categories, policies, tags).</summary>
public sealed record Lookups(List<PersonLookup> People, List<TeamLookup> Teams, List<NamedRef> Departments, List<NamedRef> Projects,
    List<NamedRef> Categories, List<NamedRef> EscalationPolicies, List<string> Tags, List<NamedRef> Templates);

public sealed record PersonLookup(Guid Id, string Name, string? JobTitle, string? Email, Guid? DepartmentId, string? DepartmentName, bool IsExecutive);

public sealed record TeamLookup(Guid Id, string Name, Guid? DepartmentId, Guid? LeaderId, List<Guid> MemberIds);

// ───────────────────────────── Escalation policies ─────────────────────────────

public sealed record EscalationStepDto(Guid? Id, int Order, EscalationTrigger Trigger, double Threshold, ThresholdUnit Unit,
    EscalationTarget Targets, NotifyChannels Channels, int Level, bool RaiseCeoAttention, string? Description = null);

public sealed record EscalationPolicyDto(Guid Id, string Name, string? Description, string AppliesTo, bool IsDefault, bool IsActive, List<EscalationStepDto> Steps);

public sealed record EscalationPolicyInput(string Name, string? Description, string AppliesTo, bool IsDefault, bool IsActive, List<EscalationStepDto> Steps);

// ───────────────────────────── Projects, meetings, templates, views ─────────────────────────────

public sealed record ProjectInput(string Code, string Name, string? Description, Guid? OwnerId, Guid? DepartmentId, DateOnly? StartDate,
    DateOnly? TargetDate, TaskVisibility Visibility = TaskVisibility.Normal, bool IsActive = true);

public sealed record MeetingDto(Guid Id, string Title, DateTime HeldAt, PersonRef? Organizer, string? Location, string? Notes, NamedRef? Project,
    List<PersonRef> Participants, List<ActionItemDto> ActionItems);

public sealed record ActionItemDto(Guid Id, string? Decision, string ActionText, PersonRef? Owner, DateOnly? DueDate, Criticality Criticality,
    Guid? TaskId, string? TaskNumber, WorkStatus? TaskStatus, MeetingActionStatus Status);

public sealed record MeetingInput(string Title, DateTime HeldAt, Guid? OrganizerId, string? Location, string? Notes, Guid? ProjectId,
    List<Guid> ParticipantIds, List<ActionItemInput> ActionItems);

public sealed record ActionItemInput(Guid? Id, string? Decision, string ActionText, Guid? OwnerId, DateOnly? DueDate, Criticality Criticality = Criticality.Normal);

public sealed record CreateActionTasksRequest(List<Guid>? ActionItemIds, FollowUpSchedule? FollowUp);

public sealed record TemplateDto(Guid Id, string Name, string Category, string? Description, TaskPriority Priority, Criticality Criticality,
    bool RequiresApproval, Guid? EscalationPolicyId, FollowUpSchedule FollowUp, List<string> Roles, List<TemplateItem> Items,
    List<ApproverRole> ApprovalFlow, bool IsActive);

public sealed record TemplateInput(string Name, string Category, string? Description, TaskPriority Priority, Criticality Criticality,
    bool RequiresApproval, Guid? EscalationPolicyId, FollowUpSchedule? FollowUp, List<string> Roles, List<TemplateItem> Items,
    List<ApproverRole> ApprovalFlow, bool IsActive = true);

/// <summary>Instantiate a template: role placeholders → people, anchor date for due offsets.</summary>
public sealed record InstantiateTemplateRequest(string? Title, DateOnly? AnchorDate, Dictionary<string, Guid> RoleAssignments, Guid? ProjectId,
    Guid? TeamId, Guid? DepartmentId, Criticality? Criticality);

public sealed record SavedViewDto(Guid Id, string Name, TaskFilter Filter, bool IsShared, bool IsMine);

public sealed record SavedViewInput(string Name, TaskFilter Filter, bool IsShared);

// ───────────────────────────── Integration monitor (spec §67) ─────────────────────────────

public sealed record CheckpointDto(Guid Id, IntegrationType Type, string SourceKey, string DisplayName, bool Enabled, SyncState Status,
    DateTime? LastSuccessfulSyncAt, DateTime? LastAttemptAt, DateTime? NextRunAt, DateTime? LastProcessedAt, string? LastProcessedExternalId,
    bool HasDeltaToken, bool MidPagination, string? Error, int RetryCount, long ItemsImportedTotal, int LastRunItemCount, int PendingMessages);

public sealed record JobDto(string Key, string Name, string Description, JobScheduleKind ScheduleKind, int IntervalSeconds, TimeOnly? TimeOfDay,
    DayOfWeek? DayOfWeek, MisfirePolicy Misfire, bool Enabled, JobState State, DateTime? NextRunAt, DateTime? LastStartedAt,
    DateTime? LastSucceededAt, DateTime? LastFailedAt, string? LastError, int ConsecutiveFailures, string? LeaseOwner, DateTime? LeaseExpiresAt,
    string? Checkpoint, string ScheduleText);

public sealed record JobExecutionDto(Guid Id, string JobKey, JobTrigger Trigger, string Host, DateTime StartedAt, DateTime? FinishedAt,
    JobOutcome Outcome, int ItemsProcessed, string? Message, string? Error, DateTime? DueAt, double? DurationMs);

public sealed record IntegrationErrorDto(Guid Id, string Integration, string? SourceKey, string Operation, IntegrationErrorKind Kind,
    int? StatusCode, string Message, int? RetryAfterSeconds, DateTime OccurredAt, bool Resolved);

public sealed record MonitorOverview(IntegrationProbe? Probe, string Mode, List<CheckpointDto> Checkpoints, List<JobDto> Jobs,
    int MessagesAwaitingProcessing, int MessagesNeedingReview, int MessagesFailed, int NotificationsPending, int NotificationsFailed,
    int NotificationsDeadLetter, int FailedApiCalls24h, int AuthProblems24h, int RateLimitEvents24h, int PermissionErrors24h,
    int ResyncEvents24h, List<IntegrationErrorDto> RecentErrors, List<JobExecutionDto> RecentExecutions, List<HostHeartbeat> Hosts,
    DateTime ServerTime);

public sealed record HostHeartbeat(string Host, DateTime LastSeen, int Executions24h);

/// <summary>Runtime-editable settings (subset of <see cref="Common.FollowUpOptions"/>).</summary>
public sealed record RuntimeSettings
{
    public int SeverelyOverdueDays { get; init; } = 7;
    public int RecentUpdateWindowHours { get; init; } = 24;
    public int MaxRemindersPerRecipientPerDay { get; init; } = 6;
    public int ReminderExpiryHours { get; init; } = 20;
    public double AutoAttachConfidence { get; init; } = 0.80;
    public double SuggestConfidence { get; init; } = 0.45;
    public bool DailyDigestEnabled { get; init; } = true;
    public TimeOnly DailyDigestTime { get; init; } = new(8, 30);
    public bool WeeklyReviewEnabled { get; init; } = true;
    public bool CalendarSyncEnabled { get; init; } = true;
    public Criticality AcknowledgementFrom { get; init; } = Criticality.Normal;
    public int RepeatedNonResponseReminders { get; init; } = 3;
}
