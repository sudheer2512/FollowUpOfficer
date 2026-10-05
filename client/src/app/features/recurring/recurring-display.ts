/** Small display helpers shared by the recurring list and detail pages. */
import { RecurrenceState } from '../../core/models/enums';
import { UiTone } from '../../core/util/tone';

/** Active good · Paused warning · Ended neutral. */
export function stateTone(state: RecurrenceState | string | null | undefined): UiTone {
  switch (state) {
    case 'Active':
      return 'good';
    case 'Paused':
      return 'warning';
    default:
      return 'neutral';
  }
}

/** Compliance bar colour: ≥ 90 % good, ≥ 70 % warning, otherwise danger. */
export function complianceTone(percent: number | null | undefined): 'good' | 'warning' | 'danger' {
  const p = percent ?? 0;
  return p >= 90 ? 'good' : p >= 70 ? 'warning' : 'danger';
}

/** Lateness in minutes → "45 min" / "3h" / "2 days". */
export function latenessText(minutes: number | null | undefined): string {
  const m = Math.max(0, Math.round(minutes ?? 0));
  if (m < 60) return `${m} min`;
  if (m < 1440) return `${Math.round(m / 60)}h`;
  const days = Math.round(m / 1440);
  return `${days} day${days === 1 ? '' : 's'}`;
}

/** Clamps a percentage for bar widths. */
export function barWidth(percent: number | null | undefined): number {
  return Math.max(0, Math.min(100, percent ?? 0));
}
