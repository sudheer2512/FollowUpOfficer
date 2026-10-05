import { ChangeDetectionStrategy, Component, computed, forwardRef, inject, input, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Guid, NamedRef } from '../../core/models/common.models';
import { LookupsStore } from '../../core/state/lookups.store';

export type LookupKind = 'department' | 'project' | 'category' | 'policy' | 'template' | 'team';

let selectSeq = 0;

/**
 * <select> over one lookup list, with an empty "None" option. Value: Guid | null.
 *   <fu-lookup-select kind="project" formControlName="projectId" emptyLabel="No project" />
 */
@Component({
  selector: 'fu-lookup-select',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => LookupSelectComponent), multi: true }],
  template: `<select
    class="select"
    [id]="selectId()"
    [attr.aria-label]="ariaLabel() || null"
    [disabled]="disabled()"
    [value]="value() ?? ''"
    (change)="onSelect($any($event.target).value)"
    (blur)="onTouched()"
  >
    <option value="">{{ emptyLabel() }}</option>
    @for (o of options(); track o.id) {
      <option [value]="o.id" [selected]="o.id === value()">{{ o.name }}</option>
    }
  </select>`,
  host: { style: 'display:block;min-width:0' },
})
export class LookupSelectComponent implements ControlValueAccessor {
  private readonly store = inject(LookupsStore);
  readonly kind = input.required<LookupKind>();
  readonly emptyLabel = input('None');
  readonly ariaLabel = input('');
  readonly selectId = input(`fu-lookup-${++selectSeq}`);
  /** Optional filter, e.g. teams of one department. */
  readonly filter = input<((o: NamedRef & Record<string, unknown>) => boolean) | null>(null);

  protected readonly value = signal<Guid | null>(null);
  protected readonly disabled = signal(false);
  private onChange: (v: Guid | null) => void = () => undefined;
  protected onTouched: () => void = () => undefined;

  protected readonly options = computed<NamedRef[]>(() => {
    const d = this.store.data();
    let list: NamedRef[];
    switch (this.kind()) {
      case 'department':
        list = d.departments;
        break;
      case 'project':
        list = d.projects;
        break;
      case 'category':
        list = d.categories;
        break;
      case 'policy':
        list = d.escalationPolicies;
        break;
      case 'template':
        list = d.templates;
        break;
      case 'team':
        list = d.teams;
        break;
    }
    const f = this.filter();
    return f ? list.filter((o) => f(o as NamedRef & Record<string, unknown>)) : list;
  });

  constructor() {
    this.store.load();
  }

  writeValue(v: Guid | null | undefined): void {
    this.value.set(v ?? null);
  }
  registerOnChange(fn: (v: Guid | null) => void): void {
    this.onChange = fn;
  }
  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }
  setDisabledState(d: boolean): void {
    this.disabled.set(d);
  }

  protected onSelect(v: string): void {
    const next = v || null;
    this.value.set(next);
    this.onChange(next);
  }
}
