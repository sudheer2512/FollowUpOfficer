/**
 * Client-side descriptions of follow-up schedules, recurrence rules and due rules.
 * They mirror FollowUpSchedule.Describe(), RecurrenceRule.Describe() and DueRule.Describe() in the domain so forms can
 * show a live sentence before anything is saved.
 */
import { DayOfWeek } from '../models/enums';
import { DueRule, FollowUpSchedule, RecurrenceRule } from '../models/rules.models';
import { formatDateOnly, monthName, toTimeInput } from './dates';

function hhmm(t: string | null | undefined, fallback = '10:00'): string {
  return toTimeInput(t) || fallback;
}

function joinAnd(items: string[]): string {
  return items.join(' and ');
}

function dateList(dates: string[]): string {
  return dates.map((d) => formatDateOnly(d).replace(/ \d{4}$/, '')).join(', ');
}

/** Mirrors FollowUpSchedule.Describe(): "Every 2 days at 10:00 until completed". */
export function describeFollowUp(s: FollowUpSchedule | null | undefined): string {
  if (!s || s.frequency === 'None') return 'No automatic follow-up';
  const at = ` at ${hhmm(s.timeOfDay)}`;
  const weekdays = s.weekdays ?? [];
  let text: string;
  switch (s.frequency) {
    case 'Daily':
      text = 'Daily' + at;
      break;
    case 'EveryNDays':
      text = `Every ${s.intervalDays} days` + at;
      break;
    case 'Weekly':
      text = `Weekly${weekdays.length > 0 ? ' on ' + weekdays[0] : ''}` + at;
      break;
    case 'Fortnightly':
      text = 'Fortnightly' + at;
      break;
    case 'Monthly':
      text = 'Monthly' + at;
      break;
    case 'SpecificWeekdays':
      text = `Every ${joinAnd(weekdays)}` + at;
      break;
    case 'SpecificDates':
      text = `On ${dateList(s.dates ?? [])}` + at;
      break;
    case 'Custom':
      text = s.intervalHours > 0 ? `Every ${s.intervalHours} working hours` : `Every ${s.intervalDays} days` + at;
      break;
    default:
      text = String(s.frequency);
  }
  return text + (s.until ? ` until ${formatDateOnly(s.until)}` : ' until completed');
}

function ordinal(n: number): string {
  return n === 1 ? 'first' : n === 2 ? 'second' : n === 3 ? 'third' : n === 4 ? 'fourth' : `${n}th`;
}

/** Mirrors RecurrenceRule.Describe(): "Every month on day 1, created at 08:00". */
export function describeRecurrence(r: RecurrenceRule | null | undefined): string {
  if (!r) return '';
  const interval = Math.max(1, r.interval || 1);
  const every = interval > 1 ? `every ${interval} ` : 'every ';
  const cap = (s: string) => (s ? s.charAt(0).toUpperCase() + s.slice(1) : s);
  const days = () => ((r.daysOfWeek ?? []).length === 0 ? 'the start day' : joinAnd(r.daysOfWeek as DayOfWeek[]));
  const day = () => {
    switch (r.monthlyPattern) {
      case 'LastDay':
        return 'the last day';
      case 'FirstWorkingDay':
        return 'the first working day';
      case 'LastWorkingDay':
        return 'the last working day';
      case 'NthWeekday':
        return `the ${r.weekOrdinal === -1 ? 'last' : ordinal(r.weekOrdinal)} ${r.weekday}`;
      default:
        return `day ${r.dayOfMonth}`;
    }
  };
  const monthList = (ms: number[]) => ms.map((m) => monthName(m, true)).join(', ');
  const anchors = () => (r.anchorDates ?? []).map((d) => formatDateOnly(d)).join(', ');
  let text: string;
  switch (r.frequency) {
    case 'Daily':
      text = interval > 1 ? `Every ${interval} days` : 'Daily';
      break;
    case 'WorkingDays':
      text = 'Every working day';
      break;
    case 'Weekly':
      text =
        interval === 2 && (r.daysOfWeek ?? []).length <= 1
          ? `Fortnightly on ${days()}`
          : `${cap(every)}week${interval > 1 ? 's' : ''} on ${days()}`;
      break;
    case 'Monthly':
      text = `${cap(every)}month${interval > 1 ? 's' : ''} on ${day()}`;
      break;
    case 'Quarterly':
      text = (r.months ?? []).length ? `Quarterly (${monthList(r.months)}) on ${day()}` : `Quarterly on ${day()}`;
      break;
    case 'HalfYearly':
      text = (r.months ?? []).length ? `Half-yearly (${monthList(r.months)}) on ${day()}` : `Half-yearly on ${day()}`;
      break;
    case 'Yearly':
      text = (r.months ?? []).length ? `Annually in ${monthList(r.months)} on ${day()}` : `Annually on ${day()}`;
      break;
    case 'FinancialYear':
      text = `Once per financial year (starting ${monthName(r.fiscalYearStartMonth)}) on ${day()}`;
      break;
    case 'AnchorDates':
      text =
        r.anchorOffsetDays < 0
          ? `${-r.anchorOffsetDays} days before ${anchors() || 'the anchor date'}${r.repeatAnchorsYearly ? ' (every year)' : ''}`
          : `On ${anchors() || 'the anchor date'}${r.anchorOffsetDays > 0 ? ` + ${r.anchorOffsetDays} days` : ''}${r.repeatAnchorsYearly ? ' (every year)' : ''}`;
      break;
    default:
      text = String(r.frequency);
  }
  return `${text}, created at ${hhmm(r.createTime, '08:00')}`;
}

/** Mirrors DueRule.Describe(): "due by day 5 of the month by 18:00". */
export function describeDueRule(d: DueRule | null | undefined): string {
  if (!d) return '';
  let text: string;
  switch (d.kind) {
    case 'SameDay':
      text = 'due the same day';
      break;
    case 'AfterDays':
      text = `due ${d.value} day(s) later`;
      break;
    case 'AfterWorkingDays':
      text = `due ${d.value} working day(s) later`;
      break;
    case 'DayOfMonth':
      text = `due by day ${d.value} of the month`;
      break;
    case 'WeekdayOfSameWeek':
      text = `due ${d.weekday} of the same week`;
      break;
    case 'EndOfMonth':
      text = 'due at month end';
      break;
    case 'AnchorDate':
      text = 'due on the anchor date';
      break;
    default:
      text = String(d.kind);
  }
  return text + (d.dueTime ? ` by ${hhmm(d.dueTime)}` : '');
}
