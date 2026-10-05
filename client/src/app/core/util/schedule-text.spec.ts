import { DEFAULT_DUE_RULE, DEFAULT_RECURRENCE_RULE, FollowUpSchedule, NO_FOLLOW_UP } from '../models/rules.models';
import { describeDueRule, describeFollowUp, describeRecurrence } from './schedule-text';

const schedule = (p: Partial<FollowUpSchedule>): FollowUpSchedule => ({ ...NO_FOLLOW_UP, ...p });

describe('describeFollowUp (mirrors FollowUpSchedule.Describe)', () => {
  it('describes no follow-up', () => {
    expect(describeFollowUp(NO_FOLLOW_UP)).toBe('No automatic follow-up');
    expect(describeFollowUp(null)).toBe('No automatic follow-up');
  });

  it('describes interval schedules with time and default end', () => {
    expect(describeFollowUp(schedule({ frequency: 'Daily', timeOfDay: '09:30:00' }))).toBe('Daily at 09:30 until completed');
    expect(describeFollowUp(schedule({ frequency: 'EveryNDays', intervalDays: 2 }))).toBe('Every 2 days at 10:00 until completed');
    expect(describeFollowUp(schedule({ frequency: 'Fortnightly' }))).toBe('Fortnightly at 10:00 until completed');
    expect(describeFollowUp(schedule({ frequency: 'Monthly' }))).toBe('Monthly at 10:00 until completed');
  });

  it('describes weekday schedules', () => {
    expect(describeFollowUp(schedule({ frequency: 'Weekly', weekdays: ['Friday'] }))).toBe('Weekly on Friday at 10:00 until completed');
    expect(describeFollowUp(schedule({ frequency: 'SpecificWeekdays', weekdays: ['Monday', 'Thursday'] }))).toBe(
      'Every Monday and Thursday at 10:00 until completed',
    );
  });

  it('describes custom hour schedules without a time of day', () => {
    expect(describeFollowUp(schedule({ frequency: 'Custom', intervalHours: 4 }))).toBe('Every 4 working hours until completed');
    expect(describeFollowUp(schedule({ frequency: 'Custom', intervalDays: 5, intervalHours: 0 }))).toBe('Every 5 days at 10:00 until completed');
  });

  it('adds an explicit end date', () => {
    expect(describeFollowUp(schedule({ frequency: 'Daily', until: '2026-12-31' }))).toBe('Daily at 10:00 until 31 Dec 2026');
  });

  it('lists specific dates without the year', () => {
    expect(describeFollowUp(schedule({ frequency: 'SpecificDates', dates: ['2026-10-09', '2026-10-16'] }))).toBe(
      'On 9 Oct, 16 Oct at 10:00 until completed',
    );
  });
});

describe('describeRecurrence (mirrors RecurrenceRule.Describe)', () => {
  it('describes monthly day-of-month rules', () => {
    expect(describeRecurrence(DEFAULT_RECURRENCE_RULE)).toBe('Every month on day 1, created at 08:00');
  });

  it('describes fortnightly weekly rules', () => {
    expect(describeRecurrence({ ...DEFAULT_RECURRENCE_RULE, frequency: 'Weekly', interval: 2, daysOfWeek: ['Monday'] })).toBe(
      'Fortnightly on Monday, created at 08:00',
    );
  });

  it('describes last working day and nth weekday patterns', () => {
    expect(describeRecurrence({ ...DEFAULT_RECURRENCE_RULE, monthlyPattern: 'LastWorkingDay' })).toBe(
      'Every month on the last working day, created at 08:00',
    );
    expect(
      describeRecurrence({ ...DEFAULT_RECURRENCE_RULE, monthlyPattern: 'NthWeekday', weekOrdinal: -1, weekday: 'Friday' }),
    ).toBe('Every month on the last Friday, created at 08:00');
  });

  it('describes quarterly rules with explicit months', () => {
    expect(describeRecurrence({ ...DEFAULT_RECURRENCE_RULE, frequency: 'Quarterly', months: [1, 4, 7, 10] })).toBe(
      'Quarterly (Jan, Apr, Jul, Oct) on day 1, created at 08:00',
    );
  });

  it('describes anchor-date rules with a negative offset', () => {
    expect(
      describeRecurrence({
        ...DEFAULT_RECURRENCE_RULE,
        frequency: 'AnchorDates',
        anchorDates: ['2027-03-31'],
        anchorOffsetDays: -15,
        repeatAnchorsYearly: true,
      }),
    ).toBe('15 days before 31 Mar 2027 (every year), created at 08:00');
  });
});

describe('describeDueRule (mirrors DueRule.Describe)', () => {
  it('describes due rules with and without a time', () => {
    expect(describeDueRule(DEFAULT_DUE_RULE)).toBe('due the same day');
    expect(describeDueRule({ ...DEFAULT_DUE_RULE, kind: 'DayOfMonth', value: 5, dueTime: '18:00:00' })).toBe('due by day 5 of the month by 18:00');
    expect(describeDueRule({ ...DEFAULT_DUE_RULE, kind: 'WeekdayOfSameWeek', weekday: 'Friday' })).toBe('due Friday of the same week');
  });
});
