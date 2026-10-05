import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { PlanningApi } from '../../core/api/planning.api';
import { ProjectInput } from '../../core/models/admin.models';
import { DateOnly, Guid } from '../../core/models/common.models';
import { TASK_VISIBILITY_VALUES, TaskVisibility } from '../../core/models/enums';
import { ProjectSummary } from '../../core/models/view.models';
import { LookupsStore } from '../../core/state/lookups.store';
import { diffDays } from '../../core/util/dates';
import { DateInputComponent } from '../../shared/forms/date-input.component';
import { LookupSelectComponent } from '../../shared/forms/lookup-select.component';
import { PersonPickerComponent } from '../../shared/forms/person-picker.component';
import { HumanizePipe } from '../../shared/pipes/format.pipes';
import { IconComponent } from '../../shared/ui/icon.component';
import { ModalComponent } from '../../shared/ui/modal.component';
import { ToastService } from '../../shared/ui/toast.service';

/**
 * Create / edit a project in a modal.
 *   @if (formOpen()) { <fu-project-form [project]="editing()" (saved)="onSaved($event)" (closed)="formOpen.set(false)" /> }
 *
 * ProjectSummary carries no visibility, so when editing the visibility select starts at "Keep current" and is only sent
 * when the user picks a value.
 */
@Component({
  selector: 'fu-project-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, PersonPickerComponent, LookupSelectComponent, DateInputComponent, IconComponent, HumanizePipe],
  template: `
    <fu-modal [title]="project() ? 'Edit project' : 'New project'" [subtitle]="project()?.name ?? 'Group related delegated tasks under one initiative.'" size="lg" [dismissOnScrim]="false" (closed)="closed.emit()">
      <form id="fu-project-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
        <div class="form-grid">
          <div class="field">
            <label for="pf-code" class="req">Code</label>
            <input id="pf-code" class="input" formControlName="code" maxlength="32" placeholder="e.g. UAE-EINV" autocomplete="off" />
            @if (submitted() && form.controls.code.invalid) {
              <span class="error">A short project code is required.</span>
            }
          </div>
          <div class="field">
            <label for="pf-name" class="req">Name</label>
            <input id="pf-name" class="input" formControlName="name" maxlength="200" placeholder="e.g. UAE e-Invoicing Launch" />
            @if (submitted() && form.controls.name.invalid) {
              <span class="error">A project name is required.</span>
            }
          </div>
          <div class="field full">
            <label for="pf-desc">Description</label>
            <textarea id="pf-desc" class="textarea" rows="3" formControlName="description" placeholder="Objective, scope and what success looks like"></textarea>
          </div>
          <div class="field">
            <label for="pf-owner">Owner</label>
            <fu-person-picker formControlName="ownerId" inputId="pf-owner" placeholder="Accountable owner…" />
          </div>
          <div class="field">
            <label for="pf-dept">Department</label>
            <fu-lookup-select kind="department" formControlName="departmentId" selectId="pf-dept" emptyLabel="No department" />
          </div>
          <div class="field">
            <label for="pf-start">Start date</label>
            <fu-date-input formControlName="startDate" inputId="pf-start" />
          </div>
          <div class="field">
            <label for="pf-target">Target date</label>
            <fu-date-input formControlName="targetDate" inputId="pf-target" [min]="form.controls.startDate.value" />
            @if (dateOrderInvalid()) {
              <span class="error">The target date must be on or after the start date.</span>
            }
          </div>
          <div class="field">
            <label for="pf-vis">Visibility</label>
            <select id="pf-vis" class="select" formControlName="visibility">
              @if (project()) {
                <option value="">Keep current</option>
              }
              @for (v of visibilities; track v) {
                <option [value]="v">{{ v | humanize }}</option>
              }
            </select>
            <span class="hint">Restricts who can see the project's tasks.</span>
          </div>
          <div class="field">
            <span class="label">Status</span>
            <label class="switch"><input type="checkbox" formControlName="isActive" /> Active</label>
            <span class="hint">Inactive projects are hidden from pickers and the default list.</span>
          </div>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="fu-project-form" class="btn btn-primary" [disabled]="busy()">
          <fu-icon name="check" [size]="15" />
          {{ busy() ? 'Saving…' : project() ? 'Save changes' : 'Create project' }}
        </button>
      </ng-container>
    </fu-modal>
  `,
})
export class ProjectFormComponent implements OnInit {
  private readonly api = inject(PlanningApi);
  private readonly lookups = inject(LookupsStore);
  private readonly toast = inject(ToastService);

  /** The project to edit; null creates a new one. */
  readonly project = input<ProjectSummary | null>(null);
  readonly saved = output<ProjectSummary>();
  readonly closed = output<void>();

  protected readonly visibilities = TASK_VISIBILITY_VALUES;
  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly dateOrderInvalid = signal(false);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: ['', [Validators.required, Validators.maxLength(32)]],
    name: ['', [Validators.required, Validators.maxLength(200)]],
    description: [''],
    ownerId: [null as Guid | null],
    departmentId: [null as Guid | null],
    startDate: [null as DateOnly | null],
    targetDate: [null as DateOnly | null],
    visibility: ['Normal' as TaskVisibility | ''],
    isActive: [true],
  });

  ngOnInit(): void {
    const p = this.project();
    if (!p) return;
    this.form.reset({
      code: p.code ?? '',
      name: p.name ?? '',
      description: p.description ?? '',
      ownerId: p.owner?.id ?? null,
      departmentId: p.department?.id ?? null,
      startDate: p.startDate ?? null,
      targetDate: p.targetDate ?? null,
      visibility: '',
      isActive: p.isActive ?? true,
    });
  }

  protected save(): void {
    this.submitted.set(true);
    const v = this.form.getRawValue();
    const orderBad = !!v.startDate && !!v.targetDate && diffDays(v.startDate, v.targetDate) < 0;
    this.dateOrderInvalid.set(orderBad);
    if (this.form.invalid || orderBad || this.busy()) {
      this.form.markAllAsTouched();
      return;
    }

    const input: ProjectInput = {
      code: v.code.trim(),
      name: v.name.trim(),
      description: v.description.trim() || null,
      ownerId: v.ownerId,
      departmentId: v.departmentId,
      startDate: v.startDate || null,
      targetDate: v.targetDate || null,
      visibility: v.visibility || undefined,
      isActive: v.isActive,
    };
    const existing = this.project();
    const request = existing ? this.api.updateProject(existing.id, input) : this.api.createProject(input);

    this.busy.set(true);
    request.subscribe({
      next: (summary) => {
        this.busy.set(false);
        // Project pickers read the cached lookups; refresh them so the new name/state shows everywhere.
        this.lookups.refresh().subscribe({ error: () => undefined });
        this.toast.success(existing ? 'Project updated' : `Project “${summary?.name ?? input.name}” created`);
        this.saved.emit(summary);
      },
      error: () => this.busy.set(false),
    });
  }
}
