import { FormBuilder } from '@angular/forms';
import { DEFAULT_DUE_RULE, DEFAULT_RECURRENCE_RULE } from '../../core/models/rules.models';
import { RecurringDefinitionDto } from '../../core/models/view.models';
import {
  anchorOffsetSentence,
  clampInt,
  createRecurringForm,
  dueFromForm,
  emptyRecurringInput,
  fiscalYearLabel,
  inputFromDto,
  isoWeek,
  patchRecurringForm,
  previewRequestOf,
  recurringInputFromForm,
  recurringProblems,
  ruleFromForm,
  titleTokens,
} from './recurring-form.model';

const fb = () => new FormBuilder().nonNullable;

describe('ruleFromForm', () => {
  const base = createRecurringForm(fb()).getRawValue().rule;

  it('keeps months only for quarterly / half-yearly / yearly rules, sorted and unique', () => {
    expect(ruleFromForm({ ...base, frequency: 'Monthly', months: [4, 1] }).months).toEqual([]);
    expect(ruleFromForm({ ...base, frequency: 'Quarterly', months: [10, 1, 4, 7, 4] }).months).toEqual([1, 4, 7, 10]);
  });

  it('clamps intervals and keeps "last" (-1) weekday ordinals', () => {
    const r = ruleFromForm({ ...base, frequency: 'Weekly', interval: 0, weekOrdinal: -1, daysOfWeek: ['Friday', 'Monday'] });
    expect(r.interval).toBe(1);
    expect(r.weekOrdinal).toBe(-1);
    expect(r.daysOfWeek).toEqual(['Monday', 'Friday']);
    expect(ruleFromForm({ ...base, frequency: 'WorkingDays', interval: 5 }).interval).toBe(1);
  });

  it('normalises the create time and drops anchor settings for other frequencies', () => {
    const r = ruleFromForm({ ...base, createTime: '7:30', anchorDates: ['2027-03-15'], anchorOffsetDays: -15 });
    expect(r.createTime).toBe('07:30:00');
    expect(r.anchorDates).toEqual([]);
    expect(r.anchorOffsetDays).toBe(0);
  });

  it('only aligns quarterly rules to the financial year when no explicit months are picked', () => {
    expect(ruleFromForm({ ...base, frequency: 'Quarterly', alignToFiscalYear: true }).alignToFiscalYear).toBe(true);
    expect(ruleFromForm({ ...base, frequency: 'Quarterly', alignToFiscalYear: true, months: [1] }).alignToFiscalYear).toBe(false);
    expect(ruleFromForm({ ...base, frequency: 'Monthly', alignToFiscalYear: true }).alignToFiscalYear).toBe(false);
  });
});

describe('dueFromForm', () => {
  it('uses the value only where the kind needs it', () => {
    expect(dueFromForm({ kind: 'SameDay', value: 4, weekday: 'Friday', dueTime: '', holidayHandling: 'Keep' }).value).toBe(0);
    expect(dueFromForm({ kind: 'DayOfMonth', value: 0, weekday: 'Friday', dueTime: '18:00', holidayHandling: 'Keep' })).toEqual({
      kind: 'DayOfMonth',
      value: 1,
      weekday: 'Friday',
      dueTime: '18:00:00',
      holidayHandling: 'Keep',
    });
  });
});

describe('recurringInputFromForm', () => {
  it('trims text, de-duplicates people and drops empty checklist rows', () => {
    const form = createRecurringForm(fb());
    patchRecurringForm(form, {
      ...emptyRecurringInput(),
      titleTemplate: '  GST return — {PrevMonthYear} ',
      code: ' ',
      primaryAssigneeId: 'p1',
      additionalAssigneeIds: ['p2', 'p1', 'p2'],
      checklist: ['Reconcile', '  ', 'File return'],
    });
    const input = recurringInputFromForm(form.getRawValue());
    expect(input.titleTemplate).toBe('GST return — {PrevMonthYear}');
    expect(input.code).toBeNull();
    expect(input.additionalAssigneeIds).toEqual(['p2']);
    expect(input.checklist).toEqual(['Reconcile', 'File return']);
    expect(input.maxOccurrences).toBeNull();
  });

  it('round-trips a stored definition through the form', () => {
    const dto = {
      id: 'd1',
      code: 'REC-1',
      titleTemplate: 'Board pack {Quarter}',
      owner: { id: 'o1', name: 'CEO' },
      primaryAssignee: { id: 'p1', name: 'Rahul' },
      additionalAssignees: [{ id: 'p2', name: 'Meera' }],
      team: { id: 't1', name: 'Finance' },
      priority: 'High',
      criticality: 'Critical',
      visibility: 'ManagementOnly',
      rule: { ...DEFAULT_RECURRENCE_RULE, frequency: 'Quarterly', months: [1, 4, 7, 10], createTime: '09:15:00' },
      due: { ...DEFAULT_DUE_RULE, kind: 'AfterWorkingDays', value: 5, dueTime: '18:00:00' },
      followUp: null,
      holidayHandling: 'PreviousWorkingDay',
      catchUp: 'GenerateWithinWindow',
      catchUpWindowDays: 5,
      requiresApproval: true,
      requiresAcknowledgement: false,
      checklist: ['Collect MIS'],
      startDate: '2026-01-01',
      endDate: null,
      maxOccurrences: 12,
    } as unknown as RecurringDefinitionDto;
    const form = createRecurringForm(fb());
    patchRecurringForm(form, inputFromDto(dto));
    const input = recurringInputFromForm(form.getRawValue());
    expect(input.ownerId).toBe('o1');
    expect(input.teamId).toBe('t1');
    expect(input.rule.months).toEqual([1, 4, 7, 10]);
    expect(input.rule.createTime).toBe('09:15:00');
    expect(input.due).toEqual({ ...DEFAULT_DUE_RULE, kind: 'AfterWorkingDays', value: 5, dueTime: '18:00:00' });
    expect(input.catchUpWindowDays).toBe(5);
    expect(input.checklist).toEqual(['Collect MIS']);
    expect(input.maxOccurrences).toBe(12);
    expect(form.pristine).toBe(true);
  });
});

describe('recurringProblems / previewRequestOf', () => {
  it('mirrors the server validation', () => {
    expect(recurringProblems(emptyRecurringInput())).toHaveLength(2);
    const anchor = { ...emptyRecurringInput(), titleTemplate: 'Renew licence', teamId: 't1' };
    anchor.rule = { ...anchor.rule, frequency: 'AnchorDates' };
    expect(recurringProblems(anchor)).toEqual(['Add at least one anchor date (for example the licence expiry).']);
    expect(previewRequestOf(anchor, 8)).toBeNull();
    const dates = { ...anchor, startDate: '2026-10-10', endDate: '2026-10-01', rule: { ...anchor.rule, anchorDates: ['2027-03-15'] } };
    expect(recurringProblems(dates)).toEqual(['The end date is before the start date.']);
    expect(previewRequestOf(dates, 8)?.count).toBe(8);
  });
});

describe('title tokens', () => {
  it('computes examples like RecurrenceEngine.RenderTitle', () => {
    const tokens = Object.fromEntries(titleTokens(new Date(2026, 9, 6), 4).map((t) => [t.token, t.example]));
    expect(tokens['{Month}']).toBe('October');
    expect(tokens['{PrevMonthYear}']).toBe('September 2026');
    expect(tokens['{Quarter}']).toBe('Q4');
    expect(tokens['{FY}']).toBe('FY2026-27');
    expect(tokens['{FYQuarter}']).toBe('Q3 FY2026-27');
    expect(tokens['{Week}']).toBe('W41');
    expect(tokens['{Date}']).toBe('6 Oct 2026');
  });

  it('handles the January previous month, ISO week edges and calendar financial years', () => {
    const jan = Object.fromEntries(titleTokens(new Date(2027, 0, 4), 4).map((t) => [t.token, t.example]));
    expect(jan['{PrevMonthYear}']).toBe('December 2026');
    expect(jan['{FY}']).toBe('FY2026-27');
    expect(isoWeek(new Date(2027, 0, 1))).toBe(53);
    expect(fiscalYearLabel(new Date(2026, 5, 1), 1)).toBe('FY2026');
  });
});

describe('small helpers', () => {
  it('clamps integers and describes anchor offsets', () => {
    expect(clampInt('', 1, 10, 3)).toBe(3);
    expect(clampInt('42', 1, 10, 3)).toBe(10);
    expect(clampInt(null, 1, 10, 3)).toBe(3);
    expect(anchorOffsetSentence(-15)).toBe('15 days before each anchor date');
    expect(anchorOffsetSentence(0)).toBe('On each anchor date');
    expect(anchorOffsetSentence(1)).toBe('1 day after each anchor date');
  });
});
