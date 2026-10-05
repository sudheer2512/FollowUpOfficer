/**
 * TaskFilter ↔ URL query string mapping. The query keys are exactly the camelCase TaskFilter property names,
 * so server drilldowns ({ route: "/tasks", query: { bucket: "Overdue" } }) land on a correctly filtered list.
 * List values are comma separated ("statuses=InProgress,Blocked"). Singular aliases (status, priority,
 * criticality) are accepted on input for robustness.
 */
import { TaskFilter } from '../models/task.models';

export const DEFAULT_PAGE_SIZE = 50;

type FilterKey = keyof TaskFilter;

const STRING_KEYS: FilterKey[] = [
  'text', 'scope', 'assignedToId', 'assignedById', 'teamId', 'departmentId', 'projectId', 'minCriticality', 'bucket',
  'dueFrom', 'dueTo', 'tag', 'completedFrom', 'completedTo', 'sort',
];
const LIST_KEYS: FilterKey[] = ['statuses', 'priorities', 'criticalities', 'health'];
const BOOL_KEYS: FilterKey[] = [
  'open', 'recurring', 'overdue', 'blocked', 'waitingForCeo', 'noResponse', 'awaitingApproval', 'topLevelOnly',
];
const INT_KEYS: FilterKey[] = ['minDaysOverdue', 'page', 'pageSize'];

const ALIASES: Record<string, FilterKey> = {
  status: 'statuses',
  priority: 'priorities',
  criticality: 'criticalities',
  healths: 'health',
  q: 'text',
  search: 'text',
  assigneeId: 'assignedToId',
  delegatorId: 'assignedById',
};

/** Every query key the task list understands (used to tell filter keys from unrelated params). */
export const TASK_FILTER_QUERY_KEYS: string[] = [...STRING_KEYS, ...LIST_KEYS, ...BOOL_KEYS, ...INT_KEYS].map(String);

export type QueryInput = Record<string, string | string[] | null | undefined>;

function first(v: string | string[] | null | undefined): string | undefined {
  if (v === null || v === undefined) return undefined;
  return Array.isArray(v) ? v[0] : v;
}

function list(v: string | string[] | null | undefined): string[] {
  if (v === null || v === undefined) return [];
  const raw = Array.isArray(v) ? v : [v];
  return raw.flatMap((s) => s.split(',')).map((s) => s.trim()).filter(Boolean);
}

function bool(v: string | undefined): boolean | undefined {
  if (v === undefined || v === '') return undefined;
  const s = v.toLowerCase();
  if (s === 'true' || s === '1' || s === 'yes') return true;
  if (s === 'false' || s === '0' || s === 'no') return false;
  return undefined;
}

/** Parses router query params into a TaskFilter (unknown keys are ignored; page defaults to 1). */
export function queryToFilter(query: QueryInput): TaskFilter {
  const normalised: QueryInput = {};
  for (const [k, v] of Object.entries(query)) {
    const key = (ALIASES[k] as string | undefined) ?? k;
    if (normalised[key] === undefined) normalised[key] = v;
    else normalised[key] = [...list(normalised[key]), ...list(v)];
  }
  const filter: Record<string, unknown> = {};
  for (const k of STRING_KEYS) {
    const v = first(normalised[k]);
    if (v !== undefined && v !== '') filter[k] = v;
  }
  for (const k of LIST_KEYS) {
    const v = list(normalised[k]);
    if (v.length) filter[k] = v;
  }
  for (const k of BOOL_KEYS) {
    const v = bool(first(normalised[k]));
    if (v !== undefined) filter[k] = v;
  }
  for (const k of INT_KEYS) {
    const v = first(normalised[k]);
    if (v !== undefined && v !== '' && !isNaN(Number(v))) filter[k] = Math.trunc(Number(v));
  }
  const result = filter as TaskFilter;
  result.page = result.page && result.page > 0 ? result.page : 1;
  result.pageSize = result.pageSize && result.pageSize > 0 ? result.pageSize : DEFAULT_PAGE_SIZE;
  return result;
}

/**
 * Serialises a TaskFilter to query params. Empty values, page 1 and the default page size are omitted
 * so URLs stay short and shareable.
 */
export function filterToQuery(filter: TaskFilter): Record<string, string> {
  const q: Record<string, string> = {};
  const f = filter as Record<string, unknown>;
  for (const k of STRING_KEYS) {
    const v = f[k];
    if (typeof v === 'string' && v.trim() !== '') q[k] = v.trim();
  }
  for (const k of LIST_KEYS) {
    const v = f[k];
    if (Array.isArray(v) && v.length) q[k] = v.join(',');
  }
  for (const k of BOOL_KEYS) {
    const v = f[k];
    if (typeof v === 'boolean') q[k] = String(v);
  }
  for (const k of INT_KEYS) {
    const v = f[k];
    if (typeof v === 'number' && !isNaN(v)) {
      if (k === 'page' && v <= 1) continue;
      if (k === 'pageSize' && v === DEFAULT_PAGE_SIZE) continue;
      q[k] = String(v);
    }
  }
  return q;
}

/** True when the filter has no criteria beyond paging and sort. */
export function isEmptyFilter(filter: TaskFilter): boolean {
  const q = filterToQuery(filter);
  delete q['page'];
  delete q['pageSize'];
  delete q['sort'];
  return Object.keys(q).length === 0;
}

/** Number of active criteria (for the "Filters (3)" badge). */
export function activeFilterCount(filter: TaskFilter): number {
  const q = filterToQuery(filter);
  delete q['page'];
  delete q['pageSize'];
  delete q['sort'];
  delete q['text'];
  return Object.keys(q).length;
}

/** Builds the request body for POST /api/tasks/query (drops undefined, keeps paging). */
export function toQueryBody(filter: TaskFilter): TaskFilter {
  const body: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(filter)) {
    if (v === undefined || v === null || v === '') continue;
    if (Array.isArray(v) && v.length === 0) continue;
    body[k] = v;
  }
  body['page'] = filter.page ?? 1;
  body['pageSize'] = filter.pageSize ?? DEFAULT_PAGE_SIZE;
  return body as TaskFilter;
}
