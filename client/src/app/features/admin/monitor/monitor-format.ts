/**
 * Pure helpers for the integration monitor: status → tone mappings, durations and sync aggregates.
 * Tones are the design-system suffixes used as `badge tone-…` / `kpi tone-…`.
 */
import { CheckpointDto } from '../../../core/models/admin.models';
import { DateTimeUtc } from '../../../core/models/common.models';
import { IntegrationType } from '../../../core/models/enums';
import { UiTone } from '../../../core/util/tone';

export function syncStateTone(state: string | null | undefined): UiTone {
  switch (state) {
    case 'Succeeded':
      return 'good';
    case 'Running':
      return 'info';
    case 'Failed':
    case 'AuthError':
      return 'danger';
    case 'Throttled':
      return 'warning';
    default:
      return 'neutral';
  }
}

export function jobStateTone(state: string | null | undefined): UiTone {
  switch (state) {
    case 'Running':
      return 'info';
    case 'Failed':
      return 'danger';
    default:
      return 'neutral';
  }
}

export function jobTriggerTone(trigger: string | null | undefined): UiTone {
  switch (trigger) {
    case 'CatchUp':
      return 'warning';
    case 'Manual':
      return 'info';
    case 'Startup':
      return 'accent';
    default:
      return 'neutral';
  }
}

export function jobOutcomeTone(outcome: string | null | undefined): UiTone {
  switch (outcome) {
    case 'Succeeded':
      return 'good';
    case 'Running':
      return 'info';
    case 'Failed':
    case 'TimedOut':
      return 'danger';
    case 'Abandoned':
      return 'orange';
    default:
      return 'neutral';
  }
}

export function errorKindTone(kind: string | null | undefined): UiTone {
  switch (kind) {
    case 'Authentication':
    case 'Permission':
      return 'danger';
    case 'Throttled':
    case 'Transient':
      return 'warning';
    case 'Gone':
      return 'orange';
    default:
      return 'neutral';
  }
}

export function notificationStatusTone(status: string | null | undefined): UiTone {
  switch (status) {
    case 'Pending':
    case 'Sending':
      return 'info';
    case 'Sent':
      return 'good';
    case 'Failed':
    case 'DeadLetter':
      return 'danger';
    case 'Expired':
      return 'warning';
    default:
      return 'neutral';
  }
}

/** 850 → "850 ms", 1234 → "1.2 s", 125000 → "2 min 5 s". */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || isNaN(ms) || ms < 0) return '—';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s < 10 ? s.toFixed(1) : Math.round(s)} s`;
  const min = Math.floor(s / 60);
  const rest = Math.round(s % 60);
  return rest ? `${min} min ${rest} s` : `${min} min`;
}

/** Milliseconds since an ISO instant (relative to `now`), or null when absent / invalid. */
export function ageMs(value: DateTimeUtc | null | undefined, now: number = Date.now()): number | null {
  if (!value) return null;
  const t = new Date(value).getTime();
  return isNaN(t) ? null : now - t;
}

/** Severity order used to pick the "worst" status of a group of checkpoints. */
const SYNC_SEVERITY: Record<string, number> = {
  AuthError: 6,
  Failed: 5,
  Throttled: 4,
  Running: 2,
  Idle: 1,
  Succeeded: 0,
};

export interface SyncAggregate {
  /** Worst status among enabled checkpoints ("Disabled" when none is enabled, "None" when there are none). */
  status: string;
  tone: UiTone;
  total: number;
  enabled: number;
  lastSuccess: DateTimeUtc | null;
  backlog: number;
}

/** Rolls checkpoints of the given integration types up into one status card. */
export function aggregateSync(checkpoints: readonly CheckpointDto[], types: readonly IntegrationType[]): SyncAggregate {
  const list = checkpoints.filter((c) => types.includes(c.type));
  const enabled = list.filter((c) => c.enabled && c.status !== 'Disabled');
  let status = list.length ? 'Disabled' : 'None';
  let worst = -1;
  for (const c of enabled) {
    const sev = SYNC_SEVERITY[c.status] ?? 1;
    if (sev > worst) {
      worst = sev;
      status = c.status;
    }
  }
  return {
    status,
    tone: status === 'None' || status === 'Disabled' ? 'neutral' : syncStateTone(status),
    total: list.length,
    enabled: enabled.length,
    lastSuccess: latest(list.map((c) => c.lastSuccessfulSyncAt)),
    backlog: list.reduce((sum, c) => sum + (c.pendingMessages ?? 0), 0),
  };
}

/** The most recent of several ISO instants. */
export function latest(values: readonly (DateTimeUtc | null | undefined)[]): DateTimeUtc | null {
  let best: DateTimeUtc | null = null;
  let bestT = -Infinity;
  for (const v of values) {
    if (!v) continue;
    const t = new Date(v).getTime();
    if (!isNaN(t) && t > bestT) {
      bestT = t;
      best = v;
    }
  }
  return best;
}
