/**
 * String unions for every server enum (src/FollowUp.Domain/Enums.cs and friends).
 * The API serialises enums as strings; [Flags] enums arrive as comma lists ("Email, Teams").
 * Each union has a matching `*_VALUES` array so forms and filters can iterate the options in server order.
 */

export type DayOfWeek = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday';
export const DAY_OF_WEEK_VALUES: DayOfWeek[] = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
/** Monday-first order for pickers (business week). */
export const WEEKDAYS_MONDAY_FIRST: DayOfWeek[] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

// ── Roles & organisation ──
/** [Flags] AppRole — comma list on the wire, e.g. "TeamMember, Executive". */
export type AppRoleFlag = 'TeamMember' | 'TeamLeader' | 'SeniorManager' | 'Executive' | 'Administrator';
export const APP_ROLE_VALUES: AppRoleFlag[] = ['TeamMember', 'TeamLeader', 'SeniorManager', 'Executive', 'Administrator'];
export type AppRole = string;

export type TeamMemberRole = 'Member' | 'Leader' | 'Deputy';
export type HolidayKind = 'Public' | 'Company' | 'Regional';
export const HOLIDAY_KIND_VALUES: HolidayKind[] = ['Public', 'Company', 'Regional'];

// ── Tasks ──
export type TaskPriority = 'Low' | 'Normal' | 'High' | 'Urgent';
export const TASK_PRIORITY_VALUES: TaskPriority[] = ['Low', 'Normal', 'High', 'Urgent'];

export type Criticality = 'Informational' | 'Normal' | 'Important' | 'Critical' | 'BusinessCritical';
export const CRITICALITY_VALUES: Criticality[] = ['Informational', 'Normal', 'Important', 'Critical', 'BusinessCritical'];

export type WorkStatus =
  | 'Draft'
  | 'Assigned'
  | 'AcknowledgementPending'
  | 'Accepted'
  | 'InProgress'
  | 'WaitingForInformation'
  | 'WaitingForExternalParty'
  | 'WaitingForApproval'
  | 'Blocked'
  | 'PartiallyCompleted'
  | 'CompletedByAssignee'
  | 'CompletionAwaitingApproval'
  | 'Completed'
  | 'Deferred'
  | 'Cancelled';
export const WORK_STATUS_VALUES: WorkStatus[] = [
  'Draft', 'Assigned', 'AcknowledgementPending', 'Accepted', 'InProgress', 'WaitingForInformation',
  'WaitingForExternalParty', 'WaitingForApproval', 'Blocked', 'PartiallyCompleted', 'CompletedByAssignee',
  'CompletionAwaitingApproval', 'Completed', 'Deferred', 'Cancelled',
];
/** Statuses an assignee may set directly through POST /api/tasks/{id}/status. */
export const SETTABLE_STATUS_VALUES: WorkStatus[] = [
  'InProgress', 'WaitingForInformation', 'WaitingForExternalParty', 'WaitingForApproval', 'Blocked',
  'PartiallyCompleted', 'Deferred', 'Cancelled',
];
export const CLOSED_STATUSES: WorkStatus[] = ['Completed', 'Cancelled'];

export type TaskVisibility = 'Normal' | 'TeamOnly' | 'ManagementOnly' | 'CeoOnly' | 'SelectedParticipants';
export const TASK_VISIBILITY_VALUES: TaskVisibility[] = ['Normal', 'TeamOnly', 'ManagementOnly', 'CeoOnly', 'SelectedParticipants'];

export type AssigneeRole = 'Primary' | 'Additional' | 'TeamLeader' | 'Supporting';
export type AcknowledgementResponse = 'Accept' | 'NeedClarification' | 'CannotAccept' | 'ProposeDifferentDeadline';
export type AcknowledgementState = 'NotRequired' | 'Pending' | 'Accepted' | 'ClarificationRequested' | 'Declined' | 'DeadlineProposed';
export type DependencyType = 'FinishToStart' | 'StartToStart' | 'External';
export const DEPENDENCY_TYPE_VALUES: DependencyType[] = ['FinishToStart', 'StartToStart', 'External'];

export type TaskHealth = 'Healthy' | 'Watch' | 'AtRisk' | 'Critical';
export const TASK_HEALTH_VALUES: TaskHealth[] = ['Healthy', 'Watch', 'AtRisk', 'Critical'];

export type TaskSource = 'Manual' | 'NaturalLanguage' | 'Voice' | 'Recurring' | 'Template' | 'Meeting' | 'Email' | 'Teams' | 'Import';
export type ChangeSource = 'User' | 'System' | 'Ai' | 'Email' | 'Teams' | 'Recurring' | 'Seed';

export type DeadlineBucket = 'None' | 'Later' | 'DueThisWeek' | 'DueTomorrow' | 'DueToday' | 'Overdue' | 'SeverelyOverdue';
/** Buckets offered in the task filter (spec: Due Today/Tomorrow/This Week/Overdue/Severely Overdue). */
export const FILTER_BUCKET_VALUES: DeadlineBucket[] = ['DueToday', 'DueTomorrow', 'DueThisWeek', 'Overdue', 'SeverelyOverdue'];

export type FollowUpPauseReason = 'Customer' | 'Vendor' | 'GovernmentDepartment' | 'Management' | 'Dependency' | 'ExternalInformation' | 'Other';
export const FOLLOW_UP_PAUSE_REASON_VALUES: FollowUpPauseReason[] = [
  'Customer', 'Vendor', 'GovernmentDepartment', 'Management', 'Dependency', 'ExternalInformation', 'Other',
];

// ── Follow-up ──
export type FollowUpFrequency =
  | 'None' | 'Daily' | 'EveryNDays' | 'Weekly' | 'Fortnightly' | 'Monthly' | 'SpecificWeekdays' | 'SpecificDates' | 'Custom';
export const FOLLOW_UP_FREQUENCY_VALUES: FollowUpFrequency[] = [
  'None', 'Daily', 'EveryNDays', 'Weekly', 'Fortnightly', 'Monthly', 'SpecificWeekdays', 'SpecificDates', 'Custom',
];

// ── Commitments, approvals, extensions ──
export type CommitmentStatus = 'Open' | 'Met' | 'Missed' | 'Superseded' | 'Cancelled';
export type CommunicationSource = 'Manual' | 'Comment' | 'Email' | 'TeamsChat' | 'TeamsChannel' | 'Meeting' | 'Acknowledgement';
export type ApproverRole = 'TeamLeader' | 'Delegator' | 'DepartmentHead' | 'Ceo' | 'AccountableManager';
export const APPROVER_ROLE_VALUES: ApproverRole[] = ['TeamLeader', 'Delegator', 'DepartmentHead', 'Ceo', 'AccountableManager'];
export type ApprovalDecision = 'Pending' | 'Approved' | 'Reopened' | 'MoreWorkRequested' | 'Superseded';
export type ApprovalAction = 'Confirm' | 'Reopen' | 'RequestMoreWork';
export type EvidenceKind = 'Document' | 'Report' | 'Screenshot' | 'TestResult' | 'Url' | 'GitCommit' | 'CustomerConfirmation' | 'Other';
export const EVIDENCE_KIND_VALUES: EvidenceKind[] = [
  'Document', 'Report', 'Screenshot', 'TestResult', 'Url', 'GitCommit', 'CustomerConfirmation', 'Other',
];
export type ExtensionStatus = 'Pending' | 'Approved' | 'Rejected' | 'Withdrawn';
export type SuggestionKind =
  | 'MarkCompletedByAssignee' | 'ChangeStatus' | 'ChangeDueDate' | 'RecordBlocker' | 'LinkCommunication' | 'CreateCommitment';
export type SuggestionStatus = 'Pending' | 'Accepted' | 'Dismissed' | 'AutoApplied' | 'Expired';

// ── Communications ──
export type CommunicationChannel = 'Email' | 'TeamsChat' | 'TeamsChannel';
export const COMMUNICATION_CHANNEL_VALUES: CommunicationChannel[] = ['Email', 'TeamsChat', 'TeamsChannel'];
export type MessageDirection = 'Inbound' | 'Outbound' | 'Internal';
export type MessageProcessingStatus = 'Pending' | 'Processed' | 'NeedsReview' | 'Ignored' | 'Failed';
export const MESSAGE_PROCESSING_STATUS_VALUES: MessageProcessingStatus[] = ['Pending', 'Processed', 'NeedsReview', 'Ignored', 'Failed'];
export type MessageCategory =
  | 'Unclassified' | 'ProgressUpdate' | 'Completion' | 'Delay' | 'Blocker' | 'Commitment' | 'DeadlineChange'
  | 'Clarification' | 'Approval' | 'Rejection' | 'Escalation' | 'Information' | 'RequestForHelp' | 'Unrelated';
export type MatchMethod =
  | 'TaskNumber' | 'ConversationThread' | 'ReplyToNotification' | 'Participants' | 'Keywords' | 'Project' | 'Subject'
  | 'RecentContext' | 'Semantic' | 'Manual';
export type LinkStatus = 'Linked' | 'Suggested' | 'Rejected';

// ── Recurrence ──
export type RecurrenceFrequency =
  | 'Daily' | 'WorkingDays' | 'Weekly' | 'Monthly' | 'Quarterly' | 'HalfYearly' | 'Yearly' | 'FinancialYear' | 'AnchorDates';
export const RECURRENCE_FREQUENCY_VALUES: RecurrenceFrequency[] = [
  'Daily', 'WorkingDays', 'Weekly', 'Monthly', 'Quarterly', 'HalfYearly', 'Yearly', 'FinancialYear', 'AnchorDates',
];
export type MonthlyPattern = 'DayOfMonth' | 'LastDay' | 'FirstWorkingDay' | 'LastWorkingDay' | 'NthWeekday';
export const MONTHLY_PATTERN_VALUES: MonthlyPattern[] = ['DayOfMonth', 'LastDay', 'FirstWorkingDay', 'LastWorkingDay', 'NthWeekday'];
export type DueRuleKind = 'SameDay' | 'AfterDays' | 'AfterWorkingDays' | 'DayOfMonth' | 'WeekdayOfSameWeek' | 'EndOfMonth' | 'AnchorDate';
export const DUE_RULE_KIND_VALUES: DueRuleKind[] = [
  'SameDay', 'AfterDays', 'AfterWorkingDays', 'DayOfMonth', 'WeekdayOfSameWeek', 'EndOfMonth', 'AnchorDate',
];
export type HolidayHandling = 'Keep' | 'NextWorkingDay' | 'PreviousWorkingDay' | 'Skip';
export const HOLIDAY_HANDLING_VALUES: HolidayHandling[] = ['Keep', 'NextWorkingDay', 'PreviousWorkingDay', 'Skip'];
export type CatchUpPolicy = 'GenerateAll' | 'GenerateLatestOnly' | 'SkipMissed' | 'GenerateWithinWindow';
export const CATCH_UP_POLICY_VALUES: CatchUpPolicy[] = ['GenerateAll', 'GenerateLatestOnly', 'SkipMissed', 'GenerateWithinWindow'];
export type RecurrenceState = 'Active' | 'Paused' | 'Ended';
export type OccurrenceOutcome =
  | 'Created' | 'CreatedLate' | 'Skipped' | 'SkippedByUser' | 'SkippedHoliday' | 'SkippedPaused' | 'SkippedCatchUp' | 'OutOfRange';
export const OCCURRENCE_OUTCOME_VALUES: OccurrenceOutcome[] = [
  'Created', 'CreatedLate', 'Skipped', 'SkippedByUser', 'SkippedHoliday', 'SkippedPaused', 'SkippedCatchUp', 'OutOfRange',
];
export type ChangeScope = 'ThisInstance' | 'ThisAndFuture' | 'EntireSeries';

// ── Escalation & attention ──
export type EscalationTrigger = 'BeforeDue' | 'OnDue' | 'Overdue' | 'NoResponse' | 'AcknowledgementPending' | 'CommitmentMissed' | 'Blocked';
export const ESCALATION_TRIGGER_VALUES: EscalationTrigger[] = [
  'BeforeDue', 'OnDue', 'Overdue', 'NoResponse', 'AcknowledgementPending', 'CommitmentMissed', 'Blocked',
];
export type ThresholdUnit = 'Hours' | 'Days' | 'WorkingDays';
export const THRESHOLD_UNIT_VALUES: ThresholdUnit[] = ['Hours', 'Days', 'WorkingDays'];

/** [Flags] EscalationTarget — comma list on the wire ("TeamLeader, Ceo"). */
export type EscalationTargetFlag = 'Assignee' | 'TeamLeader' | 'AccountableManager' | 'DepartmentHead' | 'Delegator' | 'Ceo' | 'Followers';
export const ESCALATION_TARGET_VALUES: EscalationTargetFlag[] = [
  'Assignee', 'TeamLeader', 'AccountableManager', 'DepartmentHead', 'Delegator', 'Ceo', 'Followers',
];
export type EscalationTarget = string;

/** [Flags] NotifyChannels — comma list on the wire ("Email, Teams, InApp"). */
export type NotifyChannelFlag = 'Email' | 'Teams' | 'InApp';
export const NOTIFY_CHANNEL_VALUES: NotifyChannelFlag[] = ['Email', 'Teams', 'InApp'];
export type NotifyChannels = string;

export type AttentionType =
  | 'CriticalOverdue' | 'ExtensionRequest' | 'RepeatedNonResponse' | 'Blocked' | 'ApprovalRequired' | 'CommitmentMissed'
  | 'CriticalMessage' | 'DepartmentConflict' | 'CustomerEscalation' | 'AcknowledgementPending' | 'AcknowledgementDeclined'
  | 'AiSuggestion' | 'EscalatedToCeo' | 'RecurringMissed';
export const ATTENTION_TYPE_VALUES: AttentionType[] = [
  'CriticalOverdue', 'ExtensionRequest', 'RepeatedNonResponse', 'Blocked', 'ApprovalRequired', 'CommitmentMissed',
  'CriticalMessage', 'DepartmentConflict', 'CustomerEscalation', 'AcknowledgementPending', 'AcknowledgementDeclined',
  'AiSuggestion', 'EscalatedToCeo', 'RecurringMissed',
];
export type AttentionStatus = 'Open' | 'Snoozed' | 'Resolved' | 'Dismissed';
export const ATTENTION_STATUS_VALUES: AttentionStatus[] = ['Open', 'Snoozed', 'Resolved', 'Dismissed'];

// ── Notifications ──
export type NotificationChannel = 'Email' | 'TeamsChat' | 'TeamsChannel' | 'TeamsActivity' | 'InApp';
export type NotificationCategory =
  | 'Assignment' | 'AcknowledgementRequest' | 'Reminder' | 'DueSoon' | 'DueToday' | 'Overdue' | 'Escalation'
  | 'CommitmentMissed' | 'ApprovalRequest' | 'ApprovalDecision' | 'ExtensionRequest' | 'ExtensionDecision'
  | 'StatusChange' | 'Comment' | 'CeoAlert' | 'Digest' | 'WeeklyReview' | 'General';
export type NotificationStatus = 'Pending' | 'Sending' | 'Sent' | 'Failed' | 'DeadLetter' | 'Cancelled' | 'Suppressed' | 'Expired';
export const NOTIFICATION_STATUS_VALUES: NotificationStatus[] = [
  'Pending', 'Sending', 'Sent', 'Failed', 'DeadLetter', 'Cancelled', 'Suppressed', 'Expired',
];

// ── Platform ──
export type IntegrationType = 'OutlookMail' | 'TeamsChat' | 'TeamsChannel' | 'Directory' | 'Calendar';
export type SyncState = 'Idle' | 'Running' | 'Succeeded' | 'Failed' | 'AuthError' | 'Throttled' | 'Disabled';
export type JobScheduleKind = 'Interval' | 'DailyAt' | 'WeeklyAt';
export type MisfirePolicy = 'RunOnce' | 'SkipToNext' | 'RunIfWithinWindow';
export type JobState = 'Idle' | 'Running' | 'Failed' | 'Disabled';
export type JobOutcome = 'Running' | 'Succeeded' | 'Failed' | 'TimedOut' | 'Abandoned' | 'Skipped' | 'Cancelled';
export type JobTrigger = 'Schedule' | 'CatchUp' | 'Manual' | 'Startup';
export type IntegrationErrorKind = 'Authentication' | 'Permission' | 'Throttled' | 'Transient' | 'NotFound' | 'Gone' | 'Validation' | 'Unknown';
export type MeetingActionStatus = 'Open' | 'TaskCreated' | 'Done' | 'Cancelled';

// ── AI ──
export type QuestionIntent =
  | 'Unknown' | 'AttentionRequired' | 'MissedCommitments' | 'CommitmentsDue' | 'NoResponse' | 'Overdue' | 'Blocked'
  | 'ProjectBlockers' | 'RecurringMissed' | 'DueSoon' | 'Critical' | 'CompletedRecently' | 'Workload' | 'AwaitingApproval'
  | 'TaskLookup' | 'Summary';
