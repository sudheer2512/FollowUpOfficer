import { ChangeDetectionStrategy, Component, computed, forwardRef, inject, input, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { LookupsStore } from '../../core/state/lookups.store';
import { IconComponent } from '../ui/icon.component';

let tagSeq = 0;

/** Free-text tags with suggestions from lookups. Enter or comma adds a tag. Value: string[]. */
@Component({
  selector: 'fu-tag-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => TagInputComponent), multi: true }],
  template: `<div class="box">
      @for (t of value(); track t) {
        <span class="chip">#{{ t }}
          <button type="button" class="x" [attr.aria-label]="'Remove tag ' + t" (click)="remove(t)"><fu-icon name="x" [size]="12" /></button>
        </span>
      }
      <input
        class="q"
        type="text"
        [id]="inputId()"
        [attr.list]="listId"
        [placeholder]="value().length ? '' : placeholder()"
        [attr.aria-label]="placeholder()"
        [value]="query()"
        (input)="query.set($any($event.target).value)"
        (keydown)="onKey($event)"
        (blur)="commit()"
      />
      <datalist [id]="listId">
        @for (s of suggestions(); track s) {
          <option [value]="s"></option>
        }
      </datalist>
    </div>`,
  styles: `
    :host { display: block; min-width: 0; }
    .box { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; min-height: 34px; padding: 3px 6px; border: 1px solid var(--border-strong); border-radius: var(--radius-sm); background: var(--surface); }
    .box:focus-within { border-color: var(--accent); box-shadow: var(--focus); }
    .q { flex: 1 1 100px; min-width: 80px; border: 0; outline: none; background: transparent; height: 26px; padding: 0 4px; font-size: 13.5px; }
  `,
})
export class TagInputComponent implements ControlValueAccessor {
  private readonly store = inject(LookupsStore);
  readonly placeholder = input('Add tags…');
  readonly inputId = input(`fu-tags-${++tagSeq}`);
  protected readonly listId = `fu-tags-list-${tagSeq}`;
  protected readonly value = signal<string[]>([]);
  protected readonly query = signal('');
  private onChange: (v: string[]) => void = () => undefined;

  protected readonly suggestions = computed(() => this.store.tags().filter((t) => !this.value().includes(t)));

  constructor() {
    this.store.load();
  }

  writeValue(v: string[] | null | undefined): void {
    this.value.set([...(v ?? [])]);
  }
  registerOnChange(fn: (v: string[]) => void): void {
    this.onChange = fn;
  }
  registerOnTouched(): void {
    /* not tracked */
  }

  protected onKey(e: KeyboardEvent): void {
    if (e.key === 'Enter' || e.key === ',') {
      e.preventDefault();
      this.commit();
    } else if (e.key === 'Backspace' && !this.query() && this.value().length) {
      this.remove(this.value()[this.value().length - 1]);
    }
  }

  protected commit(): void {
    const t = this.query().trim().replace(/^#/, '').replace(/,$/, '');
    if (t && !this.value().includes(t)) {
      this.value.set([...this.value(), t]);
      this.onChange([...this.value()]);
    }
    this.query.set('');
  }

  protected remove(t: string): void {
    this.value.set(this.value().filter((x) => x !== t));
    this.onChange([...this.value()]);
  }
}
