/**
 * Active-filter chips for the task list: TaskFilter → removable chips, and the inverse operations
 * (remove one chip, clear all criteria). Pure functions so they can be unit-tested.
 */
import { TaskFilter } from '../../../core/models/task.models';
import { formatDateOnly } from '../../../core/util/dates';
import { humanize } from '../../../core/util/humanize';
import { filterToQuery } from '../../../core/util/task-filter-query';

/** One removable chip. `value` is set for list criteria, where removing the chip drops just that entry. */
export interface FilterChip {
  key: keyof TaskFilter;
  value?: string;
  label: string;
}

/** Resolves ids in the filter to display names (people, teams, departments, projects). */
export interface FilterNameResolver {
  person(id: string): string;
  team(id: string): string;
  department(id: string): string;
  project(id: string): string;
}

/** Boolean criteria and how they read when true / false. */
const BOOLEAN_LABELS: Partial<Record<keyof TaskFilter, [string, string]>> = {
  recurring: ['Recurring', 'Not recurring'],
  overdue: ['Overdue', 'Not overdue'],
  blocked: ['Blocked', 'Not blocked'],
  waitingForCeo: ['Waiting for CEO', 'Not waiting for CEO'],
  noResponse: ['No response', 'Has responded'],
  awaitingApproval: ['Awaiting approval', 'Not awaiting approval'],
  topLevelOnly: ['Top-level only', 'Including subtasks'],
};

function named(prefix: string, name: string, id: string): string {
  return `${prefix}: ${name || 'Unknown (' + id.slice(0, 8) + ')'}`;
}

/**
 * Chips for every active criterion. Scope, the open/closed toggle, sort and paging have dedicated controls and are
 * not repeated as chips.
 */
export function describeFilter(f: TaskFilter, names: FilterNameResolver): FilterChip[] {
  const chips: FilterChip[] = [];
  if (f.text) chips.push({ key: 'text', label: `Text: “${f.text}”` });
  for (const s of f.statuses ?? []) chips.push({ key: 'statuses', value: s, label: `Status: ${humanize(s)}` });
  if (f.assignedToId) chips.push({ key: 'assignedToId', label: named('Responsible', names.person(f.assignedToId), f.assignedToId) });
  if (f.assignedById) chips.push({ key: 'assignedById', label: named('Delegated by', names.person(f.assignedById), f.assignedById) });
  if (f.teamId) chips.push({ key: 'teamId', label: named('Team', names.team(f.teamId), f.teamId) });
  if (f.departmentId) chips.push({ key: 'departmentId', label: named('Department', names.department(f.departmentId), f.departmentId) });
  if (f.projectId) chips.push({ key: 'projectId', label: named('Project', names.project(f.projectId), f.projectId) });
  for (const p of f.priorities ?? []) chips.push({ key: 'priorities', value: p, label: `Priority: ${humanize(p)}` });
  for (const c of f.criticalities ?? []) chips.push({ key: 'criticalities', value: c, label: `Criticality: ${humanize(c)}` });
  if (f.minCriticality) chips.push({ key: 'minCriticality', label: `Criticality ≥ ${humanize(f.minCriticality)}` });
  for (const h of f.health ?? []) chips.push({ key: 'health', value: h, label: `Health: ${humanize(h)}` });
  if (f.bucket) chips.push({ key: 'bucket', label: humanize(f.bucket) });
  if (f.dueFrom) chips.push({ key: 'dueFrom', label: `Due from ${formatDateOnly(f.dueFrom, { short: true })}` });
  if (f.dueTo) chips.push({ key: 'dueTo', label: `Due by ${formatDateOnly(f.dueTo, { short: true })}` });
  if (f.minDaysOverdue !== null && f.minDaysOverdue !== undefined) {
    chips.push({ key: 'minDaysOverdue', label: `Overdue ≥ ${f.minDaysOverdue} day${f.minDaysOverdue === 1 ? '' : 's'}` });
  }
  if (f.completedFrom) chips.push({ key: 'completedFrom', label: `Completed from ${formatDateOnly(f.completedFrom, { short: true })}` });
  if (f.completedTo) chips.push({ key: 'completedTo', label: `Completed by ${formatDateOnly(f.completedTo, { short: true })}` });
  if (f.tag) chips.push({ key: 'tag', label: `#${f.tag}` });
  for (const [key, [yes, no]] of Object.entries(BOOLEAN_LABELS) as [keyof TaskFilter, [string, string]][]) {
    const v = f[key];
    if (typeof v === 'boolean') chips.push({ key, label: v ? yes : no });
  }
  return chips;
}

/** The filter without the chip's criterion (or without one entry of a list criterion). Paging restarts. */
export function removeChip(f: TaskFilter, chip: FilterChip): TaskFilter {
  const next: Record<string, unknown> = { ...f, page: 1 };
  if (chip.value !== undefined) {
    const current = next[chip.key];
    const list = Array.isArray(current) ? (current as string[]) : [];
    const rest = list.filter((v) => v !== chip.value);
    if (rest.length) next[chip.key] = rest;
    else delete next[chip.key];
  } else {
    delete next[chip.key];
  }
  return next as TaskFilter;
}

/** Removes every criterion but keeps the scope, open/closed toggle, sort and page size. */
export function clearCriteria(f: TaskFilter): TaskFilter {
  const kept: TaskFilter = { page: 1 };
  if (f.scope) kept.scope = f.scope;
  if (typeof f.open === 'boolean') kept.open = f.open;
  if (f.sort) kept.sort = f.sort;
  if (f.pageSize) kept.pageSize = f.pageSize;
  return kept;
}

/** The filter as a saved view stores it: criteria, scope and sort, without paging. */
export function viewCriteria(f: TaskFilter): TaskFilter {
  const copy: TaskFilter = { ...f };
  delete copy.page;
  delete copy.pageSize;
  return copy;
}

/** True when two filters mean the same thing in the URL (paging ignored). Used to highlight the active saved view. */
export function sameCriteria(a: TaskFilter, b: TaskFilter): boolean {
  const qa = filterToQuery(viewCriteria(a));
  const qb = filterToQuery(viewCriteria(b));
  const ka = Object.keys(qa).sort();
  const kb = Object.keys(qb).sort();
  return ka.length === kb.length && ka.every((k, i) => k === kb[i] && qa[k] === qb[k]);
}
