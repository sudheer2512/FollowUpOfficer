/** TypeScript mirror of src/FollowUp.Core/Contracts/AdminContracts.cs plus auth/system responses from docs/API.md. */
import { DateOnly, DateTimeUtc, Guid, NamedRef, PersonRef, TimeOnly } from './common.models';
import {
  AppRole,
  ApproverRole,
  ChangeSource,
  Criticality,
  EscalationTarget,
  EscalationTrigger,
  HolidayKind,
  IntegrationErrorKind,
  IntegrationType,
  JobOutcome,
  JobScheduleKind,
  JobState,
  JobTrigger,
  MeetingActionStatus,
  MisfirePolicy,
  NotifyChannels,
  SyncState,
  TaskPriority,
  TaskVisibility,
  TeamMemberRole,
  ThresholdUnit,
  WorkStatus,
} from './enums';
import { FollowUpSchedule, TemplateItem } from './rules.models';
import { TaskFilter } from './task.models';

// ───────────────────────────── Auth ─────────────────────────────

/** The signed-in user (GET /api/auth/me). */
export interface MeDto {
  employeeId: Guid;
  name: string;
  email: string;
  jobTitle?: string | null;
  departmentId?: Guid | null;
  departmentName?: string | null;
  roles: string[];
  isExecutive: boolean;
  isAdministrator: boolean;
  isManager: boolean;
  teamIds: Guid[];
  ledTeamIds: Guid[];
  userName: string;
}

export interface DemoUser {
  userName: string;
  name: string;
  title: string;
  role: string;
}

export interface AuthConfig {
  entraEnabled: boolean;
  tenantId?: string | null;
  clientId?: string | null;
  apiScope?: string | null;
  demoMode: boolean;
  demoUsers: DemoUser[];
}

export interface LoginRequest {
  userName: string;
  password: string;
}

export interface SystemInfo {
  organisation: string;
  timeZone: string;
  mode: string;
  aiProvider: string;
  hosts: unknown[];
  version: string;
}

export interface HealthInfo {
  status: string;
  database: string;
  mode: string;
  jobsEnabled: boolean;
  serverTime: DateTimeUtc;
}

// ───────────────────────────── Organisation ─────────────────────────────

export interface DepartmentDto {
  id: Guid;
  code: string;
  name: string;
  head?: PersonRef | null;
  parentDepartmentId?: Guid | null;
  isActive: boolean;
  employeeCount: number;
  teamCount: number;
}

export interface DepartmentInput {
  code: string;
  name: string;
  headId?: Guid | null;
  parentDepartmentId?: Guid | null;
  isActive: boolean;
}

export interface TeamMemberDto {
  employeeId: Guid;
  name: string;
  jobTitle?: string | null;
  role: TeamMemberRole;
}

export interface TeamDto {
  id: Guid;
  name: string;
  department?: NamedRef | null;
  leader?: PersonRef | null;
  deputyLeader?: PersonRef | null;
  email?: string | null;
  teamsTeamId?: string | null;
  teamsChannelId?: string | null;
  teamsChannelName?: string | null;
  isActive: boolean;
  members: TeamMemberDto[];
}

export interface TeamInput {
  name: string;
  departmentId?: Guid | null;
  leaderId?: Guid | null;
  deputyLeaderId?: Guid | null;
  email?: string | null;
  teamsTeamId?: string | null;
  teamsChannelId?: string | null;
  teamsChannelName?: string | null;
  isActive: boolean;
  memberIds: Guid[];
}

export interface EmployeeDto {
  id: Guid;
  employeeCode: string;
  displayName: string;
  email: string;
  jobTitle?: string | null;
  phone?: string | null;
  department?: NamedRef | null;
  manager?: PersonRef | null;
  teams: NamedRef[];
  isActive: boolean;
  userName?: string | null;
  /** [Flags] comma list, e.g. "TeamMember, Executive". */
  roles: AppRole;
  hasLogin: boolean;
  loginEnabled: boolean;
  lastLoginAt?: DateTimeUtc | null;
  externalUserId?: string | null;
  /** [Flags] comma list, e.g. "Email, Teams, InApp". */
  preferredChannels: NotifyChannels;
}

export interface EmployeeInput {
  employeeCode: string;
  displayName: string;
  email: string;
  jobTitle?: string | null;
  phone?: string | null;
  departmentId?: Guid | null;
  managerId?: Guid | null;
  isActive: boolean;
  preferredChannels: NotifyChannels;
}

export interface AccountInput {
  userName: string;
  roles: AppRole;
  enabled: boolean;
  newPassword?: string | null;
}

export interface HolidayDto {
  id: Guid;
  date: DateOnly;
  name: string;
  kind: HolidayKind;
}

export interface CalendarDto {
  id: Guid;
  name: string;
  region?: string | null;
  timeZoneId: string;
  /** Comma list of weekday names, e.g. "Saturday,Sunday". */
  weekendDays: string;
  workdayStart: TimeOnly;
  workdayEnd: TimeOnly;
  isDefault: boolean;
  holidays: HolidayDto[];
}

export interface CalendarInput {
  name: string;
  region?: string | null;
  timeZoneId: string;
  weekendDays: string;
  workdayStart: TimeOnly;
  workdayEnd: TimeOnly;
  isDefault: boolean;
}

export interface HolidayInput {
  date: DateOnly;
  name: string;
  kind: HolidayKind;
}

/** Lookup lists for forms (GET /api/lookups). */
export interface PersonLookup {
  id: Guid;
  name: string;
  jobTitle?: string | null;
  email?: string | null;
  departmentId?: Guid | null;
  departmentName?: string | null;
  isExecutive: boolean;
}

export interface TeamLookup {
  id: Guid;
  name: string;
  departmentId?: Guid | null;
  leaderId?: Guid | null;
  memberIds: Guid[];
}

export interface Lookups {
  people: PersonLookup[];
  teams: TeamLookup[];
  departments: NamedRef[];
  projects: NamedRef[];
  categories: NamedRef[];
  escalationPolicies: NamedRef[];
  tags: string[];
  templates: NamedRef[];
}

// ───────────────────────────── Escalation policies ─────────────────────────────

export interface EscalationStepDto {
  id?: Guid | null;
  order: number;
  trigger: EscalationTrigger;
  threshold: number;
  unit: ThresholdUnit;
  targets: EscalationTarget;
  channels: NotifyChannels;
  level: number;
  raiseCeoAttention: boolean;
  description?: string | null;
}

export interface EscalationPolicyDto {
  id: Guid;
  name: string;
  description?: string | null;
  appliesTo: string;
  isDefault: boolean;
  isActive: boolean;
  steps: EscalationStepDto[];
}

export interface EscalationPolicyInput {
  name: string;
  description?: string | null;
  appliesTo: string;
  isDefault: boolean;
  isActive: boolean;
  steps: EscalationStepDto[];
}

// ───────────────────────────── Projects, meetings, templates, views ─────────────────────────────

export interface ProjectInput {
  code: string;
  name: string;
  description?: string | null;
  ownerId?: Guid | null;
  departmentId?: Guid | null;
  startDate?: DateOnly | null;
  targetDate?: DateOnly | null;
  visibility?: TaskVisibility;
  isActive?: boolean;
}

export interface ActionItemDto {
  id: Guid;
  decision?: string | null;
  actionText: string;
  owner?: PersonRef | null;
  dueDate?: DateOnly | null;
  criticality: Criticality;
  taskId?: Guid | null;
  taskNumber?: string | null;
  taskStatus?: WorkStatus | null;
  status: MeetingActionStatus;
}

export interface MeetingDto {
  id: Guid;
  title: string;
  heldAt: DateTimeUtc;
  organizer?: PersonRef | null;
  location?: string | null;
  notes?: string | null;
  project?: NamedRef | null;
  participants: PersonRef[];
  actionItems: ActionItemDto[];
}

export interface ActionItemInput {
  id?: Guid | null;
  decision?: string | null;
  actionText: string;
  ownerId?: Guid | null;
  dueDate?: DateOnly | null;
  criticality?: Criticality;
}

export interface MeetingInput {
  title: string;
  heldAt: DateTimeUtc;
  organizerId?: Guid | null;
  location?: string | null;
  notes?: string | null;
  projectId?: Guid | null;
  participantIds: Guid[];
  actionItems: ActionItemInput[];
}

export interface CreateActionTasksRequest {
  actionItemIds?: Guid[] | null;
  followUp?: FollowUpSchedule | null;
}

export interface TemplateDto {
  id: Guid;
  name: string;
  category: string;
  description?: string | null;
  priority: TaskPriority;
  criticality: Criticality;
  requiresApproval: boolean;
  escalationPolicyId?: Guid | null;
  followUp: FollowUpSchedule;
  roles: string[];
  items: TemplateItem[];
  approvalFlow: ApproverRole[];
  isActive: boolean;
}

export interface TemplateInput {
  name: string;
  category: string;
  description?: string | null;
  priority: TaskPriority;
  criticality: Criticality;
  requiresApproval: boolean;
  escalationPolicyId?: Guid | null;
  followUp?: FollowUpSchedule | null;
  roles: string[];
  items: TemplateItem[];
  approvalFlow: ApproverRole[];
  isActive?: boolean;
}

/** Instantiate a template: role placeholders → people, anchor date for due offsets. */
export interface InstantiateTemplateRequest {
  title?: string | null;
  anchorDate?: DateOnly | null;
  roleAssignments: Record<string, Guid>;
  projectId?: Guid | null;
  teamId?: Guid | null;
  departmentId?: Guid | null;
  criticality?: Criticality | null;
}

export interface SavedViewDto {
  id: Guid;
  name: string;
  filter: TaskFilter;
  isShared: boolean;
  isMine: boolean;
}

export interface SavedViewInput {
  name: string;
  filter: TaskFilter;
  isShared: boolean;
}

// ───────────────────────────── Integration monitor (spec §67) ─────────────────────────────

/** Connection health of the Microsoft 365 integration (Abstractions/Ports.cs IntegrationProbe). */
export interface IntegrationProbe {
  mode: string;
  authenticated: boolean;
  tenantId?: string | null;
  account?: string | null;
  grantedPermissions: string[];
  missingPermissions: string[];
  error?: string | null;
  checkedAt: DateTimeUtc;
}

export interface CheckpointDto {
  id: Guid;
  type: IntegrationType;
  sourceKey: string;
  displayName: string;
  enabled: boolean;
  status: SyncState;
  lastSuccessfulSyncAt?: DateTimeUtc | null;
  lastAttemptAt?: DateTimeUtc | null;
  nextRunAt?: DateTimeUtc | null;
  lastProcessedAt?: DateTimeUtc | null;
  lastProcessedExternalId?: string | null;
  hasDeltaToken: boolean;
  midPagination: boolean;
  error?: string | null;
  retryCount: number;
  itemsImportedTotal: number;
  lastRunItemCount: number;
  pendingMessages: number;
}

export interface JobDto {
  key: string;
  name: string;
  description: string;
  scheduleKind: JobScheduleKind;
  intervalSeconds: number;
  timeOfDay?: TimeOnly | null;
  dayOfWeek?: string | null;
  misfire: MisfirePolicy;
  enabled: boolean;
  state: JobState;
  nextRunAt?: DateTimeUtc | null;
  lastStartedAt?: DateTimeUtc | null;
  lastSucceededAt?: DateTimeUtc | null;
  lastFailedAt?: DateTimeUtc | null;
  lastError?: string | null;
  consecutiveFailures: number;
  leaseOwner?: string | null;
  leaseExpiresAt?: DateTimeUtc | null;
  checkpoint?: string | null;
  scheduleText: string;
}

export interface JobExecutionDto {
  id: Guid;
  jobKey: string;
  trigger: JobTrigger;
  host: string;
  startedAt: DateTimeUtc;
  finishedAt?: DateTimeUtc | null;
  outcome: JobOutcome;
  itemsProcessed: number;
  message?: string | null;
  error?: string | null;
  dueAt?: DateTimeUtc | null;
  durationMs?: number | null;
}

export interface IntegrationErrorDto {
  id: Guid;
  integration: string;
  sourceKey?: string | null;
  operation: string;
  kind: IntegrationErrorKind;
  statusCode?: number | null;
  message: string;
  retryAfterSeconds?: number | null;
  occurredAt: DateTimeUtc;
  resolved: boolean;
}

export interface HostHeartbeat {
  host: string;
  lastSeen: DateTimeUtc;
  executions24h: number;
}

export interface MonitorOverview {
  probe?: IntegrationProbe | null;
  /** "Emulator", "Graph", "Disabled"… */
  mode: string;
  checkpoints: CheckpointDto[];
  jobs: JobDto[];
  messagesAwaitingProcessing: number;
  messagesNeedingReview: number;
  messagesFailed: number;
  notificationsPending: number;
  notificationsFailed: number;
  notificationsDeadLetter: number;
  failedApiCalls24h: number;
  authProblems24h: number;
  rateLimitEvents24h: number;
  permissionErrors24h: number;
  resyncEvents24h: number;
  recentErrors: IntegrationErrorDto[];
  recentExecutions: JobExecutionDto[];
  hosts: HostHeartbeat[];
  serverTime: DateTimeUtc;
}

export interface AuditEntryDto {
  id: Guid;
  at: DateTimeUtc;
  actorName: string;
  action: string;
  entityType: string;
  entityId?: Guid | null;
  taskId?: Guid | null;
  taskNumber?: string | null;
  field?: string | null;
  oldValue?: string | null;
  newValue?: string | null;
  details?: string | null;
  source: ChangeSource;
}

export interface AuditQuery {
  taskId?: Guid | null;
  entityType?: string | null;
  action?: string | null;
  from?: DateOnly | null;
  to?: DateOnly | null;
  page?: number;
  pageSize?: number;
}

export interface AuditVerifyResult {
  ok: boolean;
  brokenEntryId?: Guid | null;
}

/** Runtime-editable settings. */
export interface RuntimeSettings {
  severelyOverdueDays: number;
  recentUpdateWindowHours: number;
  maxRemindersPerRecipientPerDay: number;
  reminderExpiryHours: number;
  /** 0–1 */
  autoAttachConfidence: number;
  /** 0–1 */
  suggestConfidence: number;
  dailyDigestEnabled: boolean;
  dailyDigestTime: TimeOnly;
  weeklyReviewEnabled: boolean;
  calendarSyncEnabled: boolean;
  acknowledgementFrom: Criticality;
  repeatedNonResponseReminders: number;
}
