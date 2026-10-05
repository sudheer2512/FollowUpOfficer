namespace FollowUp.Domain;

// ───────────────────────────── Roles & organisation ─────────────────────────────

/// <summary>Application roles (spec §3). Stored as flags on the user account.</summary>
[Flags]
public enum AppRole
{
    None = 0,
    TeamMember = 1,
    TeamLeader = 2,
    SeniorManager = 4,
    Executive = 8,       // CEO / MD / executive — complete visibility
    Administrator = 16,
}

public enum TeamMemberRole { Member, Leader, Deputy }

public enum HolidayKind { Public, Company, Regional }

// ───────────────────────────── Tasks ─────────────────────────────

public enum TaskPriority { Low, Normal, High, Urgent }

/// <summary>Criticality drives notification channels, reminder intervals, escalations and CEO alerts (spec §18).</summary>
public enum Criticality { Informational, Normal, Important, Critical, BusinessCritical }

/// <summary>
/// Workflow status (spec §7). "Overdue" is deliberately not stored: it is derived from the deadline so that the
/// underlying workflow state (e.g. In Progress) is never lost. The API exposes it as a display status and filter.
/// </summary>
public enum WorkStatus
{
    Draft,
    Assigned,
    AcknowledgementPending,
    Accepted,
    InProgress,
    WaitingForInformation,
    WaitingForExternalParty,
    WaitingForApproval,
    Blocked,
    PartiallyCompleted,
    CompletedByAssignee,
    CompletionAwaitingApproval,
    Completed,
    Deferred,
    Cancelled,
}

public enum TaskVisibility { Normal, TeamOnly, ManagementOnly, CeoOnly, SelectedParticipants }

public enum AssigneeRole { Primary, Additional, TeamLeader, Supporting }

public enum AcknowledgementResponse { Accept, NeedClarification, CannotAccept, ProposeDifferentDeadline }

public enum AcknowledgementState { NotRequired, Pending, Accepted, ClarificationRequested, Declined, DeadlineProposed }

public enum DependencyType { FinishToStart, StartToStart, External }

public enum TaskHealth { Healthy, Watch, AtRisk, Critical }

public enum TaskSource { Manual, NaturalLanguage, Voice, Recurring, Template, Meeting, Email, Teams, Import }

public enum ChangeSource { User, System, Ai, Email, Teams, Recurring, Seed }

public enum DeadlineBucket { None, Later, DueThisWeek, DueTomorrow, DueToday, Overdue, SeverelyOverdue }

public enum FollowUpPauseReason { Customer, Vendor, GovernmentDepartment, Management, Dependency, ExternalInformation, Other }

// ───────────────────────────── Follow-up ─────────────────────────────

public enum FollowUpFrequency { None, Daily, EveryNDays, Weekly, Fortnightly, Monthly, SpecificWeekdays, SpecificDates, Custom }

public enum FollowUpAction { None, Stop, Skip, Remind }

public enum FollowUpKind
{
    None,
    UpdateRequest,
    AcknowledgementReminder,
    DueSoon,
    DueToday,
    Overdue,
    BlockerCheck,
    DependencyCheck,
    CommitmentCheck,
    ApprovalPending,
}

// ───────────────────────────── Commitments, approvals, extensions ─────────────────────────────

public enum CommitmentStatus { Open, Met, Missed, Superseded, Cancelled }

public enum CommunicationSource { Manual, Comment, Email, TeamsChat, TeamsChannel, Meeting, Acknowledgement }

public enum ApproverRole { TeamLeader, Delegator, DepartmentHead, Ceo, AccountableManager }

public enum ApprovalDecision { Pending, Approved, Reopened, MoreWorkRequested, Superseded }

public enum EvidenceKind { Document, Report, Screenshot, TestResult, Url, GitCommit, CustomerConfirmation, Other }

public enum ExtensionStatus { Pending, Approved, Rejected, Withdrawn }

public enum SuggestionKind { MarkCompletedByAssignee, ChangeStatus, ChangeDueDate, RecordBlocker, LinkCommunication, CreateCommitment }

public enum SuggestionStatus { Pending, Accepted, Dismissed, AutoApplied, Expired }

// ───────────────────────────── Communications ─────────────────────────────

public enum CommunicationChannel { Email, TeamsChat, TeamsChannel }

public enum MessageDirection { Inbound, Outbound, Internal }

public enum MessageProcessingStatus { Pending, Processed, NeedsReview, Ignored, Failed }

/// <summary>AI classification of a communication (spec §23).</summary>
public enum MessageCategory
{
    Unclassified,
    ProgressUpdate,
    Completion,
    Delay,
    Blocker,
    Commitment,
    DeadlineChange,
    Clarification,
    Approval,
    Rejection,
    Escalation,
    Information,
    RequestForHelp,
    Unrelated,
}

/// <summary>How a communication was associated with a task (spec §22, §26).</summary>
public enum MatchMethod { TaskNumber, ConversationThread, ReplyToNotification, Participants, Keywords, Project, Subject, RecentContext, Semantic, Manual }

public enum LinkStatus { Linked, Suggested, Rejected }

// ───────────────────────────── Recurrence ─────────────────────────────

public enum RecurrenceFrequency { Daily, WorkingDays, Weekly, Monthly, Quarterly, HalfYearly, Yearly, FinancialYear, AnchorDates }

public enum MonthlyPattern { DayOfMonth, LastDay, FirstWorkingDay, LastWorkingDay, NthWeekday }

public enum DueRuleKind { SameDay, AfterDays, AfterWorkingDays, DayOfMonth, WeekdayOfSameWeek, EndOfMonth, AnchorDate }

public enum HolidayHandling { Keep, NextWorkingDay, PreviousWorkingDay, Skip }

/// <summary>What to do with occurrences that should have been generated while the system was down (spec §15).</summary>
public enum CatchUpPolicy { GenerateAll, GenerateLatestOnly, SkipMissed, GenerateWithinWindow }

public enum RecurrenceState { Active, Paused, Ended }

public enum OccurrenceOutcome { Created, CreatedLate, Skipped, SkippedByUser, SkippedHoliday, SkippedPaused, SkippedCatchUp, OutOfRange }

public enum ChangeScope { ThisInstance, ThisAndFuture, EntireSeries }

// ───────────────────────────── Escalation & attention ─────────────────────────────

public enum EscalationTrigger { BeforeDue, OnDue, Overdue, NoResponse, AcknowledgementPending, CommitmentMissed, Blocked }

public enum ThresholdUnit { Hours, Days, WorkingDays }

[Flags]
public enum EscalationTarget
{
    None = 0,
    Assignee = 1,
    TeamLeader = 2,
    AccountableManager = 4,
    DepartmentHead = 8,
    Delegator = 16,
    Ceo = 32,
    Followers = 64,
}

[Flags]
public enum NotifyChannels
{
    None = 0,
    Email = 1,
    Teams = 2,
    InApp = 4,
}

public enum AttentionType
{
    CriticalOverdue,
    ExtensionRequest,
    RepeatedNonResponse,
    Blocked,
    ApprovalRequired,
    CommitmentMissed,
    CriticalMessage,
    DepartmentConflict,
    CustomerEscalation,
    AcknowledgementPending,
    AcknowledgementDeclined,
    AiSuggestion,
    EscalatedToCeo,
    RecurringMissed,
}

public enum AttentionStatus { Open, Snoozed, Resolved, Dismissed }

// ───────────────────────────── Notifications ─────────────────────────────

public enum NotificationChannel { Email, TeamsChat, TeamsChannel, TeamsActivity, InApp }

public enum NotificationCategory
{
    Assignment,
    AcknowledgementRequest,
    Reminder,
    DueSoon,
    DueToday,
    Overdue,
    Escalation,
    CommitmentMissed,
    ApprovalRequest,
    ApprovalDecision,
    ExtensionRequest,
    ExtensionDecision,
    StatusChange,
    Comment,
    CeoAlert,
    Digest,
    WeeklyReview,
    General,
}

public enum NotificationStatus { Pending, Sending, Sent, Failed, DeadLetter, Cancelled, Suppressed, Expired }

// ───────────────────────────── Platform ─────────────────────────────

public enum IntegrationType { OutlookMail, TeamsChat, TeamsChannel, Directory, Calendar }

public enum SyncState { Idle, Running, Succeeded, Failed, AuthError, Throttled, Disabled }

public enum JobScheduleKind { Interval, DailyAt, WeeklyAt }

/// <summary>Policy applied when a job's scheduled run was missed (downtime) — spec §62.</summary>
public enum MisfirePolicy { RunOnce, SkipToNext, RunIfWithinWindow }

public enum JobState { Idle, Running, Failed, Disabled }

public enum JobOutcome { Running, Succeeded, Failed, TimedOut, Abandoned, Skipped, Cancelled }

public enum JobTrigger { Schedule, CatchUp, Manual, Startup }

public enum IntegrationErrorKind { Authentication, Permission, Throttled, Transient, NotFound, Gone, Validation, Unknown }

public enum MeetingActionStatus { Open, TaskCreated, Done, Cancelled }
