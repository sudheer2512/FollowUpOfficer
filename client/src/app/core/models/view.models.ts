/**
 * TypeScript mirror of src/FollowUp.Core/Contracts/ViewContracts.cs plus the AI port records it embeds
 * (TaskDraft, QuestionQuery from Abstractions/AiPorts.cs) and MessageParty / MessageFacts (Domain/Communications.cs).
 */
import { DateOnly, DateTimeUtc, Drilldown, Guid, NamedRef, PersonRef, Tile, TimeOnly, Tone } from './common.models';
import {
  AttentionStatus,
  AttentionType,
  CatchUpPolicy,
  ChangeScope,
  CommunicationChannel,
  Criticality,
  HolidayHandling,
  LinkStatus,
  MatchMethod,
  MessageCategory,
  MessageDirection,
  MessageProcessingStatus,
  NotificationCategory,
  NotificationChannel,
  NotificationStatus,
  OccurrenceOutcome,
  QuestionIntent,
  RecurrenceState,
  TaskHealth,
  TaskPriority,
  TaskVisibility,
  WorkStatus,
} from './enums';
import { DueRule, FollowUpSchedule, RecurrenceRule } from './rules.models';
import { CommitmentDto, TaskListItem, WorkloadDto } from './task.models';

// ───────────────────────────── Attention ─────────────────────────────

export interface AttentionItemDto {
  id: Guid;
  type: AttentionType;
  typeLabel: string;
  title: string;
  detail: string;
  score: number;
  status: AttentionStatus;
  createdAt: DateTimeUtc;
  updatedAt: DateTimeUtc;
  snoozedUntil?: DateTimeUtc | null;
  task?: TaskListItem | null;
  commitmentId?: Guid | null;
  extensionRequestId?: Guid | null;
  approvalId?: Guid | null;
  messageId?: Guid | null;
  suggestionId?: Guid | null;
  /** Server-suggested quick actions for this item (free-form keys; the UI also infers actions from `type`). */
  actions: string[];
}

export type AttentionDecisionAction = 'resolve' | 'dismiss' | 'snooze' | 'reopen';

export interface AttentionDecision {
  action: AttentionDecisionAction;
  note?: string | null;
  snoozeUntil?: DateTimeUtc | null;
}

// ───────────────────────────── Dashboards ─────────────────────────────

export interface ApprovalQueueItem {
  /** "Completion" / "Extension" / "Acknowledgement"… */
  kind: string;
  id: Guid;
  task: TaskListItem;
  title: string;
  detail: string;
  requestedAt: DateTimeUtc;
}

export interface WorkbenchSection {
  key: string;
  title: string;
  description: string;
  count: number;
  drilldown: Drilldown;
  tasks: TaskListItem[];
  attention?: AttentionItemDto[] | null;
  commitments?: CommitmentDto[] | null;
  approvals?: ApprovalQueueItem[] | null;
}

/** CEO workbench (spec §38, §79). */
export interface CeoWorkbench {
  generatedAt: DateTimeUtc;
  greeting: string;
  tiles: Tile[];
  sections: WorkbenchSection[];
  summary: ExecutiveSummary;
}

export interface MemberLoad {
  person: PersonRef;
  open: number;
  dueThisWeek: number;
  overdue: number;
  critical: number;
  blocked: number;
  noRecentUpdate: number;
  completed: number;
}

/** Team dashboard (spec §40). */
export interface TeamDashboard {
  team: NamedRef;
  leader?: PersonRef | null;
  tiles: Tile[];
  members: MemberLoad[];
  attentionTasks: TaskListItem[];
  overdue: TaskListItem[];
  blocked: TaskListItem[];
  noRecentUpdate: TaskListItem[];
  recurring: RecurringDefinitionDto[];
}

/** Individual dashboard (spec §41). */
export interface MyDashboard {
  me: PersonRef;
  tiles: Tile[];
  sections: WorkbenchSection[];
}

/** Derived project status: "On Track" / "At Risk" / "Off Track" / "Completed" (server text). */
export interface ProjectSummary {
  id: Guid;
  code: string;
  name: string;
  description?: string | null;
  owner?: PersonRef | null;
  department?: NamedRef | null;
  startDate?: DateOnly | null;
  targetDate?: DateOnly | null;
  status: string;
  totalTasks: number;
  openTasks: number;
  completedTasks: number;
  overdueTasks: number;
  blockedTasks: number;
  criticalTasks: number;
  percentComplete: number;
  worstHealth: TaskHealth;
  nextDeadline?: TaskListItem | null;
  isActive: boolean;
}

export interface ProjectDetail {
  summary: ProjectSummary;
  tasks: TaskListItem[];
  blockers: TaskListItem[];
  commitments: CommitmentDto[];
}

// ───────────────────────────── Executive summary, digest, review ─────────────────────────────

export interface SummaryPoint {
  text: string;
  count?: number | null;
  tone: Tone;
  drilldown?: Drilldown | null;
}

/** AI executive summary (spec §56). */
export interface ExecutiveSummary {
  headline: string;
  points: SummaryPoint[];
  narrative?: string | null;
  /** e.g. "Heuristic", "Anthropic". */
  provider: string;
  generatedAt: DateTimeUtc;
}

export interface DigestDto {
  asOf: DateTimeUtc;
  open: number;
  overdue: number;
  critical: number;
  needAttention: number;
  dueToday: number;
  missedCommitments: number;
  completedYesterday: number;
  topAttention: AttentionItemDto[];
  summary: ExecutiveSummary;
}

export interface ReviewSection {
  key: string;
  title: string;
  tasks: TaskListItem[];
  commitments?: CommitmentDto[] | null;
  instances?: RecurringInstanceDto[] | null;
  lines?: string[] | null;
}

export interface TeamStatusRow {
  teamId: Guid;
  team: string;
  open: number;
  overdue: number;
  completed: number;
  blocked: number;
  onTimePercent: number;
  health: TaskHealth;
}

/** Weekly executive review (spec §55). */
export interface WeeklyReviewDto {
  weekStart: DateOnly;
  weekEnd: DateOnly;
  summary: ExecutiveSummary;
  sections: ReviewSection[];
  teams: TeamStatusRow[];
}

// ───────────────────────────── Natural language ─────────────────────────────

export interface ParseCommandRequest {
  text: string;
  voice?: boolean;
}

export interface DraftNote {
  field: string;
  message: string;
}

/** Structured task extracted from a natural-language or voice command (AiPorts.TaskDraft). */
export interface TaskDraft {
  title: string;
  description?: string | null;
  primaryAssigneeId?: Guid | null;
  assigneeIds: Guid[];
  teamIds: Guid[];
  departmentId?: Guid | null;
  projectId?: Guid | null;
  startDate?: DateOnly | null;
  dueDate?: DateOnly | null;
  dueTime?: TimeOnly | null;
  followUp?: FollowUpSchedule | null;
  priority: TaskPriority;
  criticality: Criticality;
  notifyCeoOnDelay: boolean;
  requiresApproval: boolean;
  isRecurring: boolean;
  recurrence?: RecurrenceRule | null;
  recurrenceDue?: DueRule | null;
  /** 0–1 */
  confidence: number;
  notes: DraftNote[];
  unresolvedNames: string[];
  needsConfirmation: boolean;
}

export interface ResolvedDraft {
  draft: TaskDraft;
  primaryAssignee?: PersonRef | null;
  assignees: PersonRef[];
  teams: NamedRef[];
  department?: NamedRef | null;
  project?: NamedRef | null;
  followUpText?: string | null;
  recurrenceText?: string | null;
  dueRuleText?: string | null;
  workload: WorkloadDto[];
  provider: string;
}

export interface AskRequest {
  question: string;
}

/** A natural-language question resolved into a structured query (AiPorts.QuestionQuery). */
export interface QuestionQuery {
  intent: QuestionIntent;
  departmentId?: Guid | null;
  teamId?: Guid | null;
  employeeId?: Guid | null;
  projectId?: Guid | null;
  minDaysOverdue?: number | null;
  from?: DateOnly | null;
  to?: DateOnly | null;
  minCriticality?: Criticality | null;
  taskNumber?: string | null;
  toMe: boolean;
  searchText?: string | null;
  confidence: number;
  interpretation: string;
}

export interface AskAnswer {
  question: string;
  query: QuestionQuery;
  interpretation: string;
  answer: string;
  tasks: TaskListItem[];
  commitments: CommitmentDto[];
  instances: RecurringInstanceDto[];
  attention: AttentionItemDto[];
  drilldown?: Drilldown | null;
  provider: string;
}

// ───────────────────────────── Recurring ─────────────────────────────

export interface RecurringDefinitionDto {
  id: Guid;
  seriesId: Guid;
  code: string;
  titleTemplate: string;
  description?: string | null;
  owner?: PersonRef | null;
  primaryAssignee?: PersonRef | null;
  additionalAssignees: PersonRef[];
  team?: NamedRef | null;
  department?: NamedRef | null;
  project?: NamedRef | null;
  priority: TaskPriority;
  criticality: Criticality;
  visibility: TaskVisibility;
  rule: RecurrenceRule;
  ruleText: string;
  due: DueRule;
  dueText: string;
  followUp: FollowUpSchedule;
  followUpText: string;
  escalationPolicyId?: Guid | null;
  holidayHandling: HolidayHandling;
  catchUp: CatchUpPolicy;
  catchUpWindowDays: number;
  requiresApproval: boolean;
  requiresAcknowledgement: boolean;
  checklist: string[];
  startDate: DateOnly;
  endDate?: DateOnly | null;
  maxOccurrences?: number | null;
  effectiveFrom: DateOnly;
  state: RecurrenceState;
  pauseReason?: string | null;
  resumeOn?: DateOnly | null;
  lastProcessedOccurrence?: DateOnly | null;
  nextOccurrenceAt?: DateTimeUtc | null;
  generatedCount: number;
  missedCount: number;
  openInstances: number;
  completedOnTime: number;
  completedLate: number;
  compliancePercent: number;
}

export interface RecurringDefinitionInput {
  code?: string | null;
  titleTemplate: string;
  description?: string | null;
  ownerId?: Guid | null;
  primaryAssigneeId?: Guid | null;
  additionalAssigneeIds: Guid[];
  teamId?: Guid | null;
  departmentId?: Guid | null;
  projectId?: Guid | null;
  categoryId?: Guid | null;
  priority: TaskPriority;
  criticality: Criticality;
  visibility: TaskVisibility;
  rule: RecurrenceRule;
  due: DueRule;
  followUp?: FollowUpSchedule | null;
  escalationPolicyId?: Guid | null;
  holidayHandling: HolidayHandling;
  catchUp: CatchUpPolicy;
  catchUpWindowDays: number;
  requiresApproval: boolean;
  requiresAcknowledgement: boolean;
  checklist: string[];
  startDate?: DateOnly | null;
  endDate?: DateOnly | null;
  maxOccurrences?: number | null;
}

export interface RecurringUpdateRequest {
  definition: RecurringDefinitionInput;
  scope: ChangeScope;
  fromInstanceId?: Guid | null;
  fromDate?: DateOnly | null;
}

export interface RecurringPauseRequest {
  reason?: string | null;
  resumeOn?: DateOnly | null;
}

export interface SkipOccurrenceRequest {
  nominalDate?: DateOnly | null;
  instanceId?: Guid | null;
  reason?: string | null;
}

export interface OccurrencePreview {
  nominalDate: DateOnly;
  /** Holiday-adjusted date; null when the occurrence is skipped. */
  date?: DateOnly | null;
  createAt: DateTimeUtc;
  dueDate?: DateOnly | null;
  dueTime?: TimeOnly | null;
  key: string;
  title: string;
  note?: string | null;
}

export interface PreviewRequest {
  rule: RecurrenceRule;
  due: DueRule;
  holidayHandling: HolidayHandling;
  startDate?: DateOnly | null;
  endDate?: DateOnly | null;
  titleTemplate?: string | null;
  count?: number;
}

export interface RecurringInstanceDto {
  id: Guid;
  definitionId: Guid;
  definitionTitle: string;
  nominalDate: DateOnly;
  occurrenceDate?: DateOnly | null;
  occurrenceKey: string;
  scheduledCreateAt: DateTimeUtc;
  dueDate?: DateOnly | null;
  outcome: OccurrenceOutcome;
  reason?: string | null;
  taskId?: Guid | null;
  taskNumber?: string | null;
  taskStatus?: WorkStatus | null;
  processedAt: DateTimeUtc;
  latenessMinutes: number;
  criticality: Criticality;
}

// ───────────────────────────── Communications ─────────────────────────────

export interface MessageParty {
  address?: string | null;
  name?: string | null;
  externalUserId?: string | null;
  employeeId?: Guid | null;
  /** "To" / "Cc" / "Mention"… */
  kind?: string | null;
}

/** Facts extracted from a message (spec §23). */
export interface MessageFacts {
  suggestedStatus?: WorkStatus | null;
  blocker?: string | null;
  commitmentDate?: DateOnly | null;
  commitmentText?: string | null;
  proposedDueDate?: DateOnly | null;
  progressPercent?: number | null;
  urgent: boolean;
  signals: string[];
}

export interface LinkedTaskRef {
  linkId: Guid;
  taskId: Guid;
  number: string;
  title: string;
  method: MatchMethod;
  confidence: number;
  explanation: string;
  signals: string[];
  status: LinkStatus;
}

export interface MessageDto {
  id: Guid;
  channel: CommunicationChannel;
  subject?: string | null;
  body: string;
  senderName?: string | null;
  senderAddress?: string | null;
  sender?: PersonRef | null;
  recipients: MessageParty[];
  sentAt: DateTimeUtc;
  importedAt: DateTimeUtc;
  direction: MessageDirection;
  processingStatus: MessageProcessingStatus;
  category: MessageCategory;
  categoryConfidence?: number | null;
  analysisSummary?: string | null;
  facts?: MessageFacts | null;
  sourceKey: string;
  webUrl?: string | null;
  isSystemGenerated: boolean;
  links: LinkedTaskRef[];
}

export interface MessageFilter {
  text?: string | null;
  channel?: CommunicationChannel | null;
  status?: MessageProcessingStatus | null;
  unlinked?: boolean | null;
  from?: DateOnly | null;
  to?: DateOnly | null;
  page?: number;
  pageSize?: number;
}

// ───────────────────────────── Reports ─────────────────────────────

export interface ReportRequest {
  from?: DateOnly | null;
  to?: DateOnly | null;
  departmentId?: Guid | null;
  teamId?: Guid | null;
}

export interface MetricValue {
  key: string;
  label: string;
  value: number;
  /** "%", "days", "hours", "" … */
  unit: string;
  hint?: string | null;
  drilldown?: Drilldown | null;
}

export interface GroupPerformance {
  id: Guid;
  name: string;
  assigned: number;
  completed: number;
  completedOnTime: number;
  onTimePercent: number;
  avgCompletionDays: number;
  avgDelayDays: number;
  overdue: number;
  escalations: number;
  commitmentsMet: number;
  commitmentsMissed: number;
  reopened: number;
}

export interface AgeingBucket {
  label: string;
  count: number;
  drilldown: Drilldown;
}

export interface EscalationBreakdown {
  key: string;
  label: string;
  count: number;
}

export interface TrendPoint {
  date: DateOnly;
  created: number;
  completed: number;
  overdue: number;
}

export interface ReportDto {
  from: DateOnly;
  to: DateOnly;
  metrics: MetricValue[];
  departments: GroupPerformance[];
  teams: GroupPerformance[];
  people: GroupPerformance[];
  overdueAgeing: AgeingBucket[];
  escalationsByLevel: EscalationBreakdown[];
  escalationsByTrigger: EscalationBreakdown[];
  trend: TrendPoint[];
  recurringCompliance: RecurringDefinitionDto[];
  missedCommitments: CommitmentDto[];
  noResponseTasks: TaskListItem[];
}

// ───────────────────────────── Search ─────────────────────────────

export interface SearchHit {
  /** "Task", "Person", "Team", "Department", "Comment", "Email", "Teams", "Commitment", "Project", "Tag", "Attachment"… */
  kind: string;
  id: Guid;
  title: string;
  snippet: string;
  taskId?: Guid | null;
  taskNumber?: string | null;
  at?: DateTimeUtc | null;
  route: string;
}

export interface SearchResult {
  query: string;
  hits: SearchHit[];
  counts: Record<string, number>;
}

// ───────────────────────────── Notifications ─────────────────────────────

export interface InboxItem {
  id: Guid;
  category: NotificationCategory;
  subject: string;
  body: string;
  taskId?: Guid | null;
  taskNumber?: string | null;
  createdAt: DateTimeUtc;
  readAt?: DateTimeUtc | null;
}

export interface NotificationRow {
  id: Guid;
  idempotencyKey: string;
  taskId?: Guid | null;
  taskNumber?: string | null;
  recipient?: string | null;
  channel: NotificationChannel;
  category: NotificationCategory;
  subject: string;
  status: NotificationStatus;
  attemptCount: number;
  scheduledAt: DateTimeUtc;
  lastAttemptAt?: DateTimeUtc | null;
  nextAttemptAt?: DateTimeUtc | null;
  sentAt?: DateTimeUtc | null;
  error?: string | null;
  createdAt: DateTimeUtc;
  expiresAt?: DateTimeUtc | null;
}

export interface UnreadCount {
  count: number;
}
