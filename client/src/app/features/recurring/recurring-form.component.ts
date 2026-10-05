import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  viewChild,
} from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { DateOnly } from '../../core/models/common.models';
import {
  CATCH_UP_POLICY_VALUES,
  CRITICALITY_VALUES,
  DUE_RULE_KIND_VALUES,
  DayOfWeek,
  HOLIDAY_HANDLING_VALUES,
  HolidayHandling,
  MONTHLY_PATTERN_VALUES,
  RECURRENCE_FREQUENCY_VALUES,
  TASK_PRIORITY_VALUES,
  TASK_VISIBILITY_VALUES,
  WEEKDAYS_MONDAY_FIRST,
} from '../../core/models/enums';
import { formatDateOnly, monthName } from '../../core/util/dates';
import { humanize, pluralize } from '../../core/util/humanize';
import { describeDueRule, describeFollowUp, describeRecurrence } from '../../core/util/schedule-text';
import { DateInputComponent } from '../../shared/forms/date-input.component';
import { FollowUpPickerComponent } from '../../shared/forms/follow-up-picker.component';
import { LookupSelectComponent } from '../../shared/forms/lookup-select.component';
import { PersonPickerComponent } from '../../shared/forms/person-picker.component';
import { TeamPickerComponent } from '../../shared/forms/team-picker.component';
import { DateOnlyPipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { DropdownComponent } from '../../shared/ui/dropdown.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { rawValueOfInput } from './form-signal';
import { RecurrencePreviewComponent } from './recurrence-preview.component';
import {
  CATCH_UP_TEXT,
  DUE_KIND_LABEL,
  HOLIDAY_HANDLING_TEXT,
  INTERVAL_FREQUENCIES,
  MONTHLY_PATTERN_LABEL,
  MONTH_BASED_FREQUENCIES,
  MONTH_LIST_FREQUENCIES,
  RecurringForm,
  WEEK_ORDINALS,
  anchorOffsetSentence,
  dueFromForm,
  previewRequestOf,
  ruleFromForm,
  titleTokens,
} from './recurring-form.model';

let formSeq = 0;

const INTERVAL_UNIT: Partial<Record<string, string>> = { Daily: 'day', Weekly: 'week', Monthly: 'month', Yearly: 'year' };

/**
 * Recurring-definition form (spec §9): basics, rule builder with a live sentence, due rule, holiday and downtime
 * handling, follow-up, checklist, range and controls, plus a live preview of the next occurrences.
 * The parent owns the FormGroup (createRecurringForm) and the submit button:
 *   <fu-recurring-form [form]="form" />                 — /recurring/new and edit
 *   <fu-recurring-form [form]="form" [collapsible]="true"><x rf-side /></fu-recurring-form>  — Delegate dialog
 * Content marked `rf-side` is projected under the preview in the side column.
 */
@Component({
  selector: 'fu-recurring-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    IconComponent,
    DropdownComponent,
    PersonPickerComponent,
    TeamPickerComponent,
    LookupSelectComponent,
    DateInputComponent,
    FollowUpPickerComponent,
    RecurrencePreviewComponent,
    HumanizePipe,
    DateOnlyPipe,
  ],
  templateUrl: './recurring-form.component.html',
  styleUrl: './recurring-form.component.css',
})
export class RecurringFormComponent {
  private readonly injector = inject(Injector);
  private readonly titleInput = viewChild<ElementRef<HTMLInputElement>>('titleInput');
  private readonly autoId = `rf${++formSeq}`;

  readonly form = input.required<RecurringForm>();
  /** Start the secondary sections collapsed (the Delegate dialog keeps the long form calm). */
  readonly collapsible = input(false);
  /** Prefix for element ids, so callers can focus fields (e.g. `${prefix}-primary`). */
  readonly idPrefix = input('');
  readonly previewCount = input(8);

  protected readonly uid = computed(() => this.idPrefix() || this.autoId);
  protected readonly v = rawValueOfInput(() => this.form());
  protected readonly rule = computed(() => ruleFromForm(this.v().rule));
  protected readonly due = computed(() => dueFromForm(this.v().due));
  protected readonly sentence = computed(() => `${describeRecurrence(this.rule())}; ${describeDueRule(this.due())}.`);
  protected readonly previewRequest = computed(() => {
    const v = this.v();
    return previewRequestOf(
      { rule: this.rule(), due: this.due(), holidayHandling: v.holidayHandling, startDate: v.startDate, endDate: v.endDate, titleTemplate: v.titleTemplate },
      this.previewCount(),
    );
  });
  protected readonly tokens = computed(() => titleTokens(new Date(), this.rule().fiscalYearStartMonth));
  protected readonly titleHint = 'Tokens such as {Month} or {FY} are replaced for every occurrence — check the preview.';
  protected readonly primaryExclude = computed(() => {
    const id = this.v().primaryAssigneeId;
    return id ? [id] : [];
  });

  // ── Option lists ──
  protected readonly frequencies = RECURRENCE_FREQUENCY_VALUES;
  protected readonly patterns = MONTHLY_PATTERN_VALUES;
  protected readonly patternLabel = MONTHLY_PATTERN_LABEL;
  protected readonly dueKinds = DUE_RULE_KIND_VALUES;
  protected readonly dueKindLabel = DUE_KIND_LABEL;
  protected readonly holidayOptions = HOLIDAY_HANDLING_VALUES;
  protected readonly holidayText = HOLIDAY_HANDLING_TEXT;
  protected readonly dueHolidayOptions: HolidayHandling[] = ['PreviousWorkingDay', 'NextWorkingDay', 'Keep'];
  protected readonly catchUps = CATCH_UP_POLICY_VALUES;
  protected readonly catchUpText = CATCH_UP_TEXT;
  protected readonly weekdays = WEEKDAYS_MONDAY_FIRST;
  protected readonly ordinals = WEEK_ORDINALS;
  protected readonly priorities = TASK_PRIORITY_VALUES;
  protected readonly criticalities = CRITICALITY_VALUES;
  protected readonly visibilities = TASK_VISIBILITY_VALUES;
  protected readonly monthOptions = Array.from({ length: 12 }, (_, i) => ({ value: i + 1, short: monthName(i + 1, true), long: monthName(i + 1) }));

  // ── Derived view state ──
  protected readonly frequency = computed(() => this.v().rule.frequency);
  protected readonly isMonthBased = computed(() => MONTH_BASED_FREQUENCIES.includes(this.frequency()));
  protected readonly usesMonthList = computed(() => MONTH_LIST_FREQUENCIES.includes(this.frequency()));
  protected readonly hasInterval = computed(() => INTERVAL_FREQUENCIES.includes(this.frequency()));
  protected readonly intervalUnit = computed(() => INTERVAL_UNIT[this.frequency()] ?? '');
  protected readonly anchorSentence = computed(() => anchorOffsetSentence(this.v().rule.anchorOffsetDays));
  /** Rules that already land on working days ignore the occurrence holiday handling (RecurrenceEngine.Resolve). */
  protected readonly alreadyWorkingDays = computed(() => {
    const r = this.rule();
    return (
      r.frequency === 'WorkingDays' ||
      (MONTH_BASED_FREQUENCIES.includes(r.frequency) && (r.monthlyPattern === 'FirstWorkingDay' || r.monthlyPattern === 'LastWorkingDay'))
    );
  });
  protected readonly monthListHint = computed(() => {
    const r = this.rule();
    if (r.months.length) return '';
    const base = r.alignToFiscalYear ? `the financial-year start (${monthName(r.fiscalYearStartMonth)})` : 'the start month';
    switch (r.frequency) {
      case 'Quarterly':
        return `No months picked: every 3 months from ${base}.`;
      case 'HalfYearly':
        return `No months picked: every 6 months from ${base}.`;
      default:
        return 'No month picked: the start month.';
    }
  });
  protected readonly checklistCount = computed(() => this.v().checklist.filter((s) => s.trim()).length);
  protected readonly summaries = computed(() => {
    const v = this.v();
    const range = [
      v.startDate ? `From ${formatDateOnly(v.startDate)}` : 'From today',
      v.endDate ? `until ${formatDateOnly(v.endDate)}` : 'no end date',
      v.maxOccurrences ? `max ${v.maxOccurrences}` : '',
    ].filter(Boolean);
    const controls = [
      v.requiresApproval ? 'Approval' : '',
      v.requiresAcknowledgement ? 'Acknowledgement' : '',
      v.escalationPolicyId ? 'Escalation policy' : '',
    ].filter(Boolean);
    return {
      holidays: `${humanize(v.holidayHandling)} · ${humanize(v.catchUp)}`,
      followUp: describeFollowUp(v.followUp),
      checklist: this.checklistCount() ? pluralize(this.checklistCount(), 'item') : 'None',
      range: range.join(', '),
      controls: controls.length ? controls.join(' · ') : 'Defaults',
    };
  });

  // ── Rule builder actions ──
  protected toggleWeekday(day: DayOfWeek): void {
    const c = this.form().controls.rule.controls.daysOfWeek;
    const set = new Set(c.value);
    if (set.has(day)) set.delete(day);
    else set.add(day);
    c.setValue(this.weekdays.filter((d) => set.has(d)));
    c.markAsDirty();
  }

  protected toggleMonth(month: number): void {
    const c = this.form().controls.rule.controls.months;
    const set = new Set(c.value);
    if (set.has(month)) set.delete(month);
    else set.add(month);
    c.setValue([...set].sort((a, b) => a - b));
    c.markAsDirty();
  }

  protected addAnchor(el: HTMLInputElement): void {
    const value = el.value as DateOnly;
    el.value = '';
    if (!value) return;
    const c = this.form().controls.rule.controls.anchorDates;
    if (!c.value.includes(value)) {
      c.setValue([...c.value, value].sort());
      c.markAsDirty();
    }
  }

  protected removeAnchor(value: DateOnly): void {
    const c = this.form().controls.rule.controls.anchorDates;
    c.setValue(c.value.filter((d) => d !== value));
    c.markAsDirty();
  }

  /** Inserts a title token at the caret (or replaces the selection) and puts the caret after it. */
  protected insertToken(token: string): void {
    const control = this.form().controls.titleTemplate;
    const el = this.titleInput()?.nativeElement;
    const value = control.value ?? '';
    const start = el?.selectionStart ?? value.length;
    const end = el?.selectionEnd ?? start;
    control.setValue(value.slice(0, start) + token + value.slice(end));
    control.markAsDirty();
    if (el) {
      const caret = start + token.length;
      el.focus();
      el.setSelectionRange(caret, caret);
    }
  }

  // ── Checklist ──
  protected addChecklistItem(index = this.form().controls.checklist.length): void {
    const list = this.form().controls.checklist;
    list.insert(index, new FormControl('', { nonNullable: true }));
    list.markAsDirty();
    this.focusChecklist(index);
  }

  protected removeChecklistItem(index: number): void {
    const list = this.form().controls.checklist;
    list.removeAt(index);
    list.markAsDirty();
  }

  protected moveChecklistItem(index: number, delta: number): void {
    const list = this.form().controls.checklist;
    const target = index + delta;
    if (target < 0 || target >= list.length) return;
    const control = list.at(index);
    list.removeAt(index, { emitEvent: false });
    list.insert(target, control);
    list.markAsDirty();
    this.focusChecklist(target);
  }

  protected checklistId(index: number): string {
    return `${this.uid()}-check-${index}`;
  }

  private focusChecklist(index: number): void {
    afterNextRender(() => document.getElementById(this.checklistId(index))?.focus(), { injector: this.injector });
  }
}
