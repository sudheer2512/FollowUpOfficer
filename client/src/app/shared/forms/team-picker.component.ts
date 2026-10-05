import { ChangeDetectionStrategy, Component, computed, forwardRef, inject, input, output, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Guid } from '../../core/models/common.models';
import { LookupsStore } from '../../core/state/lookups.store';

let teamSeq = 0;

/**
 * Team picker. Single mode renders a <select>; multiple mode renders toggle chips.
 *   <fu-team-picker formControlName="teamId" />                      value: Guid | null
 *   <fu-team-picker formControlName="teamIds" [multiple]="true" />   value: Guid[]
 */
@Component({
  selector: 'fu-team-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => TeamPickerComponent), multi: true }],
  template: `
    @if (multiple()) {
      <div class="checks" role="group" [attr.aria-label]="ariaLabel() || 'Teams'">
        @for (t of teams(); track t.id) {
          <button
            type="button"
            class="chip selectable"
            [class.on]="value().includes(t.id)"
            [attr.aria-pressed]="value().includes(t.id)"
            [disabled]="disabled()"
            (click)="toggle(t.id)"
          >
            {{ t.name }}
          </button>
        } @empty {
          <span class="muted small">No teams</span>
        }
      </div>
    } @else {
      <select class="select" [id]="selectId()" [attr.aria-label]="ariaLabel() || 'Team'" [disabled]="disabled()" (change)="selectOne($any($event.target).value)" (blur)="onTouched()">
        <option value="">{{ emptyLabel() }}</option>
        @for (t of teams(); track t.id) {
          <option [value]="t.id" [selected]="value()[0] === t.id">{{ t.name }}</option>
        }
      </select>
    }
  `,
  host: { style: 'display:block;min-width:0' },
})
export class TeamPickerComponent implements ControlValueAccessor {
  private readonly store = inject(LookupsStore);
  readonly multiple = input(false);
  readonly emptyLabel = input('No team');
  /** id of the <select> (single mode) so a <label for> can point at it. */
  readonly selectId = input(`fu-team-${++teamSeq}`);
  readonly ariaLabel = input('');
  /** Only teams of this department. */
  readonly departmentId = input<Guid | null>(null);
  readonly selectionChange = output<Guid[]>();

  protected readonly value = signal<Guid[]>([]);
  protected readonly disabled = signal(false);
  private onChange: (v: Guid | Guid[] | null) => void = () => undefined;
  protected onTouched: () => void = () => undefined;

  protected readonly teams = computed(() => {
    const dep = this.departmentId();
    return this.store.teams().filter((t) => !dep || t.departmentId === dep);
  });

  constructor() {
    this.store.load();
  }

  writeValue(v: Guid | Guid[] | null | undefined): void {
    this.value.set(Array.isArray(v) ? [...v] : v ? [v] : []);
  }
  registerOnChange(fn: (v: Guid | Guid[] | null) => void): void {
    this.onChange = fn;
  }
  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }
  setDisabledState(d: boolean): void {
    this.disabled.set(d);
  }

  protected toggle(id: Guid): void {
    const cur = this.value();
    this.value.set(cur.includes(id) ? cur.filter((x) => x !== id) : [...cur, id]);
    this.onChange([...this.value()]);
    this.selectionChange.emit([...this.value()]);
    this.onTouched();
  }

  protected selectOne(id: string): void {
    this.value.set(id ? [id] : []);
    this.onChange(id || null);
    this.selectionChange.emit([...this.value()]);
  }
}
