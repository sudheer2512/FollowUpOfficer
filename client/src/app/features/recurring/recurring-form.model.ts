/**
 * Recurring-definition form model: the typed FormGroup factory, defaults, and the mappings between the form value,
 * RecurringDefinitionInput (POST/PUT /api/recurring) and RecurringDefinitionDto. Shared by the /recurring editor and
 * the Delegate dialog (which switches to this form when a task is recurring).
 */
import { FormControl, NonNullableFormBuilder, Validators } from '@angular/forms';
import { DateOnly, Guid } from '../../core/models/common.models';
import {
  CatchUpPolicy,
  Criticality,
  DayOfWeek,
  DueRuleKind,
  HolidayHandling,
  MonthlyPattern,
  RecurrenceFrequency,
  TaskPriority,
  TaskVisibility,
  WEEKDAYS_MONDAY_FIRST,
} from '../../core/models/enums';
import {
  DEFAULT_DUE_RULE,
  DEFAULT_RECURRENCE_RULE,
  DueRule,
  FollowUpSchedule,
  NO_FOLLOW_UP,
  RecurrenceRule,
} from '../../core/models/rules.models';
import { PreviewRequest, RecurringDefinitionDto, RecurringDefinitionInput } from '../../core/models/view.models';
import { todayDateOnly, toTimeInput, toTimeOnly } from '../../core/util/dates';

/** Default follow-up for new delegations and recurring tasks: every 2 days at 10:00, working days only. */
export const DEFAULT_FOLLOW_UP: FollowUpSchedule = {
  ...NO_FOLLOW_UP,
  frequency: 'EveryNDays',
  intervalDays: 2,
  timeOfDay: '10:00:00',
  workingDaysOnly: true,
};

/** Frequencies whose occurrences are picked from explicit months (Jan–Dec chips). */
export const MONTH_LIST_FREQUENCIES: readonly RecurrenceFrequency[] = ['Quarterly', 'HalfYearly', 'Yearly'];
/** Frequencies that place the occurrence on a day within a month (monthly pattern). */
export const MONTH_BASED_FREQUENCIES: readonly RecurrenceFrequency[] = ['Monthly', 'Quarterly', 'HalfYearly', 'Yearly', 'FinancialYear'];
/** Frequencies with a meaningful "every N" interval. */
export const INTERVAL_FREQUENCIES: readonly RecurrenceFrequency[] = ['Daily', 'Weekly', 'Monthly', 'Yearly'];

/** A blank definition with sensible defaults (mirrors the server's RecurringDefinitionInput defaults). */
export function emptyRecurringInput(): RecurringDefinitionInput {
  return {
    code: null,
    titleTemplate: '',
    description: null,
    ownerId: null,
    primaryAssigneeId: null,
    additionalAssigneeIds: [],
    teamId: null,
    departmentId: null,
    projectId: null,
    categoryId: null,
    priority: 'Normal',
    criticality: 'Normal',
    visibility: 'Normal',
    rule: { ...DEFAULT_RECURRENCE_RULE, daysOfWeek: [], months: [], anchorDates: [] },
    due: { ...DEFAULT_DUE_RULE },
    followUp: { ...DEFAULT_FOLLOW_UP },
    escalationPolicyId: null,
    holidayHandling: 'NextWorkingDay',
    catchUp: 'GenerateAll',
    catchUpWindowDays: 3,
    requiresApproval: false,
    requiresAcknowledgement: false,
    checklist: [],
    startDate: todayDateOnly(),
    endDate: null,
    maxOccurrences: null,
  };
}

/** Typed FormGroup for one recurring definition. Times are held as "HH:mm" for `<input type="time">`. */
export function createRecurringForm(fb: NonNullableFormBuilder) {
  return fb.group({
    code: fb.control(''),
    titleTemplate: fb.control('', { validators: [Validators.required, Validators.maxLength(300)] }),
    description: fb.control(''),
    ownerId: fb.control<Guid | null>(null),
    primaryAssigneeId: fb.control<Guid | null>(null),
    additionalAssigneeIds: fb.control<Guid[]>([]),
    teamId: fb.control<Guid | null>(null),
    departmentId: fb.control<Guid | null>(null),
    projectId: fb.control<Guid | null>(null),
    categoryId: fb.control<Guid | null>(null),
    priority: fb.control<TaskPriority>('Normal'),
    criticality: fb.control<Criticality>('Normal'),
    visibility: fb.control<TaskVisibility>('Normal'),
    rule: fb.group({
      frequency: fb.control<RecurrenceFrequency>('Monthly'),
      interval: fb.control(1),
      daysOfWeek: fb.control<DayOfWeek[]>([]),
      monthlyPattern: fb.control<MonthlyPattern>('DayOfMonth'),
      dayOfMonth: fb.control(1),
      weekOrdinal: fb.control(1),
      weekday: fb.control<DayOfWeek>('Monday'),
      months: fb.control<number[]>([]),
      fiscalYearStartMonth: fb.control(4),
      alignToFiscalYear: fb.control(false),
      anchorDates: fb.control<DateOnly[]>([]),
      anchorOffsetDays: fb.control(0),
      repeatAnchorsYearly: fb.control(false),
      createTime: fb.control('08:00'),
    }),
    due: fb.group({
      kind: fb.control<DueRuleKind>('SameDay'),
      value: fb.control(0),
      weekday: fb.control<DayOfWeek>('Friday'),
      dueTime: fb.control(''),
      holidayHandling: fb.control<HolidayHandling>('PreviousWorkingDay'),
    }),
    followUp: fb.control<FollowUpSchedule>({ ...DEFAULT_FOLLOW_UP }),
    escalationPolicyId: fb.control<Guid | null>(null),
    holidayHandling: fb.control<HolidayHandling>('NextWorkingDay'),
    catchUp: fb.control<CatchUpPolicy>('GenerateAll'),
    catchUpWindowDays: fb.control(3),
    requiresApproval: fb.control(false),
    requiresAcknowledgement: fb.control(false),
    checklist: fb.array<FormControl<string>>([]),
    startDate: fb.control<DateOnly | null>(todayDateOnly()),
    endDate: fb.control<DateOnly | null>(null),
    maxOccurrences: fb.control<number | null>(null),
  });
}

export type RecurringForm = ReturnType<typeof createRecurringForm>;
export type RecurringFormValue = ReturnType<RecurringForm['getRawValue']>;
export type RuleFormValue = RecurringFormValue['rule'];
export type DueFormValue = RecurringFormValue['due'];

/** Number input values can be null, '' or out of range: coerce to an integer within [min, max]. */
export function clampInt(value: unknown, min: number, max: number, fallback: number): number {
  if (value === null || value === undefined || value === '') return fallback;
  const n = Math.trunc(Number(value));
  if (!Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, n));
}

function textOrNull(value: string | null | undefined): string | null {
  const t = (value ?? '').trim();
  return t ? t : null;
}

function uniqueIds(ids: readonly (Guid | null | undefined)[]): Guid[] {
  return [...new Set(ids.filter((x): x is Guid => !!x))];
}

/** Keeps only the fields that matter for the chosen frequency so stale values never leak into the rule. */
export function ruleFromForm(r: RuleFormValue): RecurrenceRule {
  const f = r.frequency;
  const months = MONTH_LIST_FREQUENCIES.includes(f)
    ? [...new Set(r.months ?? [])].filter((m) => m >= 1 && m <= 12).sort((a, b) => a - b)
    : [];
  return {
    frequency: f,
    interval: INTERVAL_FREQUENCIES.includes(f) ? clampInt(r.interval, 1, 99, 1) : 1,
    daysOfWeek: f === 'Weekly' ? WEEKDAYS_MONDAY_FIRST.filter((d) => (r.daysOfWeek ?? []).includes(d)) : [],
    monthlyPattern: r.monthlyPattern,
    dayOfMonth: clampInt(r.dayOfMonth, 1, 31, 1),
    weekOrdinal: Number(r.weekOrdinal) === -1 ? -1 : clampInt(r.weekOrdinal, 1, 4, 1),
    weekday: r.weekday,
    months,
    fiscalYearStartMonth: clampInt(r.fiscalYearStartMonth, 1, 12, 4),
    alignToFiscalYear: (f === 'Quarterly' || f === 'HalfYearly') && months.length === 0 && !!r.alignToFiscalYear,
    anchorDates: f === 'AnchorDates' ? [...new Set(r.anchorDates ?? [])].sort() : [],
    anchorOffsetDays: f === 'AnchorDates' ? clampInt(r.anchorOffsetDays, -366, 366, 0) : 0,
    repeatAnchorsYearly: f === 'AnchorDates' && !!r.repeatAnchorsYearly,
    createTime: toTimeOnly(r.createTime) ?? '08:00:00',
  };
}

export function dueFromForm(d: DueFormValue): DueRule {
  let value = 0;
  if (d.kind === 'AfterDays' || d.kind === 'AfterWorkingDays') value = clampInt(d.value, 0, 365, 0);
  else if (d.kind === 'DayOfMonth') value = clampInt(d.value, 1, 31, 1);
  return {
    kind: d.kind,
    value,
    weekday: d.weekday,
    dueTime: toTimeOnly(d.dueTime),
    holidayHandling: d.holidayHandling === 'Skip' ? 'PreviousWorkingDay' : d.holidayHandling,
  };
}

/** Form value → API input. */
export function recurringInputFromForm(v: RecurringFormValue): RecurringDefinitionInput {
  const primary = v.primaryAssigneeId ?? null;
  const max = v.maxOccurrences === null || v.maxOccurrences === undefined || String(v.maxOccurrences) === '' ? null : clampInt(v.maxOccurrences, 1, 10000, 1);
  return {
    code: textOrNull(v.code),
    titleTemplate: (v.titleTemplate ?? '').trim(),
    description: textOrNull(v.description),
    ownerId: v.ownerId ?? null,
    primaryAssigneeId: primary,
    additionalAssigneeIds: uniqueIds(v.additionalAssigneeIds ?? []).filter((id) => id !== primary),
    teamId: v.teamId ?? null,
    departmentId: v.departmentId ?? null,
    projectId: v.projectId ?? null,
    categoryId: v.categoryId ?? null,
    priority: v.priority,
    criticality: v.criticality,
    visibility: v.visibility,
    rule: ruleFromForm(v.rule),
    due: dueFromForm(v.due),
    followUp: v.followUp ?? null,
    escalationPolicyId: v.escalationPolicyId ?? null,
    holidayHandling: v.holidayHandling,
    catchUp: v.catchUp,
    catchUpWindowDays: clampInt(v.catchUpWindowDays, 1, 365, 3),
    requiresApproval: !!v.requiresApproval,
    requiresAcknowledgement: !!v.requiresAcknowledgement,
    checklist: (v.checklist ?? []).map((s) => s.trim()).filter((s) => s.length > 0),
    startDate: v.startDate || null,
    endDate: v.endDate || null,
    maxOccurrences: max,
  };
}

/** Replaces the whole form with an input (used for edit, draft pre-fill and mode switches). Leaves the form pristine. */
export function patchRecurringForm(form: RecurringForm, input: RecurringDefinitionInput): void {
  const rule: RecurrenceRule = { ...DEFAULT_RECURRENCE_RULE, ...(input.rule ?? {}) };
  const due: DueRule = { ...DEFAULT_DUE_RULE, ...(input.due ?? {}) };
  form.patchValue({
    code: input.code ?? '',
    titleTemplate: input.titleTemplate ?? '',
    description: input.description ?? '',
    ownerId: input.ownerId ?? null,
    primaryAssigneeId: input.primaryAssigneeId ?? null,
    additionalAssigneeIds: [...(input.additionalAssigneeIds ?? [])],
    teamId: input.teamId ?? null,
    departmentId: input.departmentId ?? null,
    projectId: input.projectId ?? null,
    categoryId: input.categoryId ?? null,
    priority: input.priority ?? 'Normal',
    criticality: input.criticality ?? 'Normal',
    visibility: input.visibility ?? 'Normal',
    rule: {
      frequency: rule.frequency,
      interval: rule.interval || 1,
      daysOfWeek: [...(rule.daysOfWeek ?? [])],
      monthlyPattern: rule.monthlyPattern,
      dayOfMonth: rule.dayOfMonth || 1,
      weekOrdinal: rule.weekOrdinal || 1,
      weekday: rule.weekday,
      months: [...(rule.months ?? [])],
      fiscalYearStartMonth: rule.fiscalYearStartMonth || 4,
      alignToFiscalYear: !!rule.alignToFiscalYear,
      anchorDates: [...(rule.anchorDates ?? [])],
      anchorOffsetDays: rule.anchorOffsetDays ?? 0,
      repeatAnchorsYearly: !!rule.repeatAnchorsYearly,
      createTime: toTimeInput(rule.createTime) || '08:00',
    },
    due: {
      kind: due.kind,
      value: due.value ?? 0,
      weekday: due.weekday,
      dueTime: toTimeInput(due.dueTime),
      holidayHandling: due.holidayHandling,
    },
    followUp: input.followUp ? { ...NO_FOLLOW_UP, ...input.followUp } : { ...NO_FOLLOW_UP },
    escalationPolicyId: input.escalationPolicyId ?? null,
    holidayHandling: input.holidayHandling ?? 'NextWorkingDay',
    catchUp: input.catchUp ?? 'GenerateAll',
    catchUpWindowDays: input.catchUpWindowDays || 3,
    requiresApproval: !!input.requiresApproval,
    requiresAcknowledgement: !!input.requiresAcknowledgement,
    startDate: input.startDate ?? null,
    endDate: input.endDate ?? null,
    maxOccurrences: input.maxOccurrences ?? null,
  });
  const checklist = form.controls.checklist;
  checklist.clear();
  for (const item of input.checklist ?? []) checklist.push(new FormControl(item, { nonNullable: true }));
  form.markAsPristine();
}

/** Server DTO → editable input (references become ids). */
export function inputFromDto(dto: RecurringDefinitionDto): RecurringDefinitionInput {
  return {
    code: dto.code ?? null,
    titleTemplate: dto.titleTemplate ?? '',
    description: dto.description ?? null,
    ownerId: dto.owner?.id ?? null,
    primaryAssigneeId: dto.primaryAssignee?.id ?? null,
    additionalAssigneeIds: (dto.additionalAssignees ?? []).map((p) => p.id),
    teamId: dto.team?.id ?? null,
    departmentId: dto.department?.id ?? null,
    projectId: dto.project?.id ?? null,
    // The DTO carries no category; an edit leaves it unset unless the user picks one again.
    categoryId: null,
    priority: dto.priority ?? 'Normal',
    criticality: dto.criticality ?? 'Normal',
    visibility: dto.visibility ?? 'Normal',
    rule: { ...DEFAULT_RECURRENCE_RULE, ...(dto.rule ?? {}) },
    due: { ...DEFAULT_DUE_RULE, ...(dto.due ?? {}) },
    followUp: dto.followUp ?? null,
    escalationPolicyId: dto.escalationPolicyId ?? null,
    holidayHandling: dto.holidayHandling ?? 'NextWorkingDay',
    catchUp: dto.catchUp ?? 'GenerateAll',
    catchUpWindowDays: dto.catchUpWindowDays ?? 3,
    requiresApproval: !!dto.requiresApproval,
    requiresAcknowledgement: !!dto.requiresAcknowledgement,
    checklist: [...(dto.checklist ?? [])],
    startDate: dto.startDate ?? null,
    endDate: dto.endDate ?? null,
    maxOccurrences: dto.maxOccurrences ?? null,
  };
}

/** Client-side checks mirroring RecurringTaskService.Validate, so problems show before the round trip. */
export function recurringProblems(input: RecurringDefinitionInput): string[] {
  const problems: string[] = [];
  if (!input.titleTemplate.trim()) problems.push('Give the recurring task a title.');
  if (!input.primaryAssigneeId && !input.teamId) problems.push('Choose who does the recurring task — a person or a team.');
  if (input.rule.frequency === 'AnchorDates' && input.rule.anchorDates.length === 0) {
    problems.push('Add at least one anchor date (for example the licence expiry).');
  }
  if (input.startDate && input.endDate && input.endDate < input.startDate) problems.push('The end date is before the start date.');
  return problems;
}

/** Preview request for a rule, or null when the rule cannot produce dates yet (anchor rule without anchors). */
export function previewRequestOf(
  src: Pick<RecurringDefinitionInput, 'rule' | 'due' | 'holidayHandling' | 'startDate' | 'endDate' | 'titleTemplate'>,
  count: number,
): PreviewRequest | null {
  if (src.rule.frequency === 'AnchorDates' && (src.rule.anchorDates ?? []).length === 0) return null;
  return {
    rule: src.rule,
    due: src.due,
    holidayHandling: src.holidayHandling,
    startDate: src.startDate ?? null,
    endDate: src.endDate ?? null,
    titleTemplate: (src.titleTemplate ?? '').trim() || null,
    count,
  };
}

// ───────────────────────────── Labels & explanations ─────────────────────────────

export const MONTHLY_PATTERN_LABEL: Record<MonthlyPattern, string> = {
  DayOfMonth: 'On a day number',
  LastDay: 'Last day of the month',
  FirstWorkingDay: 'First working day',
  LastWorkingDay: 'Last working day',
  NthWeekday: 'A weekday (e.g. second Tuesday)',
};

export const DUE_KIND_LABEL: Record<DueRuleKind, string> = {
  SameDay: 'Same day as the occurrence',
  AfterDays: 'After N days',
  AfterWorkingDays: 'After N working days',
  DayOfMonth: 'By day N of the month',
  WeekdayOfSameWeek: 'A weekday of the same week',
  EndOfMonth: 'End of the month',
  AnchorDate: 'On the anchor date',
};

export const HOLIDAY_HANDLING_TEXT: Record<HolidayHandling, string> = {
  Keep: 'Create it on the weekend or holiday anyway.',
  NextWorkingDay: 'Move it to the next working day.',
  PreviousWorkingDay: 'Bring it forward to the previous working day.',
  Skip: 'Skip that occurrence — it is recorded as “Skipped – holiday”.',
};

/** What happens to occurrences that fell due while the system was unavailable (mirrors RecurringTaskService.Classify). */
export const CATCH_UP_TEXT: Record<CatchUpPolicy, string> = {
  GenerateAll: 'Every missed occurrence is created when the system is back, marked “Created late”. Nothing is lost.',
  GenerateLatestOnly: 'Only the most recent missed occurrence is created (marked “Created late”); older ones are recorded as “Skipped – catch-up”.',
  SkipMissed: 'Missed occurrences whose due date has already passed are recorded as “Skipped – catch-up”; those still due are created late.',
  GenerateWithinWindow: 'Missed occurrences scheduled within the window are created late; older ones are recorded as “Skipped – catch-up”.',
};

export const WEEK_ORDINALS: { value: number; label: string }[] = [
  { value: 1, label: 'First' },
  { value: 2, label: 'Second' },
  { value: 3, label: 'Third' },
  { value: 4, label: 'Fourth' },
  { value: -1, label: 'Last' },
];

/** "15 days before each anchor date" / "On each anchor date" / "5 days after each anchor date". */
export function anchorOffsetSentence(offset: number): string {
  const n = Math.trunc(Number(offset) || 0);
  if (n === 0) return 'On each anchor date';
  const days = Math.abs(n) === 1 ? '1 day' : `${Math.abs(n)} days`;
  return n < 0 ? `${days} before each anchor date` : `${days} after each anchor date`;
}

// ───────────────────────────── Title tokens ─────────────────────────────

export interface TitleToken {
  token: string;
  meaning: string;
  example: string;
}

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

function pad2(n: number): string {
  return n < 10 ? `0${n}` : String(n);
}

/** ISO-8601 week number (weeks start on Monday; week 1 contains the first Thursday). */
export function isoWeek(date: Date): number {
  const d = new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()));
  const day = d.getUTCDay() || 7;
  d.setUTCDate(d.getUTCDate() + 4 - day);
  const yearStart = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
  return Math.ceil(((d.getTime() - yearStart.getTime()) / 86_400_000 + 1) / 7);
}

/** Mirrors RecurrenceEngine.FiscalYearLabel: "FY2026-27" (or "FY2026" for calendar financial years). */
export function fiscalYearLabel(date: Date, fyStartMonth: number): string {
  const month = date.getMonth() + 1;
  if (fyStartMonth <= 1) return `FY${date.getFullYear()}`;
  const startYear = month >= fyStartMonth ? date.getFullYear() : date.getFullYear() - 1;
  return `FY${startYear}-${pad2((startYear + 1) % 100)}`;
}

/** The title tokens RecurrenceEngine.RenderTitle understands, with examples for `date`. */
export function titleTokens(date: Date = new Date(), fyStartMonth = 4): TitleToken[] {
  const m = date.getMonth();
  const prev = new Date(date.getFullYear(), m - 1, 1);
  const fy = fiscalYearLabel(date, fyStartMonth);
  const fyQuarter = Math.floor(((m + 1 - fyStartMonth + 12) % 12) / 3) + 1;
  return [
    { token: '{Month}', meaning: 'Month of the occurrence', example: MONTHS[m] },
    { token: '{MonthShort}', meaning: 'Short month', example: MONTHS[m].slice(0, 3) },
    { token: '{PrevMonth}', meaning: 'Previous month', example: MONTHS[prev.getMonth()] },
    { token: '{PrevMonthYear}', meaning: 'Previous month and its year', example: `${MONTHS[prev.getMonth()]} ${prev.getFullYear()}` },
    { token: '{Year}', meaning: 'Year', example: String(date.getFullYear()) },
    { token: '{Quarter}', meaning: 'Calendar quarter', example: `Q${Math.floor(m / 3) + 1}` },
    { token: '{FYQuarter}', meaning: 'Financial-year quarter', example: `Q${fyQuarter} ${fy}` },
    { token: '{FY}', meaning: 'Financial year', example: fy },
    { token: '{Week}', meaning: 'ISO week number', example: `W${pad2(isoWeek(date))}` },
    { token: '{Weekday}', meaning: 'Day of the week', example: DAYS[date.getDay()] },
    { token: '{Date}', meaning: 'Occurrence date', example: `${date.getDate()} ${MONTHS[m].slice(0, 3)} ${date.getFullYear()}` },
    { token: '{Anchor}', meaning: 'Anchor date (anchor rules)', example: 'e.g. licence expiry date' },
  ];
}
