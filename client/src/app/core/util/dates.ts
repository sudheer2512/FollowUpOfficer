/**
 * Date helpers. DateOnly values travel as "yyyy-MM-dd" and are handled as local calendar dates
 * (never through UTC parsing, which would shift the day in negative offsets). DateTime values are ISO UTC.
 */
import { DateOnly, DateTimeUtc, TimeOnly } from '../models/common.models';

const MONTHS_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const DAYS_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

function pad(n: number): string {
  return n < 10 ? `0${n}` : `${n}`;
}

/** Local Date → "yyyy-MM-dd". */
export function toDateOnly(d: Date): DateOnly {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** "yyyy-MM-dd" → local Date at midnight (null when invalid). */
export function parseDateOnly(value: DateOnly | null | undefined): Date | null {
  if (!value) return null;
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (!m) return null;
  return new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
}

export function todayDateOnly(): DateOnly {
  return toDateOnly(new Date());
}

export function addDays(value: DateOnly, days: number): DateOnly {
  const d = parseDateOnly(value) ?? new Date();
  d.setDate(d.getDate() + days);
  return toDateOnly(d);
}

/** Monday of the week containing the date. */
export function startOfWeek(value: DateOnly): DateOnly {
  const d = parseDateOnly(value) ?? new Date();
  const offset = (d.getDay() + 6) % 7;
  d.setDate(d.getDate() - offset);
  return toDateOnly(d);
}

/** Next given weekday (0=Sun … 6=Sat) strictly after today, or today when `includeToday`. */
export function nextWeekday(weekday: number, includeToday = false): DateOnly {
  const d = new Date();
  let add = (weekday - d.getDay() + 7) % 7;
  if (add === 0 && !includeToday) add = 7;
  d.setDate(d.getDate() + add);
  return toDateOnly(d);
}

/** Days between two DateOnly values (b − a). */
export function diffDays(a: DateOnly, b: DateOnly): number {
  const da = parseDateOnly(a);
  const db = parseDateOnly(b);
  if (!da || !db) return 0;
  return Math.round((db.getTime() - da.getTime()) / 86_400_000);
}

/** "6 Oct 2026" (or "Tue 6 Oct" with `withWeekday`, omitting the year when it is the current year). */
export function formatDateOnly(value: DateOnly | null | undefined, opts: { withWeekday?: boolean; short?: boolean } = {}): string {
  const d = parseDateOnly(value);
  if (!d) return '';
  const sameYear = d.getFullYear() === new Date().getFullYear();
  const day = `${d.getDate()} ${MONTHS_SHORT[d.getMonth()]}`;
  const year = opts.short && sameYear ? '' : ` ${d.getFullYear()}`;
  return `${opts.withWeekday ? DAYS_SHORT[d.getDay()] + ' ' : ''}${day}${year}`;
}

/** ISO date-time → "6 Oct 2026, 14:05" in local time. */
export function formatDateTime(value: DateTimeUtc | null | undefined, opts: { short?: boolean } = {}): string {
  if (!value) return '';
  const d = new Date(value);
  if (isNaN(d.getTime())) return '';
  const sameYear = d.getFullYear() === new Date().getFullYear();
  const date = `${d.getDate()} ${MONTHS_SHORT[d.getMonth()]}${opts.short && sameYear ? '' : ' ' + d.getFullYear()}`;
  return `${date}, ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** "just now", "5 min ago", "3h ago", "2 days ago", or "in 3h" for future instants. */
export function relativeTime(value: DateTimeUtc | null | undefined, now: Date = new Date()): string {
  if (!value) return '';
  const t = new Date(value).getTime();
  if (isNaN(t)) return '';
  const diffMs = now.getTime() - t;
  const future = diffMs < 0;
  const abs = Math.abs(diffMs);
  const min = Math.floor(abs / 60_000);
  const hours = Math.floor(abs / 3_600_000);
  const days = Math.floor(abs / 86_400_000);
  let text: string;
  if (min < 1) return 'just now';
  if (min < 60) text = `${min} min`;
  else if (hours < 24) text = `${hours}h`;
  else if (days < 30) text = `${days} day${days === 1 ? '' : 's'}`;
  else return formatDateTime(value, { short: true });
  return future ? `in ${text}` : `${text} ago`;
}

/** "HH:mm" or "HH:mm:ss" → "HH:mm:ss" (null for empty). */
export function toTimeOnly(value: string | null | undefined): TimeOnly | null {
  if (!value) return null;
  const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?/.exec(value);
  if (!m) return null;
  return `${pad(Number(m[1]))}:${m[2]}:${m[3] ?? '00'}`;
}

/** "HH:mm:ss" → "HH:mm" for <input type="time">. */
export function toTimeInput(value: TimeOnly | null | undefined): string {
  if (!value) return '';
  const m = /^(\d{1,2}):(\d{2})/.exec(value);
  return m ? `${pad(Number(m[1]))}:${m[2]}` : '';
}

/** ISO UTC → value for <input type="datetime-local"> ("yyyy-MM-ddTHH:mm", local). */
export function toLocalDateTimeInput(value: DateTimeUtc | null | undefined): string {
  if (!value) return '';
  const d = new Date(value);
  if (isNaN(d.getTime())) return '';
  return `${toDateOnly(d)}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** <input type="datetime-local"> value → ISO UTC. */
export function fromLocalDateTimeInput(value: string | null | undefined): DateTimeUtc | null {
  if (!value) return null;
  const d = new Date(value);
  return isNaN(d.getTime()) ? null : d.toISOString();
}

/** Combines a DateOnly and an optional "HH:mm" into ISO UTC (local interpretation). */
export function combineDateTime(date: DateOnly, time: string | null | undefined): DateTimeUtc | null {
  const d = parseDateOnly(date);
  if (!d) return null;
  const t = /^(\d{1,2}):(\d{2})/.exec(time ?? '');
  if (t) d.setHours(Number(t[1]), Number(t[2]), 0, 0);
  else d.setHours(9, 0, 0, 0);
  return d.toISOString();
}

export function monthName(month: number, short = false): string {
  const names = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
  const n = names[(month - 1 + 12) % 12];
  return short ? n.slice(0, 3) : n;
}

export function greetingFor(date: Date = new Date()): string {
  const h = date.getHours();
  return h < 12 ? 'Good morning' : h < 17 ? 'Good afternoon' : 'Good evening';
}

export function formatLongDate(date: Date = new Date()): string {
  const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
  return `${days[date.getDay()]}, ${date.getDate()} ${monthName(date.getMonth() + 1)} ${date.getFullYear()}`;
}
