/**
 * Primitive aliases and small shared records.
 * Wire formats (docs/API.md): Guid → string, DateOnly → "yyyy-MM-dd", TimeOnly → "HH:mm:ss",
 * DateTime → ISO-8601 UTC with "Z".
 */
export type Guid = string;
/** "yyyy-MM-dd" */
export type DateOnly = string;
/** "HH:mm:ss" (the client also accepts "HH:mm") */
export type TimeOnly = string;
/** ISO-8601 UTC, e.g. "2026-10-06T08:30:00Z" */
export type DateTimeUtc = string;

export interface PersonRef {
  id: Guid;
  name: string;
  title?: string | null;
  email?: string | null;
}

export interface NamedRef {
  id: Guid;
  name: string;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

/** Where a number or sentence drills down to in the UI: a route plus query parameters (spec §56). */
export interface Drilldown {
  route: string;
  query: Record<string, string>;
}

/** Tone strings used by tiles and summary points. Unknown values fall back to neutral. */
export type Tone = 'neutral' | 'good' | 'info' | 'warning' | 'danger' | string;

export interface Tile {
  key: string;
  label: string;
  value: number;
  tone: Tone;
  drilldown: Drilldown;
  hint?: string | null;
}

/** RFC 7807 problem+json with the API's stable `code`. */
export interface ProblemDetails {
  status?: number;
  title?: string;
  detail?: string;
  code?: string;
  type?: string;
  errors?: Record<string, string[]>;
}

export interface QueuedResult {
  queued: number | boolean;
}
