import { DateOnly, TimeOnly } from './common.models';
import {
  Criticality,
  DayOfWeek,
  DueRuleKind,
  FollowUpFrequency,
  HolidayHandling,
  MonthlyPattern,
  RecurrenceFrequency,
} from './enums';

/** FollowUp.Domain.Rules.FollowUpSchedule (spec §9). */
export interface FollowUpSchedule {
  frequency: FollowUpFrequency;
  intervalDays: number;
  /** Custom schedules only: every N hours within working hours. */
  intervalHours: number;
  weekdays: DayOfWeek[];
  dates: DateOnly[];
  timeOfDay: TimeOnly;
  workingDaysOnly: boolean;
  until?: DateOnly | null;
}

export const NO_FOLLOW_UP: FollowUpSchedule = {
  frequency: 'None',
  intervalDays: 1,
  intervalHours: 0,
  weekdays: [],
  dates: [],
  timeOfDay: '10:00:00',
  workingDaysOnly: true,
  until: null,
};

/** FollowUp.Domain.Rules.RecurrenceRule (spec §11, §13). */
export interface RecurrenceRule {
  frequency: RecurrenceFrequency;
  /** Every N days / weeks / months / years. Fortnightly = Weekly with interval 2. */
  interval: number;
  daysOfWeek: DayOfWeek[];
  monthlyPattern: MonthlyPattern;
  dayOfMonth: number;
  /** For NthWeekday: 1..4, or -1 for the last such weekday. */
  weekOrdinal: number;
  weekday: DayOfWeek;
  /** Explicit months (1–12) for quarterly / half-yearly / yearly rules. */
  months: number[];
  fiscalYearStartMonth: number;
  alignToFiscalYear: boolean;
  anchorDates: DateOnly[];
  anchorOffsetDays: number;
  repeatAnchorsYearly: boolean;
  /** Local time at which each occurrence is created. */
  createTime: TimeOnly;
}

export const DEFAULT_RECURRENCE_RULE: RecurrenceRule = {
  frequency: 'Monthly',
  interval: 1,
  daysOfWeek: [],
  monthlyPattern: 'DayOfMonth',
  dayOfMonth: 1,
  weekOrdinal: 1,
  weekday: 'Monday',
  months: [],
  fiscalYearStartMonth: 4,
  alignToFiscalYear: false,
  anchorDates: [],
  anchorOffsetDays: 0,
  repeatAnchorsYearly: false,
  createTime: '08:00:00',
};

/** FollowUp.Domain.Rules.DueRule (spec §12). */
export interface DueRule {
  kind: DueRuleKind;
  /** Days for AfterDays / AfterWorkingDays; day number for DayOfMonth. */
  value: number;
  weekday: DayOfWeek;
  dueTime?: TimeOnly | null;
  /** What to do when the computed due date is not a working day. */
  holidayHandling: HolidayHandling;
}

export const DEFAULT_DUE_RULE: DueRule = {
  kind: 'SameDay',
  value: 0,
  weekday: 'Friday',
  dueTime: null,
  holidayHandling: 'PreviousWorkingDay',
};

/** FollowUp.Domain.TemplateItem (spec §51). */
export interface TemplateItem {
  key: string;
  parentKey?: string | null;
  title: string;
  description?: string | null;
  assigneeRole?: string | null;
  /** Due offset in working days from the anchor (start) date. */
  dueOffsetWorkingDays: number;
  criticality?: Criticality | null;
  dependsOn: string[];
  followUp?: FollowUpSchedule | null;
}
