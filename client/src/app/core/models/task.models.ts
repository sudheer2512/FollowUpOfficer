/** TypeScript mirror of src/FollowUp.Core/Contracts/TaskContracts.cs (camelCase JSON). */
import { DateOnly, DateTimeUtc, Guid, NamedRef, PersonRef, TimeOnly } from './common.models';
import {
  AcknowledgementResponse,
  AcknowledgementState,
  ApprovalAction,
  ApprovalDecision,
  ApproverRole,
  AssigneeRole,
  ChangeSource,
  CommitmentStatus,
  CommunicationChannel,
  CommunicationSource,
  Criticality,
  DeadlineBucket,
  DependencyType,
  EscalationTarget,
  EscalationTrigger,
  EvidenceKind,
  ExtensionStatus,
  FollowUpPauseReason,
  LinkStatus,
  MatchMethod,
  MessageCategory,
  SuggestionKind,
  SuggestionStatus,
  TaskHealth,
  TaskPriority,
  TaskSource,
  TaskVisibility,
  WorkStatus,
} from './enums';
import { FollowUpSchedule } from './rules.models';

// ───────────────────────────── Domain value records ─────────────────────────────

export interface TaskLink {
  title: string;
  url: string;
}

export interface ScoreFactor {
  code: string;
  label: string;
  points: number;
}

// ───────────────────────────── Commands ─────────────────────────────

export interface DependencyInput {
  taskId?: Guid | null;
  type?: DependencyType;
  externalDescription?: string | null;
  externalParty?: string | null;
  expectedBy?: DateOnly | null;
}

/** Fast task delegation (spec §4, §5). */
export interface CreateTaskRequest {
  title: string;
  description?: string | null;
  objective?: string | null;
  expectedResult?: string | null;
  deliverable?: string | null;
  successCriteria?: string | null;
  parentTaskId?: Guid | null;
  departmentId?: Guid | null;
  teamId?: Guid | null;
  primaryAssigneeId?: Guid | null;
  additionalAssigneeIds?: Guid[];
  teamIds?: Guid[];
  includeTeamLeader?: boolean;
  supportingMemberIds?: Guid[];
  followerIds?: Guid[];
  accountableManagerId?: Guid | null;
  projectId?: Guid | null;
  categoryId?: Guid | null;
  tags?: string[];
  priority?: TaskPriority;
  criticality?: Criticality;
  visibility?: TaskVisibility;
  startDate?: DateOnly | null;
  dueDate?: DateOnly | null;
  dueTime?: TimeOnly | null;
  hardDeadline?: DateOnly | null;
  reviewDate?: DateOnly | null;
  followUp?: FollowUpSchedule | null;
  requiresApproval?: boolean | null;
  requiresAcknowledgement?: boolean | null;
  links?: TaskLink[];
  dependencies?: DependencyInput[];
  customerImpact?: boolean;
  customerName?: string | null;
  financialImpact?: number | null;
  notifyCeoOnDelay?: boolean;
  escalationPolicyId?: Guid | null;
  isDraft?: boolean;
  source?: TaskSource;
  meetingActionItemId?: Guid | null;
  sourceMessageId?: Guid | null;
  attachmentIds?: Guid[];
}

/** Editable task fields; null/undefined = unchanged. */
export interface UpdateTaskRequest {
  title?: string | null;
  description?: string | null;
  objective?: string | null;
  expectedResult?: string | null;
  deliverable?: string | null;
  successCriteria?: string | null;
  priority?: TaskPriority | null;
  criticality?: Criticality | null;
  visibility?: TaskVisibility | null;
  projectId?: Guid | null;
  categoryId?: Guid | null;
  departmentId?: Guid | null;
  teamId?: Guid | null;
  accountableManagerId?: Guid | null;
  startDate?: DateOnly | null;
  dueDate?: DateOnly | null;
  dueTime?: TimeOnly | null;
  clearDueTime?: boolean;
  hardDeadline?: DateOnly | null;
  reviewDate?: DateOnly | null;
  followUp?: FollowUpSchedule | null;
  requiresApproval?: boolean | null;
  customerImpact?: boolean | null;
  customerName?: string | null;
  financialImpact?: number | null;
  notifyCeoOnDelay?: boolean | null;
  escalationPolicyId?: Guid | null;
  tags?: string[] | null;
  links?: TaskLink[] | null;
  changeReason?: string | null;
}

export interface ReassignRequest {
  newPrimaryAssigneeId: Guid;
  note?: string | null;
  keepPreviousAsSupporting?: boolean;
}

export interface AssigneeChange {
  employeeId?: Guid | null;
  teamId?: Guid | null;
  role: AssigneeRole;
}

export interface AcknowledgeRequest {
  response: AcknowledgementResponse;
  comment?: string | null;
  proposedDueDate?: DateOnly | null;
}

export interface StatusChangeRequest {
  status: WorkStatus;
  reason?: string | null;
  progressPercent?: number | null;
}

export interface ProgressRequest {
  progressPercent: number;
  note?: string | null;
}

export interface CommentRequest {
  body: string;
}

export interface EvidenceInput {
  kind: EvidenceKind;
  title: string;
  url?: string | null;
  reference?: string | null;
  attachmentId?: Guid | null;
}

/** Assignee marks work completed (spec §30). */
export interface CompleteRequest {
  completionDate?: DateOnly | null;
  note?: string | null;
  evidence?: EvidenceInput[] | null;
  forceWithOpenSubtasks?: boolean;
}

export interface ApprovalRequest {
  action: ApprovalAction;
  comment?: string | null;
}

export interface ExtensionRequestInput {
  proposedDueDate: DateOnly;
  reason: string;
}

export interface ExtensionDecisionRequest {
  approve: boolean;
  note?: string | null;
  overrideDate?: DateOnly | null;
}

export interface SnoozeRequest {
  reason: FollowUpPauseReason;
  resumeOn: DateOnly;
  note?: string | null;
}

export interface ReminderRequest {
  email?: boolean;
  teams?: boolean;
  message?: string | null;
  requestUpdate?: boolean;
  includeTeamLeader?: boolean;
}

export interface EscalateRequest {
  /** [Flags] comma list, e.g. "TeamLeader, Ceo". */
  targets: EscalationTarget;
  note: string;
}

export interface LinkMessageRequest {
  messageId: Guid;
}

export interface CommitmentInput {
  employeeId?: Guid | null;
  commitmentDate: DateOnly;
  text: string;
}

// ───────────────────────────── Queries ─────────────────────────────

export type TaskScope = 'all' | 'mine' | 'delegated' | 'following' | 'team';
export type TaskSort = 'attention' | 'due' | 'updated' | 'number' | 'priority';

/** Task list filter (spec §52, §53). Serialisable so it can be stored as a saved view. */
export interface TaskFilter {
  text?: string | null;
  scope?: TaskScope | string | null;
  statuses?: WorkStatus[] | null;
  open?: boolean | null;
  assignedToId?: Guid | null;
  assignedById?: Guid | null;
  teamId?: Guid | null;
  departmentId?: Guid | null;
  projectId?: Guid | null;
  priorities?: TaskPriority[] | null;
  criticalities?: Criticality[] | null;
  minCriticality?: Criticality | null;
  health?: TaskHealth[] | null;
  bucket?: DeadlineBucket | null;
  dueFrom?: DateOnly | null;
  dueTo?: DateOnly | null;
  minDaysOverdue?: number | null;
  recurring?: boolean | null;
  overdue?: boolean | null;
  blocked?: boolean | null;
  waitingForCeo?: boolean | null;
  noResponse?: boolean | null;
  awaitingApproval?: boolean | null;
  topLevelOnly?: boolean | null;
  tag?: string | null;
  completedFrom?: DateOnly | null;
  completedTo?: DateOnly | null;
  sort?: TaskSort | string | null;
  page?: number;
  pageSize?: number;
}

export interface TaskListItem {
  id: Guid;
  number: string;
  title: string;
  status: WorkStatus;
  displayStatus: string;
  priority: TaskPriority;
  criticality: Criticality;
  health: TaskHealth;
  attentionScore: number;
  progressPercent: number;
  dueDate?: DateOnly | null;
  deadlineText: string;
  bucket: DeadlineBucket;
  isOverdue: boolean;
  daysOverdue: number;
  primaryAssignee?: PersonRef | null;
  delegator?: PersonRef | null;
  teamName?: string | null;
  departmentName?: string | null;
  projectName?: string | null;
  parentTaskId?: Guid | null;
  parentNumber?: string | null;
  isBlocked: boolean;
  blockedReason?: string | null;
  noResponseDays: number;
  lastActivityAt?: DateTimeUtc | null;
  nextFollowUpAt?: DateTimeUtc | null;
  isRecurring: boolean;
  subtaskCount: number;
  openSubtaskCount: number;
  escalationLevel: number;
  visibility: TaskVisibility;
  openCommitmentDate?: DateOnly | null;
  missedCommitments: number;
  lastUpdateSummary?: string | null;
  completedAt?: DateTimeUtc | null;
  tags: string[];
}

export interface AssigneeDto {
  id: Guid;
  employeeId?: Guid | null;
  employeeName?: string | null;
  teamId?: Guid | null;
  teamName?: string | null;
  role: AssigneeRole;
  assignedAt: DateTimeUtc;
}

export interface DependencyDto {
  id: Guid;
  type: DependencyType;
  taskId?: Guid | null;
  number?: string | null;
  title?: string | null;
  status?: WorkStatus | null;
  externalDescription?: string | null;
  externalParty?: string | null;
  expectedBy?: DateOnly | null;
  isResolved: boolean;
  isBlocking: boolean;
  /** "BlockedBy" / "Blocks" (direction relative to this task). */
  direction: string;
}

export interface CommitmentDto {
  id: Guid;
  taskId: Guid;
  taskNumber: string;
  taskTitle: string;
  person?: PersonRef | null;
  committedTo?: PersonRef | null;
  commitmentDate: DateOnly;
  what?: string | null;
  originalText: string;
  source: CommunicationSource;
  sourceMessageId?: Guid | null;
  confidence?: number | null;
  status: CommitmentStatus;
  createdAt: DateTimeUtc;
  resolvedAt?: DateTimeUtc | null;
}

export interface ApprovalDto {
  id: Guid;
  round: number;
  stage: number;
  role: ApproverRole;
  approver: PersonRef;
  decision: ApprovalDecision;
  comment?: string | null;
  requestedAt: DateTimeUtc;
  decidedAt?: DateTimeUtc | null;
  canDecide: boolean;
}

export interface ExtensionDto {
  id: Guid;
  requestedBy: PersonRef;
  originalDueDate?: DateOnly | null;
  currentDueDate?: DateOnly | null;
  proposedDueDate: DateOnly;
  reason: string;
  approver?: PersonRef | null;
  status: ExtensionStatus;
  decisionNote?: string | null;
  requestedAt: DateTimeUtc;
  decidedAt?: DateTimeUtc | null;
  source: CommunicationSource;
  canDecide: boolean;
}

export interface SuggestionDto {
  id: Guid;
  kind: SuggestionKind;
  suggestedStatus?: WorkStatus | null;
  suggestedDate?: DateOnly | null;
  detail?: string | null;
  /** 0–1 */
  confidence: number;
  explanation: string;
  status: SuggestionStatus;
  messageId?: Guid | null;
  createdAt: DateTimeUtc;
}

export interface DelegationDto {
  sequence: number;
  from?: PersonRef | null;
  to: PersonRef;
  delegatedAt: DateTimeUtc;
  note?: string | null;
}

export interface AttachmentDto {
  id: Guid;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedAt: DateTimeUtc;
  uploadedBy?: PersonRef | null;
  isEvidence: boolean;
}

export interface EvidenceDto {
  id: Guid;
  kind: EvidenceKind;
  title: string;
  url?: string | null;
  reference?: string | null;
  attachmentId?: Guid | null;
  addedAt: DateTimeUtc;
}

export interface SubmissionDto {
  id: Guid;
  round: number;
  submittedBy: PersonRef;
  submittedAt: DateTimeUtc;
  completionDate: DateOnly;
  note?: string | null;
  source: ChangeSource;
  evidence: EvidenceDto[];
}

export interface EscalationDto {
  id: Guid;
  level: number;
  trigger: EscalationTrigger;
  /** [Flags] comma list. */
  targets: EscalationTarget;
  reason: string;
  triggeredAt: DateTimeUtc;
  isManual: boolean;
}

export interface TimelineItem {
  at: DateTimeUtc;
  /** e.g. created, status, comment, email, teams, reminder, escalation, commitment, approval, extension, suggestion, audit… */
  kind: string;
  title: string;
  body?: string | null;
  actor?: PersonRef | null;
  channel?: string | null;
  refId?: Guid | null;
  meta?: Record<string, string> | null;
}

export interface LinkedMessageDto {
  linkId: Guid;
  messageId: Guid;
  channel: CommunicationChannel;
  subject?: string | null;
  preview: string;
  senderName?: string | null;
  sentAt: DateTimeUtc;
  method: MatchMethod;
  /** 0–1 */
  confidence: number;
  category: MessageCategory;
  explanation: string;
  status: LinkStatus;
  webUrl?: string | null;
}

export interface TaskPermissions {
  canView: boolean;
  canManage: boolean;
  canUpdate: boolean;
  canComplete: boolean;
  canAcknowledge: boolean;
  canApprove: boolean;
  canComment: boolean;
  canDecideExtension: boolean;
}

export interface FollowUpInfo {
  schedule: FollowUpSchedule;
  description: string;
  nextAt?: DateTimeUtc | null;
  lastAt?: DateTimeUtc | null;
  pausedUntil?: DateOnly | null;
  pauseReason?: FollowUpPauseReason | null;
  pauseNote?: string | null;
  remindersSinceResponse: number;
  lastReminderAt?: DateTimeUtc | null;
  noResponseSince?: DateTimeUtc | null;
}

export interface SubtaskNode {
  id: Guid;
  number: string;
  title: string;
  status: WorkStatus;
  displayStatus: string;
  assignee?: PersonRef | null;
  dueDate?: DateOnly | null;
  deadlineText: string;
  progressPercent: number;
  health: TaskHealth;
  depth: number;
  parentTaskId?: Guid | null;
  isBlocked: boolean;
  children: SubtaskNode[];
}

/** Full task view (detail page). */
export interface TaskDetail {
  summary: TaskListItem;
  description?: string | null;
  objective?: string | null;
  expectedResult?: string | null;
  deliverable?: string | null;
  successCriteria?: string | null;
  links: TaskLink[];
  originalDelegator?: PersonRef | null;
  accountableManager?: PersonRef | null;
  createdBy?: PersonRef | null;
  project?: NamedRef | null;
  category?: NamedRef | null;
  team?: NamedRef | null;
  department?: NamedRef | null;
  teamId?: Guid | null;
  departmentId?: Guid | null;
  projectId?: Guid | null;
  categoryId?: Guid | null;
  assignedAt?: DateTimeUtc | null;
  startDate?: DateOnly | null;
  dueTime?: TimeOnly | null;
  hardDeadline?: DateOnly | null;
  reviewDate?: DateOnly | null;
  originalDueDate?: DateOnly | null;
  dueDateChangeCount: number;
  deadlineAt?: DateTimeUtc | null;
  acknowledgement: AcknowledgementState;
  acknowledgedAt?: DateTimeUtc | null;
  acknowledgementComment?: string | null;
  requiresApproval: boolean;
  customerImpact: boolean;
  customerName?: string | null;
  financialImpact?: number | null;
  notifyCeoOnDelay: boolean;
  source: TaskSource;
  attentionFactors: ScoreFactor[];
  followUp: FollowUpInfo;
  assignees: AssigneeDto[];
  followers: PersonRef[];
  subtasks: SubtaskNode[];
  dependencies: DependencyDto[];
  commitments: CommitmentDto[];
  approvals: ApprovalDto[];
  extensions: ExtensionDto[];
  suggestions: SuggestionDto[];
  delegationChain: DelegationDto[];
  attachments: AttachmentDto[];
  submissions: SubmissionDto[];
  escalations: EscalationDto[];
  messages: LinkedMessageDto[];
  timeline: TimelineItem[];
  permissions: TaskPermissions;
  recurringDefinitionId?: Guid | null;
  recurringTitle?: string | null;
  occurrenceKey?: string | null;
  meetingId?: Guid | null;
  meetingTitle?: string | null;
  parent?: TaskListItem | null;
}

/** Workload of a person before delegation (spec §42). */
export interface WorkloadDto {
  employeeId: Guid;
  name: string;
  jobTitle?: string | null;
  openTasks: number;
  dueThisWeek: number;
  overdue: number;
  critical: number;
  blocked: number;
  noResponse: number;
  /** Load label from the server, e.g. "Light", "Moderate", "Heavy", "Overloaded". */
  load: string;
}
