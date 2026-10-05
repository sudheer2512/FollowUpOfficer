import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AdminApi } from '../../../core/api/admin.api';
import { DepartmentDto, DepartmentInput } from '../../../core/models/admin.models';
import { Guid } from '../../../core/models/common.models';
import { LookupsStore } from '../../../core/state/lookups.store';
import { loader } from '../../../core/util/loader';
import { PersonComponent } from '../../../shared/domain/indicators';
import { PersonPickerComponent } from '../../../shared/forms/person-picker.component';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { ModalComponent } from '../../../shared/ui/modal.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ToastService } from '../../../shared/ui/toast.service';

/** Departments table with a create / edit modal (code, name, head, parent, active). */
@Component({
  selector: 'fu-departments-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, PersonComponent, PersonPickerComponent, EmptyStateComponent, IconComponent, ModalComponent, SkeletonComponent],
  template: `
    <section class="card">
      <header class="card-header">
        <h2>Departments</h2>
        <span class="count-pill">{{ rows().length }}</span>
        <span class="spacer"></span>
        <button type="button" class="btn btn-ghost btn-icon btn-sm" aria-label="Refresh departments" (click)="reload()">
          <fu-icon name="refresh" />
        </button>
        <button type="button" class="btn btn-primary btn-sm" (click)="openNew()"><fu-icon name="plus" /> New department</button>
      </header>
      <div class="card-body flush">
        @if (data.loading() && !data.value()) {
          <fu-skeleton variant="table" [rows]="5" />
        } @else if (data.error()) {
          <div class="card-body">
            <div class="alert tone-danger" role="alert">
              <fu-icon name="alert-circle" /> <span class="spacer">{{ data.error() }}</span>
              <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
            </div>
          </div>
        } @else if (!rows().length) {
          <fu-empty icon="folder" title="No departments yet" message="Create the first department to organise teams and people.">
            <button type="button" class="btn btn-sm mt-8" (click)="openNew()"><fu-icon name="plus" /> New department</button>
          </fu-empty>
        } @else {
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th>Code</th>
                  <th>Name</th>
                  <th>Head</th>
                  <th>Parent</th>
                  <th class="num">Employees</th>
                  <th class="num">Teams</th>
                  <th>Status</th>
                  <th><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (d of rows(); track d.id) {
                  <tr class="clickable" tabindex="0" [attr.aria-label]="'Edit ' + d.name" (click)="openEdit(d)" (keydown.enter)="openEdit(d)">
                    <td class="mono nowrap">{{ d.code }}</td>
                    <td class="strong">{{ d.name }}</td>
                    <td><fu-person [person]="d.head" emptyText="—" /></td>
                    <td class="muted">{{ parentName(d.parentDepartmentId) || '—' }}</td>
                    <td class="num">{{ d.employeeCount }}</td>
                    <td class="num">{{ d.teamCount }}</td>
                    <td>
                      <span class="badge" [class.tone-good]="d.isActive">{{ d.isActive ? 'Active' : 'Inactive' }}</span>
                    </td>
                    <td class="actions">
                      <button type="button" class="btn btn-ghost btn-icon btn-xs" [attr.aria-label]="'Edit ' + d.name" (click)="openEdit(d); $event.stopPropagation()">
                        <fu-icon name="edit" [size]="14" />
                      </button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </section>

    @if (editing(); as e) {
      <fu-modal [title]="e === 'new' ? 'New department' : 'Edit department'" [subtitle]="e === 'new' ? '' : e.name" [dismissOnScrim]="false" (closed)="close()">
        <form [formGroup]="form" class="form-grid" (ngSubmit)="save()" id="department-form" novalidate>
          <div class="field">
            <label for="dep-code" class="req">Code</label>
            <input id="dep-code" class="input mono" formControlName="code" maxlength="20" autocomplete="off" />
            @if (form.controls.code.touched && form.controls.code.invalid) {
              <span class="error">A short code is required.</span>
            }
          </div>
          <div class="field">
            <label for="dep-name" class="req">Name</label>
            <input id="dep-name" class="input" formControlName="name" maxlength="120" />
            @if (form.controls.name.touched && form.controls.name.invalid) {
              <span class="error">A name is required.</span>
            }
          </div>
          <div class="field full">
            <label for="dep-head">Department head</label>
            <fu-person-picker formControlName="headId" inputId="dep-head" placeholder="Search people…" />
          </div>
          <div class="field full">
            <label for="dep-parent">Parent department</label>
            <select id="dep-parent" class="select" formControlName="parentDepartmentId">
              <option [ngValue]="null">None (top level)</option>
              @for (p of parentOptions(); track p.id) {
                <option [ngValue]="p.id">{{ p.name }}</option>
              }
            </select>
          </div>
          <div class="field full">
            <label class="switch"><input type="checkbox" formControlName="isActive" /> Active</label>
            <span class="hint">Inactive departments stay on historic tasks but are hidden from pickers.</span>
          </div>
        </form>
        <ng-container modal-footer>
          <button type="button" class="btn" (click)="close()">Cancel</button>
          <button type="submit" form="department-form" class="btn btn-primary" [disabled]="saving()">
            {{ saving() ? 'Saving…' : e === 'new' ? 'Create department' : 'Save changes' }}
          </button>
        </ng-container>
      </fu-modal>
    }
  `,
})
export class DepartmentsTabComponent {
  private readonly api = inject(AdminApi);
  private readonly lookups = inject(LookupsStore);
  private readonly toast = inject(ToastService);

  protected readonly data = loader<DepartmentDto[]>();
  protected readonly rows = computed(() =>
    (this.data.value() ?? [])
      .map((d) => ({ ...d, employeeCount: d.employeeCount ?? 0, teamCount: d.teamCount ?? 0 }))
      .sort((a, b) => (a.name ?? '').localeCompare(b.name ?? '')),
  );
  protected readonly editing = signal<DepartmentDto | 'new' | null>(null);
  protected readonly saving = signal(false);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: ['', [Validators.required, Validators.maxLength(20)]],
    name: ['', [Validators.required, Validators.maxLength(120)]],
    headId: [null as Guid | null],
    parentDepartmentId: [null as Guid | null],
    isActive: [true],
  });

  /** A department cannot be its own parent. */
  protected readonly parentOptions = computed(() => {
    const e = this.editing();
    const selfId = e && e !== 'new' ? e.id : null;
    return this.rows().filter((d) => d.id !== selfId);
  });

  constructor() {
    this.reload();
  }

  protected reload(): void {
    this.data.load(this.api.departments());
  }

  protected parentName(id: Guid | null | undefined): string {
    return id ? (this.rows().find((d) => d.id === id)?.name ?? '') : '';
  }

  protected openNew(): void {
    this.form.reset({ code: '', name: '', headId: null, parentDepartmentId: null, isActive: true });
    this.editing.set('new');
  }

  protected openEdit(d: DepartmentDto): void {
    this.form.reset({
      code: d.code ?? '',
      name: d.name ?? '',
      headId: d.head?.id ?? null,
      parentDepartmentId: d.parentDepartmentId ?? null,
      isActive: d.isActive ?? true,
    });
    this.editing.set(d);
  }

  protected close(): void {
    this.editing.set(null);
  }

  protected save(): void {
    const e = this.editing();
    if (!e || this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const input: DepartmentInput = {
      code: v.code.trim(),
      name: v.name.trim(),
      headId: v.headId,
      parentDepartmentId: v.parentDepartmentId,
      isActive: v.isActive,
    };
    this.saving.set(true);
    const request = e === 'new' ? this.api.createDepartment(input) : this.api.updateDepartment(e.id, input);
    request.subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.success(e === 'new' ? `Department “${input.name}” created` : `Department “${input.name}” saved`);
        this.close();
        this.reload();
        this.lookups.refresh().subscribe({ error: () => undefined });
      },
      error: () => this.saving.set(false),
    });
  }
}
