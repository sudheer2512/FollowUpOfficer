import { ChangeDetectionStrategy, Component, forwardRef, input, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { DateOnly } from '../../core/models/common.models';
import { addDays, nextWeekday, todayDateOnly } from '../../core/util/dates';

let dateSeq = 0;

/**
 * DateOnly input ("yyyy-MM-dd") with optional quick picks (Today, Tomorrow, Friday, +1 week, +1 month).
 *   <fu-date-input formControlName="dueDate" [quickPicks]="true" />
 */
@Component({
  selector: 'fu-date-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => DateInputComponent), multi: true }],
  template: `<input
      class="input"
      type="date"
      [id]="inputId()"
      [attr.aria-label]="ariaLabel() || null"
      [min]="min() ?? ''"
      [max]="max() ?? ''"
      [disabled]="disabled()"
      [value]="value() ?? ''"
      (change)="set($any($event.target).value)"
      (blur)="onTouched()"
    />
    @if (quickPicks() && !disabled()) {
      <div class="picks">
        @for (p of picks; track p.label) {
          <button type="button" class="btn btn-xs btn-ghost" (click)="set(p.value())">{{ p.label }}</button>
        }
        @if (value()) {
          <button type="button" class="btn btn-xs btn-ghost" (click)="set('')">Clear</button>
        }
      </div>
    }`,
  styles: `:host { display: block; min-width: 0; } .picks { display: flex; flex-wrap: wrap; gap: 2px; margin-top: 4px; } .picks .btn { color: var(--muted); }`,
})
export class DateInputComponent implements ControlValueAccessor {
  readonly quickPicks = input(false);
  readonly min = input<DateOnly | null>(null);
  readonly max = input<DateOnly | null>(null);
  readonly ariaLabel = input('');
  readonly inputId = input(`fu-date-${++dateSeq}`);

  protected readonly value = signal<DateOnly | null>(null);
  protected readonly disabled = signal(false);
  private onChange: (v: DateOnly | null) => void = () => undefined;
  protected onTouched: () => void = () => undefined;

  protected readonly picks = [
    { label: 'Today', value: () => todayDateOnly() },
    { label: 'Tomorrow', value: () => addDays(todayDateOnly(), 1) },
    { label: 'Friday', value: () => nextWeekday(5, true) },
    { label: '+1 week', value: () => addDays(todayDateOnly(), 7) },
    { label: '+1 month', value: () => addDays(todayDateOnly(), 30) },
  ];

  writeValue(v: DateOnly | null | undefined): void {
    this.value.set(v ? v.slice(0, 10) : null);
  }
  registerOnChange(fn: (v: DateOnly | null) => void): void {
    this.onChange = fn;
  }
  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }
  setDisabledState(d: boolean): void {
    this.disabled.set(d);
  }

  protected set(v: string): void {
    const next = v || null;
    this.value.set(next);
    this.onChange(next);
  }
}
