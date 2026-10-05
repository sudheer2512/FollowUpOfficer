import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  forwardRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { PersonLookup } from '../../core/models/admin.models';
import { Guid } from '../../core/models/common.models';
import { LookupsStore } from '../../core/state/lookups.store';
import { AvatarComponent } from '../domain/indicators';
import { IconComponent } from '../ui/icon.component';

let pickerSeq = 0;

/**
 * Searchable person picker (typeahead over GET /api/lookups people). Works with reactive forms and ngModel.
 *   Single:   <fu-person-picker formControlName="primaryAssigneeId" />            value: Guid | null
 *   Multiple: <fu-person-picker formControlName="followerIds" [multiple]="true" /> value: Guid[]
 * Keyboard: type to filter, ↑/↓ to move, Enter to pick, Backspace removes the last chip.
 */
@Component({
  selector: 'fu-person-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AvatarComponent, IconComponent],
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => PersonPickerComponent), multi: true }],
  template: `
    <div class="box" [class.disabled]="disabled()" [class.focused]="open()" (click)="focusInput()">
      @for (p of selectedPeople(); track p.id) {
        <span class="chip sel">
          <fu-avatar [name]="p.name" size="sm" />
          {{ p.name }}
          @if (!disabled()) {
            <button type="button" class="x" [attr.aria-label]="'Remove ' + p.name" (click)="remove(p.id, $event)"><fu-icon name="x" [size]="12" /></button>
          }
        </span>
      }
      @if (multiple() || selectedPeople().length === 0) {
        <input
          #inp
          class="q"
          type="text"
          role="combobox"
          autocomplete="off"
          [id]="inputId()"
          [attr.aria-expanded]="open()"
          [attr.aria-controls]="listId"
          [attr.aria-activedescendant]="open() && results().length ? listId + '-' + active() : null"
          [attr.aria-label]="ariaLabel() || placeholder()"
          [placeholder]="selectedPeople().length ? '' : placeholder()"
          [disabled]="disabled()"
          [value]="query()"
          (input)="onInput($any($event.target).value)"
          (focus)="open.set(true)"
          (blur)="onBlur()"
          (keydown)="onKey($event)"
        />
      }
    </div>
    @if (open() && !disabled()) {
      <ul class="menu list-pop" role="listbox" [id]="listId">
        @for (p of results(); track p.id; let i = $index) {
          <li
            role="option"
            class="menu-item"
            [id]="listId + '-' + i"
            [class.hl]="i === active()"
            [attr.aria-selected]="i === active()"
            (mousedown)="$event.preventDefault()"
            (click)="pick(p)"
            (mouseenter)="active.set(i)"
          >
            <fu-avatar [name]="p.name" size="sm" />
            <span class="who">
              <span class="truncate">{{ p.name }}</span>
              <span class="xsmall muted truncate">{{ p.jobTitle }}{{ p.departmentName ? ' · ' + p.departmentName : '' }}</span>
            </span>
          </li>
        } @empty {
          <li class="menu-label">{{ store.loaded() ? 'No matching people' : 'Loading people…' }}</li>
        }
      </ul>
    }
  `,
  styles: `
    :host { position: relative; display: block; min-width: 0; }
    .box { display: flex; flex-wrap: wrap; align-items: center; gap: 4px; min-height: 34px; padding: 3px 6px; border: 1px solid var(--border-strong); border-radius: var(--radius-sm); background: var(--surface); cursor: text; }
    .box:hover { border-color: var(--subtle); }
    .box.focused { border-color: var(--accent); box-shadow: var(--focus); }
    .box.disabled { background: var(--surface-2); cursor: default; }
    .q { flex: 1 1 120px; min-width: 80px; border: 0; outline: none; background: transparent; height: 26px; padding: 0 4px; font-size: 13.5px; }
    .chip.sel { height: 24px; padding: 0 6px 0 2px; background: var(--accent-soft); color: var(--accent-text); }
    .list-pop { left: 0; right: 0; top: calc(100% + 4px); max-height: 280px; overflow-y: auto; list-style: none; margin: 0; }
    .menu-item.hl { background: var(--surface-hover); }
    .who { display: flex; flex-direction: column; min-width: 0; line-height: 1.25; }
  `,
})
export class PersonPickerComponent implements ControlValueAccessor {
  private readonly el = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly store = inject(LookupsStore);

  readonly multiple = input(false);
  readonly placeholder = input('Search people…');
  readonly ariaLabel = input('');
  readonly inputId = input(`fu-person-${++pickerSeq}`);
  /** People that may not be picked (e.g. the current assignee). */
  readonly excludeIds = input<Guid[]>([]);
  /** Restrict choices to these people (e.g. team members). */
  readonly onlyIds = input<Guid[] | null>(null);
  /** Emits the full selection after every change (handy for workload previews). */
  readonly selectionChange = output<Guid[]>();

  protected readonly listId = `fu-person-list-${pickerSeq}`;
  protected readonly value = signal<Guid[]>([]);
  protected readonly query = signal('');
  protected readonly open = signal(false);
  protected readonly active = signal(0);
  protected readonly disabled = signal(false);

  private onChange: (v: Guid | Guid[] | null) => void = () => undefined;
  private onTouched: () => void = () => undefined;

  protected readonly selectedPeople = computed(() =>
    this.value()
      .map((id) => this.store.person(id) ?? ({ id, name: 'Unknown person', isExecutive: false } as PersonLookup))
      .filter(Boolean),
  );

  protected readonly results = computed(() => {
    const q = this.query().trim().toLowerCase();
    const exclude = new Set([...this.excludeIds(), ...this.value()]);
    const only = this.onlyIds();
    const onlySet = only ? new Set(only) : null;
    return this.store
      .people()
      .filter((p) => !exclude.has(p.id) && (!onlySet || onlySet.has(p.id)))
      .filter(
        (p) =>
          !q ||
          p.name.toLowerCase().includes(q) ||
          (p.jobTitle ?? '').toLowerCase().includes(q) ||
          (p.departmentName ?? '').toLowerCase().includes(q) ||
          (p.email ?? '').toLowerCase().includes(q),
      )
      .slice(0, 30);
  });

  constructor() {
    this.store.load();
  }

  writeValue(v: Guid | Guid[] | null | undefined): void {
    if (Array.isArray(v)) this.value.set([...v]);
    else this.value.set(v ? [v] : []);
  }

  registerOnChange(fn: (v: Guid | Guid[] | null) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled.set(isDisabled);
  }

  protected focusInput(): void {
    this.el.nativeElement.querySelector<HTMLInputElement>('input.q')?.focus();
  }

  protected onInput(text: string): void {
    this.query.set(text);
    this.active.set(0);
    this.open.set(true);
  }

  protected onBlur(): void {
    setTimeout(() => {
      this.open.set(false);
      this.onTouched();
    }, 120);
  }

  protected onKey(e: KeyboardEvent): void {
    const list = this.results();
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      this.open.set(true);
      this.active.set(Math.min(list.length - 1, this.active() + 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      this.active.set(Math.max(0, this.active() - 1));
    } else if (e.key === 'Enter') {
      if (this.open() && list[this.active()]) {
        e.preventDefault();
        this.pick(list[this.active()]);
      }
    } else if (e.key === 'Escape') {
      if (this.open()) {
        e.stopPropagation();
        this.open.set(false);
      }
    } else if (e.key === 'Backspace' && !this.query() && this.value().length) {
      this.remove(this.value()[this.value().length - 1]);
    }
  }

  protected pick(p: PersonLookup): void {
    const next = this.multiple() ? [...this.value(), p.id] : [p.id];
    this.value.set(next);
    this.query.set('');
    this.active.set(0);
    if (!this.multiple()) this.open.set(false);
    this.emit();
    if (!this.multiple()) setTimeout(() => this.el.nativeElement.querySelector<HTMLButtonElement>('button.x')?.focus());
  }

  protected remove(id: Guid, e?: Event): void {
    e?.stopPropagation();
    this.value.set(this.value().filter((x) => x !== id));
    this.emit();
    setTimeout(() => this.focusInput());
  }

  private emit(): void {
    const v = this.value();
    this.onChange(this.multiple() ? [...v] : (v[0] ?? null));
    this.selectionChange.emit([...v]);
  }
}
