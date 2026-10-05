/** Display formatting for report metrics, periods and on-time percentages. */
import { DateOnly } from '../../core/models/common.models';
import { MetricValue } from '../../core/models/view.models';
import { parseDateOnly } from '../../core/util/dates';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** One decimal, without a trailing ".0" (3.4, 3). */
export function oneDecimal(value: number | null | undefined): string {
  const v = typeof value === 'number' && isFinite(value) ? value : 0;
  const rounded = Math.round(v * 10) / 10;
  return Number.isInteger(rounded) ? String(rounded) : rounded.toFixed(1);
}

/** Formats a metric by its unit: "%" → "82%", "days" → "3.4 days", "hours" → "5.2 h", otherwise an integer. */
export function formatMetric(metric: Pick<MetricValue, 'value' | 'unit'>): string {
  const v = typeof metric.value === 'number' && isFinite(metric.value) ? metric.value : 0;
  switch ((metric.unit ?? '').trim().toLowerCase()) {
    case '%':
    case 'percent':
      return `${Math.round(v)}%`;
    case 'day':
    case 'days':
      return `${oneDecimal(v)} ${oneDecimal(v) === '1' ? 'day' : 'days'}`;
    case 'h':
    case 'hour':
    case 'hours':
      return `${oneDecimal(v)} h`;
    default:
      return String(Math.round(v));
  }
}

/** "1 Sep – 6 Oct 2026", or with both years when the range crosses a year boundary. */
export function periodLabel(from: DateOnly | null | undefined, to: DateOnly | null | undefined): string {
  const a = parseDateOnly(from);
  const b = parseDateOnly(to);
  if (!a || !b) return '';
  const day = (d: Date) => `${d.getDate()} ${MONTHS[d.getMonth()]}`;
  const start = a.getFullYear() === b.getFullYear() ? day(a) : `${day(a)} ${a.getFullYear()}`;
  return `${start} – ${day(b)} ${b.getFullYear()}`;
}

export type OnTimeTone = 'good' | 'warning' | 'danger';

/** On-time colour carries meaning: ≥ 80 % good, ≥ 60 % watch, below that poor. */
export function onTimeTone(percent: number | null | undefined): OnTimeTone {
  const p = percent ?? 0;
  return p >= 80 ? 'good' : p >= 60 ? 'warning' : 'danger';
}

export function clampPercent(value: number | null | undefined): number {
  return Math.max(0, Math.min(100, value ?? 0));
}
