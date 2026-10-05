/**
 * Delegate-dialog form model: the typed task form, its mapping to CreateTaskRequest, and the conversions between a
 * parsed natural-language draft (ResolvedDraft), the task form and the recurring-definition form.
 */
import { NonNullableFormBuilder, Validators } from '@angular/forms';
import { DateOnly, Guid } from '../../core/models/common.models';
import { Criticality, TaskPriority, TaskSource, TaskVisibility } from '../../core/models/enums';
import { FollowUpSchedule, NO_FOLLOW_UP } from '../../core/models/rules.models';
import { CreateTaskRequest, DependencyInput, TaskLink } from '../../core/models/task.models';
import { RecurringDefinitionInput, ResolvedDraft } from '../../core/models/view.models';
import { toTimeInput, toTimeOnly } from '../../core/util/dates';
import { DEFAULT_FOLLOW_UP } from '../recurring/recurring-form.model';

/** A supporting link row. */
export function createLinkRow(fb: NonNullableFormBuilder, link?: Partial<TaskLink>) {
  return fb.group({
    title: fb.control(link?.title ?? ''),
    url: fb.control(link?.url ?? '', { validators: [Validators.pattern(/^\s*https?:\/\/\S+\s*$/i)] }),
  });
}
export type LinkRow = ReturnType<typeof createLinkRow>;

/** A dependency row: either another task (finish-to-start) or an external dependency. */
export interface DependencySeed {
  taskId?: Guid | null;
  /** Display label for a task dependency ("TASK-2026-0042 · Dealer CRM API"). */
  label?: string;
}

export function createDependencyRow(fb: NonNullableFormBuilder, seed: DependencySeed = {}) {
  return fb.group({
    taskId: fb.control<Guid | null>(seed.taskId ?? null),
    label: fb.control(seed.label ?? ''),
    externalDescription: fb.control(''),
    externalParty: fb.control(''),
    expectedBy: fb.control<DateOnly | null>(null),
  });
}
export type DependencyRow = ReturnType<typeof createDependencyRow>;

/** The structured delegation form (spec §6). Times are "HH:mm" for `<input type="time">`. */
export function createTaskForm(fb: NonNullableFormBuilder) {
  return fb.group({
    // Essentials
    title: fb.control('', { validators: [Validators.required, Validators.maxLength(300)] }),
    primaryAssigneeId: fb.control<Guid | null>(null),
    additionalAssigneeIds: fb.control<Guid[]>([]),
    teamIds: fb.control<Guid[]>([]),
    includeTeamLeader: fb.control(true),
    dueDate: fb.control<DateOnly | null>(null),
    dueTime: fb.control(''),
    priority: fb.control<TaskPriority>('Normal'),
    criticality: fb.control<Criticality>('Normal'),
    // Details
    description: fb.control(''),
    objective: fb.control(''),
    expectedResult: fb.control(''),
    deliverable: fb.control(''),
    successCriteria: fb.control(''),
    // People
    supportingMemberIds: fb.control<Guid[]>([]),
    followerIds: fb.control<Guid[]>([]),
    accountableManagerId: fb.control<Guid | null>(null),
    // Classification
    departmentId: fb.control<Guid | null>(null),
    projectId: fb.control<Guid | null>(null),
    categoryId: fb.control<Guid | null>(null),
    tags: fb.control<string[]>([]),
    visibility: fb.control<TaskVisibility>('Normal'),
    // Dates
    startDate: fb.control<DateOnly | null>(null),
    hardDeadline: fb.control<DateOnly | null>(null),
    reviewDate: fb.control<DateOnly | null>(null),
    // Follow-up
    followUp: fb.control<FollowUpSchedule>({ ...DEFAULT_FOLLOW_UP }),
    // Controls — null means "use the server default" (e.g. acknowledgement from Critical upwards).
    requiresApproval: fb.control<boolean | null>(null),
    requiresAcknowledgement: fb.control<boolean | null>(null),
    notifyCeoOnDelay: fb.control(false),
    customerImpact: fb.control(false),
    customerName: fb.control(''),
    financialImpact: fb.control<number | null>(null),
    escalationPolicyId: fb.control<Guid | null>(null),
    // Repeating rows
    dependencies: fb.array<DependencyRow>([]),
    links: fb.array<LinkRow>([]),
  });
}

export type TaskForm = ReturnType<typeof createTaskForm>;
export type TaskFormValue = ReturnType<TaskForm['getRawValue']>;

function textOrNull(value: string | null | undefined): string | null {
  const t = (value ?? '').trim();
  return t ? t : null;
}

function uniqueIds(ids: readonly (Guid | null | undefined)[]): Guid[] {
  return [...new Set(ids.filter((x): x is Guid => !!x))];
}

/** Drops undefined keys so a partial patch never overwrites a control with undefined. */
function defined<T extends object>(obj: T): Partial<T> {
  return Object.fromEntries(Object.entries(obj).filter(([, v]) => v !== undefined)) as Partial<T>;
}

function toNumberOrNull(value: unknown): number | null {
  if (value === null || value === undefined || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

export interface TaskRequestContext {
  parentTaskId?: Guid | null;
  isDraft: boolean;
  source: TaskSource;
  attachmentIds: Guid[];
}

/** Form value → POST /api/tasks body. */
export function taskRequestFromForm(v: TaskFormValue, ctx: TaskRequestContext): CreateTaskRequest {
  const primary = v.primaryAssigneeId ?? null;
  const teamIds = uniqueIds(v.teamIds ?? []);
  const dependencies: DependencyInput[] = [];
  for (const d of v.dependencies ?? []) {
    if (d.taskId) dependencies.push({ taskId: d.taskId, type: 'FinishToStart' });
    else if (d.externalDescription.trim()) {
      dependencies.push({
        type: 'External',
        externalDescription: d.externalDescription.trim(),
        externalParty: textOrNull(d.externalParty),
        expectedBy: d.expectedBy || null,
      });
    }
  }
  return {
    title: (v.title ?? '').trim(),
    description: textOrNull(v.description),
    objective: textOrNull(v.objective),
    expectedResult: textOrNull(v.expectedResult),
    deliverable: textOrNull(v.deliverable),
    successCriteria: textOrNull(v.successCriteria),
    parentTaskId: ctx.parentTaskId ?? null,
    departmentId: v.departmentId ?? null,
    projectId: v.projectId ?? null,
    categoryId: v.categoryId ?? null,
    primaryAssigneeId: primary,
    additionalAssigneeIds: uniqueIds(v.additionalAssigneeIds ?? []).filter((id) => id !== primary),
    teamIds,
    includeTeamLeader: teamIds.length > 0 && !!v.includeTeamLeader,
    supportingMemberIds: uniqueIds(v.supportingMemberIds ?? []),
    followerIds: uniqueIds(v.followerIds ?? []),
    accountableManagerId: v.accountableManagerId ?? null,
    tags: [...new Set((v.tags ?? []).map((t) => t.trim()).filter(Boolean))],
    priority: v.priority,
    criticality: v.criticality,
    visibility: v.visibility,
    startDate: v.startDate || null,
    dueDate: v.dueDate || null,
    dueTime: v.dueDate ? toTimeOnly(v.dueTime) : null,
    hardDeadline: v.hardDeadline || null,
    reviewDate: v.reviewDate || null,
    followUp: v.followUp ?? { ...NO_FOLLOW_UP },
    requiresApproval: v.requiresApproval,
    requiresAcknowledgement: v.requiresAcknowledgement,
    links: (v.links ?? [])
      .filter((l) => l.url.trim())
      .map((l) => ({ title: l.title.trim() || l.url.trim(), url: l.url.trim() })),
    dependencies,
    customerImpact: !!v.customerImpact,
    customerName: v.customerImpact ? textOrNull(v.customerName) : null,
    financialImpact: toNumberOrNull(v.financialImpact),
    notifyCeoOnDelay: !!v.notifyCeoOnDelay,
    escalationPolicyId: v.escalationPolicyId ?? null,
    isDraft: ctx.isDraft,
    source: ctx.source,
    attachmentIds: ctx.attachmentIds,
  };
}

/** Problems that block submission (the server re-validates). A draft only needs a title. */
export function taskProblems(v: TaskFormValue, asDraft: boolean): string[] {
  const problems: string[] = [];
  if (!(v.title ?? '').trim()) problems.push('Give the task a title.');
  if (!asDraft && !v.primaryAssigneeId && !(v.teamIds ?? []).length) {
    problems.push('Choose the primary responsible person or a team (its leader becomes responsible).');
  }
  if ((v.links ?? []).some((l) => l.url.trim() && !/^https?:\/\/\S+$/i.test(l.url.trim()))) {
    problems.push('Supporting links need a full address starting with http:// or https://.');
  }
  if (v.dueDate && v.hardDeadline && v.hardDeadline < v.dueDate) problems.push('The hard deadline is before the due date.');
  if (v.startDate && v.dueDate && v.dueDate < v.startDate) problems.push('The due date is before the start date.');
  return problems;
}

/** Ids of the people whose workload matters for this delegation (primary first). */
export function assigneeIdsOf(primary: Guid | null | undefined, additional: readonly Guid[] | null | undefined): Guid[] {
  return uniqueIds([primary, ...(additional ?? [])]);
}

/** Partial task-form patch from a parsed draft. Fields the draft does not mention keep their current value. */
export function taskPatchFromDraft(r: ResolvedDraft): Partial<TaskFormValue> {
  const d = r.draft;
  const primary = d.primaryAssigneeId ?? r.primaryAssignee?.id ?? null;
  const assignees = uniqueIds([...(d.assigneeIds ?? []), ...(r.assignees ?? []).map((p) => p.id)]).filter((id) => id !== primary);
  const teams = (d.teamIds ?? []).length ? d.teamIds : (r.teams ?? []).map((t) => t.id);
  return defined({
    title: d.title ?? '',
    description: d.description || undefined,
    primaryAssigneeId: primary,
    additionalAssigneeIds: assignees,
    teamIds: [...teams],
    departmentId: d.departmentId ?? r.department?.id ?? undefined,
    projectId: d.projectId ?? r.project?.id ?? undefined,
    startDate: d.startDate ?? undefined,
    dueDate: d.dueDate ?? null,
    dueTime: toTimeInput(d.dueTime),
    followUp: d.followUp ? { ...NO_FOLLOW_UP, ...d.followUp } : undefined,
    priority: d.priority ?? 'Normal',
    criticality: d.criticality ?? 'Normal',
    notifyCeoOnDelay: !!d.notifyCeoOnDelay,
    requiresApproval: d.requiresApproval ? true : undefined,
  });
}

/** Recurring-definition overrides from a parsed recurring draft ("every Monday…", "1st of every month…"). */
export function recurringOverridesFromDraft(r: ResolvedDraft): Partial<RecurringDefinitionInput> {
  const d = r.draft;
  const primary = d.primaryAssigneeId ?? r.primaryAssignee?.id ?? null;
  const team = (d.teamIds ?? [])[0] ?? r.teams?.[0]?.id ?? null;
  return defined({
    titleTemplate: d.title ?? '',
    description: d.description || undefined,
    primaryAssigneeId: primary,
    additionalAssigneeIds: uniqueIds([...(d.assigneeIds ?? []), ...(r.assignees ?? []).map((p) => p.id)]).filter((id) => id !== primary),
    teamId: team,
    departmentId: d.departmentId ?? r.department?.id ?? undefined,
    projectId: d.projectId ?? r.project?.id ?? undefined,
    priority: d.priority ?? 'Normal',
    criticality: d.criticality ?? 'Normal',
    rule: d.recurrence ?? undefined,
    due: d.recurrenceDue ?? undefined,
    followUp: d.followUp ?? undefined,
    requiresApproval: d.requiresApproval ? true : undefined,
    startDate: d.startDate ?? undefined,
  });
}

/** Carries the shared fields from the task form into the recurring form when "Recurring" is switched on. */
export function recurringOverridesFromTask(v: TaskFormValue): Partial<RecurringDefinitionInput> {
  const primary = v.primaryAssigneeId ?? null;
  return defined({
    titleTemplate: v.title.trim() || undefined,
    description: v.description.trim() || undefined,
    primaryAssigneeId: primary ?? undefined,
    additionalAssigneeIds: (v.additionalAssigneeIds ?? []).length ? uniqueIds(v.additionalAssigneeIds).filter((id) => id !== primary) : undefined,
    teamId: (v.teamIds ?? [])[0] ?? undefined,
    departmentId: v.departmentId ?? undefined,
    projectId: v.projectId ?? undefined,
    categoryId: v.categoryId ?? undefined,
    priority: v.priority,
    criticality: v.criticality,
    visibility: v.visibility,
    followUp: v.followUp,
    escalationPolicyId: v.escalationPolicyId ?? undefined,
  });
}

/** Carries the shared fields back from the recurring form when "Recurring" is switched off. */
export function taskPatchFromRecurring(input: RecurringDefinitionInput): Partial<TaskFormValue> {
  return defined({
    title: input.titleTemplate || undefined,
    description: input.description || undefined,
    primaryAssigneeId: input.primaryAssigneeId ?? undefined,
    additionalAssigneeIds: input.additionalAssigneeIds.length ? [...input.additionalAssigneeIds] : undefined,
    teamIds: input.teamId ? [input.teamId] : undefined,
    departmentId: input.departmentId ?? undefined,
    projectId: input.projectId ?? undefined,
    categoryId: input.categoryId ?? undefined,
    priority: input.priority,
    criticality: input.criticality,
    visibility: input.visibility,
    followUp: input.followUp ?? undefined,
  });
}
