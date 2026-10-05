import { ChangeDetectionStrategy, Component, computed, forwardRef, input, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { DateOnly } from '../../core/models/common.models';
import { DayOfWeek, WEEKDAYS_MONDAY_FIRST } from '../../core/models/enums';
import { FollowUpSchedule, NO_FOLLOW_UP } from '../../core/models/rules.models';
import { toTimeInput, toTimeOnly } from '../../core/util/dates';
import { describeFollowUp } from '../../core/util/schedule-text';
import { IconComponent } from '../ui/icon.component';

/** Preset keys shown in the picker (spec §9). */
export type FollowUpPreset =
  | 'none' | 'daily' | 'every2' | 'every3' | 'weekly' | 'fortnightly' | 'monthly' | 'weekdays' | 'dates' | 'customDays' | 'customHours';

export const FOLLOW_UP_PRESETS: { key: FollowUpPreset; label: string }[] = [
  { key: 'none', label: 'No automatic follow-up' },
  { key: 'daily', label: 'Daily' },
  { key: 'every2', label: 'Every 2 days' },
  { key: 'every3', label: 'Every 3 days' },
  { key: 'weekly', label: 'Weekly' },
  { key: 'fortnightly', label: 'Fortnightly' },
  { key: 'monthly', label: 'Monthly' },
  { key: 'weekdays', label: 'Specific weekdays' },
  { key: 'dates', label: 'Specific dates' },
  { key: 'customDays', label: 'Custom: every N days' },
  { key: 'customHours', label: 'Custom: every N hours' },
];

/** Which preset a schedule corresponds to. */
export function presetOf(s: FollowUpSchedule | null | undefined): FollowUpPreset {
  if (!s) return 'none';
  switch (s.frequency) {
    case 'None':
      return 'none';
    case 'Daily':
      return 'daily';
    case 'EveryNDays':
      return s.intervalDays === 2 ? 'every2' : s.intervalDays === 3 ? 'every3' : 'customDays';
    case 'Weekly':
      return 'weekly';
    case 'Fortnightly':
      return 'fortnightly';
    case 'Monthly':
      return 'monthly';
    case 'SpecificWeekdays':
      return 'weekdays';
    case 'SpecificDates':
      return 'dates';
    case 'Custom':
      return s.intervalHours > 0 ? 'customHours' : 'customDays';
    default:
      return 'none';
  }
}

/** Builds the schedule for a preset, keeping the shared options (time, working days, until) of `base`. */
export function scheduleForPreset(preset: FollowUpPreset, base: FollowUpSchedule = NO_FOLLOW_UP): FollowUpSchedule {
  const common = { ...NO_FOLLOW_UP, timeOfDay: base.timeOfDay, workingDaysOnly: base.workingDaysOnly, until: base.until ?? null };
  switch (preset) {
    case 'none':
      return { ...NO_FOLLOW_UP };
    case 'daily':
      return { ...common, frequency: 'Daily' };
    case 'every2':
      return { ...common, frequency: 'EveryNDays', intervalDays: 2 };
    case 'every3':
      return { ...common, frequency: 'EveryNDays', intervalDays: 3 };
    case 'weekly':
      return { ...common, frequency: 'Weekly', weekdays: base.weekdays.length ? [base.weekdays[0]] : ['Monday'] };
    case 'fortnightly':
      return { ...common, frequency: 'Fortnightly' };
    case 'monthly':
      return { ...common, frequency: 'Monthly' };
    case 'weekdays':
      return { ...common, frequency: 'SpecificWeekdays', weekdays: base.weekdays.length ? [...base.weekdays] : ['Monday', 'Thursday'] };
    case 'dates':
      return { ...common, frequency: 'SpecificDates', dates: [...(base.dates ?? [])] };
    case 'customDays':
      return { ...common, frequency: 'Custom', intervalDays: Math.max(1, base.intervalDays || 4), intervalHours: 0 };
    case 'customHours':
      return { ...common, frequency: 'Custom', intervalHours: Math.max(1, base.intervalHours || 4) };
  }
}

/**
 * Follow-up schedule picker (CVA, value FollowUpSchedule). Shows a live sentence ("Every 2 days at 10:00 until completed").
 *   <fu-follow-up-picker formControlName="followUp" />
 */
@Component({
  selector: 'fu-follow-up-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => FollowUpPickerComponent), multi: true }],
  template: `
    <div class="stack">
      <div class="row wrap gap-12">
        <select class="select preset" [attr.aria-label]="'Follow-up frequency'" [disabled]="disabled()" (change)="choose($any($event.target).value)">
          @for (p of presets; track p.key) {
            <option [value]="p.key" [selected]="p.key === preset()">{{ p.label }}</option>
          }
        </select>
        @if (preset() !== 'none') {
          @if (preset() !== 'customHours') {
            <label class="row small">
              <span class="muted">at</span>
              <input class="input time" type="time" [value]="time()" [disabled]="disabled()" (change)="patch({ timeOfDay: toTime($any($event.target).value) })" aria-label="Time of day" />
            </label>
          }
          <label class="check small">
            <input type="checkbox" [checked]="value().workingDaysOnly" [disabled]="disabled()" (change)="patch({ workingDaysOnly: $any($event.target).checked })" />
            Working days only
          </label>
        }
      </div>

      @switch (preset()) {
        @case ('weekly') {
          <div class="row wrap small">
            <span class="muted">On</span>
            @for (d of weekdays; track d) {
              <button type="button" class="chip selectable" [class.on]="value().weekdays[0] === d" [attr.aria-pressed]="value().weekdays[0] === d" (click)="patch({ weekdays: [d] })">{{ d.slice(0, 3) }}</button>
            }
          </div>
        }
        @case ('weekdays') {
          <div class="row wrap small" role="group" aria-label="Weekdays">
            @for (d of weekdays; track d) {
              <label class="check"><input type="checkbox" [checked]="value().weekdays.includes(d)" (change)="toggleDay(d, $any($event.target).checked)" />{{ d.slice(0, 3) }}</label>
            }
          </div>
        }
        @case ('dates') {
          <div class="row wrap small">
            @for (d of value().dates; track d) {
              <span class="chip">{{ d }}<button type="button" class="x" [attr.aria-label]="'Remove ' + d" (click)="removeDate(d)"><fu-icon name="x" [size]="12" /></button></span>
            }
            <input class="input input-sm date" type="date" aria-label="Add follow-up date" (change)="addDate($any($event.target)); $any($event.target).value = ''" />
          </div>
        }
        @case ('customDays') {
          <label class="row small">
            Every
            <input class="input input-sm n" type="number" min="1" max="60" [value]="value().intervalDays" (change)="patch({ intervalDays: toInt($any($event.target).value, 1) })" aria-label="Every N days" />
            days
          </label>
        }
        @case ('customHours') {
          <label class="row small">
            Every
            <input class="input input-sm n" type="number" min="1" max="12" [value]="value().intervalHours" (change)="patch({ intervalHours: toInt($any($event.target).value, 1) })" aria-label="Every N hours" />
            working hours
          </label>
        }
      }

      @if (preset() !== 'none' && showUntil()) {
        <label class="row small">
          <span class="muted">Until</span>
          <input class="input input-sm date" type="date" [value]="value().until ?? ''" (change)="patch({ until: $any($event.target).value || null })" aria-label="Follow up until" />
          <span class="muted xsmall">(empty = until completed)</span>
        </label>
      }
      <div class="hint row"><fu-icon name="repeat" [size]="13" /> {{ sentence() }}</div>
    </div>
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .preset { width: auto; min-width: 210px; }
    .time { width: 110px; }
    .n { width: 70px; }
    .date { width: 150px; }
  `,
})
export class FollowUpPickerComponent implements ControlValueAccessor {
  readonly showUntil = input(true);

  protected readonly presets = FOLLOW_UP_PRESETS;
  protected readonly weekdays = WEEKDAYS_MONDAY_FIRST.slice(0, 7);
  protected readonly value = signal<FollowUpSchedule>({ ...NO_FOLLOW_UP });
  protected readonly preset = signal<FollowUpPreset>('none');
  protected readonly disabled = signal(false);
  protected readonly sentence = computed(() => describeFollowUp(this.value()));
  protected readonly time = computed(() => toTimeInput(this.value().timeOfDay) || '10:00');

  private onChange: (v: FollowUpSchedule) => void = () => undefined;

  writeValue(v: FollowUpSchedule | null | undefined): void {
    const s: FollowUpSchedule = { ...NO_FOLLOW_UP, ...(v ?? {}), weekdays: [...(v?.weekdays ?? [])], dates: [...(v?.dates ?? [])] };
    this.value.set(s);
    this.preset.set(presetOf(s));
  }
  registerOnChange(fn: (v: FollowUpSchedule) => void): void {
    this.onChange = fn;
  }
  registerOnTouched(): void {
    /* not tracked */
  }
  setDisabledState(d: boolean): void {
    this.disabled.set(d);
  }

  protected choose(key: FollowUpPreset): void {
    this.preset.set(key);
    this.emit(scheduleForPreset(key, this.value()));
  }

  protected patch(p: Partial<FollowUpSchedule>): void {
    this.emit({ ...this.value(), ...p });
  }

  protected toggleDay(d: DayOfWeek, on: boolean): void {
    const set = new Set(this.value().weekdays);
    if (on) set.add(d);
    else set.delete(d);
    this.patch({ weekdays: this.weekdays.filter((x) => set.has(x)) });
  }

  protected addDate(el: HTMLInputElement): void {
    const d = el.value as DateOnly;
    if (d && !this.value().dates.includes(d)) this.patch({ dates: [...this.value().dates, d].sort() });
  }

  protected removeDate(d: DateOnly): void {
    this.patch({ dates: this.value().dates.filter((x) => x !== d) });
  }

  protected toTime(v: string): string {
    return toTimeOnly(v) ?? '10:00:00';
  }

  protected toInt(v: string, min: number): number {
    const n = Math.trunc(Number(v));
    return isNaN(n) ? min : Math.max(min, n);
  }

  private emit(s: FollowUpSchedule): void {
    this.value.set(s);
    this.onChange({ ...s });
  }
}
