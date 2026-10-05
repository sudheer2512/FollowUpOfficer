/**
 * Maps the server's free-form tone strings and enum values to the design-system tone classes:
 * neutral | info | good | warning | orange | danger | accent.
 */
import { Criticality, TaskHealth } from '../models/enums';

export type UiTone = 'neutral' | 'info' | 'good' | 'warning' | 'orange' | 'danger' | 'accent';

export function toneOf(tone: string | null | undefined): UiTone {
  switch ((tone ?? '').toLowerCase()) {
    case 'good':
    case 'success':
    case 'positive':
    case 'ok':
    case 'healthy':
    case 'green':
      return 'good';
    case 'warning':
    case 'warn':
    case 'watch':
    case 'amber':
    case 'caution':
      return 'warning';
    case 'atrisk':
    case 'orange':
    case 'high':
      return 'orange';
    case 'danger':
    case 'bad':
    case 'critical':
    case 'negative':
    case 'error':
    case 'red':
      return 'danger';
    case 'info':
    case 'blue':
      return 'info';
    case 'accent':
    case 'primary':
      return 'accent';
    default:
      return 'neutral';
  }
}

export function healthTone(h: TaskHealth | string | null | undefined): UiTone {
  switch (h) {
    case 'Healthy':
      return 'good';
    case 'Watch':
      return 'warning';
    case 'AtRisk':
      return 'orange';
    case 'Critical':
      return 'danger';
    default:
      return 'neutral';
  }
}

export function criticalityTone(c: Criticality | string | null | undefined): UiTone {
  switch (c) {
    case 'Informational':
      return 'neutral';
    case 'Important':
      return 'info';
    case 'Critical':
      return 'orange';
    case 'BusinessCritical':
      return 'danger';
    default:
      return 'neutral';
  }
}

/** Display status text / work status → tone. */
export function statusTone(status: string | null | undefined): UiTone {
  const s = (status ?? '').toLowerCase().replace(/\s+/g, '');
  if (s.includes('overdue')) return 'danger';
  if (s === 'completed') return 'good';
  if (s.includes('cancel') || s === 'draft' || s === 'deferred') return 'neutral';
  if (s.includes('blocked')) return 'danger';
  if (s.includes('waiting') || s.includes('awaiting') || s.includes('pending')) return 'warning';
  if (s.includes('completedbyassignee') || s.includes('approval')) return 'accent';
  if (s.includes('progress') || s.includes('partially')) return 'info';
  return 'neutral';
}

/** Server load label (Light / Moderate / Heavy / Overloaded …) → tone. */
export function loadTone(load: string | null | undefined): UiTone {
  const l = (load ?? '').toLowerCase();
  if (l.includes('over') || l.includes('critical')) return 'danger';
  if (l.includes('heavy') || l.includes('high')) return 'orange';
  if (l.includes('moderate') || l.includes('medium') || l.includes('busy')) return 'warning';
  if (l.includes('light') || l.includes('low') || l.includes('available')) return 'good';
  return 'neutral';
}
