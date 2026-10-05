/** Pure helpers for the weekly review page: week resolution, the week heading and "queued" toasts. */
import { DateOnly, QueuedResult } from '../../core/models/common.models';
import { addDays, parseDateOnly, startOfWeek, todayDateOnly } from '../../core/util/dates';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** The Monday of the week containing `value` ("yyyy-MM-dd"); the current week when missing or invalid. */
export function resolveWeekStart(value: string | null | undefined, today: DateOnly = todayDateOnly()): DateOnly {
  return startOfWeek(value && parseDateOnly(value) ? value : today);
}

/** "Week of 6 Oct – 12 Oct 2026"; both years are shown when the week spans New Year. */
export function weekRangeLabel(start: DateOnly, end?: DateOnly | null): string {
  const s = parseDateOnly(start);
  if (!s) return '';
  const e = parseDateOnly(end) ?? parseDateOnly(addDays(start, 6))!;
  const day = (d: Date) => `${d.getDate()} ${MONTHS[d.getMonth()]}`;
  const from = s.getFullYear() === e.getFullYear() ? day(s) : `${day(s)} ${s.getFullYear()}`;
  return `Week of ${from} – ${day(e)} ${e.getFullYear()}`;
}

/** Toast text for a `{ queued }` response: "Queued 3 messages" when the server returns a count. */
export function queuedMessage(result: QueuedResult | null | undefined): string {
  const q = result?.queued;
  if (typeof q === 'number') return q === 0 ? 'Nothing was queued — there were no recipients' : `Queued ${q} message${q === 1 ? '' : 's'}`;
  return 'Queued';
}
