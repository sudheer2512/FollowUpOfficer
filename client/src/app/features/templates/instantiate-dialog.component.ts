import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, FormControl, FormRecord, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { PlanningApi } from '../../core/api/planning.api';
import { InstantiateTemplateRequest, TemplateDto } from '../../core/models/admin.models';
import { DateOnly, Guid } from '../../core/models/common.models';
import { CRITICALITY_VALUES, Criticality } from '../../core/models/enums';
import { todayDateOnly } from '../../core/util/dates';
import { pluralize } from '../../core/util/humanize';
import { DateInputComponent } from '../../shared/forms/date-input.component';
import { LookupSelectComponent } from '../../shared/forms/lookup-select.component';
import { PersonPickerComponent } from '../../shared/forms/person-picker.component';
import { TeamPickerComponent } from '../../shared/forms/team-picker.component';
import { HumanizePipe } from '../../shared/pipes/format.pipes';
import { IconComponent } from '../../shared/ui/icon.component';
import { ModalComponent } from '../../shared/ui/modal.component';
import { ToastService } from '../../shared/ui/toast.service';
import { maxDueOffset } from './template-tree';

/** Every role placeholder the template uses (declared roles first, then any role only referenced by an item). */
export function templateRoles(t: Pick<TemplateDto, 'roles' | 'items'>): string[] {
  const roles = [...(t.roles ?? []), ...(t.items ?? []).map((i) => i.assigneeRole ?? '')].map((r) => r.trim()).filter(Boolean);
  return [...new Set(roles)];
}

/**
 * Instantiate a template: anchor date, one person per role, optional project / team / department and criticality
 * override → creates the task tree and opens the top task.
 *   @if (instantiating(); as t) { <fu-instantiate-template-dialog [template]="t" (closed)="instantiating.set(null)" /> }
 */
@Component({
  selector: 'fu-instantiate-template-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, PersonPickerComponent, LookupSelectComponent, TeamPickerComponent, DateInputComponent, IconComponent, HumanizePipe],
  template: `
    <fu-modal title="Instantiate template" [subtitle]="template().name" size="lg" [dismissOnScrim]="false" (closed)="closed.emit()">
      <form id="fu-instantiate-form" [formGroup]="form" (ngSubmit)="submit()" novalidate class="stack-lg">
        <div class="alert tone-accent small">
          <fu-icon name="layers" [size]="15" />
          <span>{{ summary() }} Follow-up, escalation and approval settings come from the template.</span>
        </div>

        <div class="form-grid">
          <div class="field full">
            <label for="inst-title">Title</label>
            <input id="inst-title" class="input" formControlName="title" [placeholder]="template().name" maxlength="300" />
            <span class="hint">Leave empty to use the template name.</span>
          </div>
          <div class="field">
            <label for="inst-anchor" class="req">Anchor (start) date</label>
            <fu-date-input formControlName="anchorDate" inputId="inst-anchor" [quickPicks]="true" />
            @if (submitted() && form.controls.anchorDate.invalid) {
              <span class="error">Choose the date the due offsets count from.</span>
            }
          </div>
          <div class="field">
            <label for="inst-crit">Criticality</label>
            <select id="inst-crit" class="select" formControlName="criticality">
              <option value="">Template default ({{ template().criticality | humanize }})</option>
              @for (c of criticalities; track c) {
                <option [value]="c">{{ c | humanize }}</option>
              }
            </select>
          </div>
        </div>

        <fieldset class="fieldset">
          <legend>Assign roles</legend>
          @if (roles().length) {
            <div class="form-grid" formGroupName="roleAssignments">
              @for (role of roles(); track role; let i = $index) {
                <div class="field">
                  <label class="req" [for]="'inst-role-' + i">{{ role }}</label>
                  <fu-person-picker [formControlName]="role" [inputId]="'inst-role-' + i" [placeholder]="'Who is the ' + role + '?'" />
                  @if (submitted() && roleInvalid(role)) {
                    <span class="error">Assign someone to this role.</span>
                  }
                </div>
              }
            </div>
          } @else {
            <p class="small muted">This template has no role placeholders; tasks are created without assignees.</p>
          }
        </fieldset>

        <div class="form-grid-3">
          <div class="field">
            <label for="inst-project">Project</label>
            <fu-lookup-select kind="project" formControlName="projectId" selectId="inst-project" emptyLabel="No project" />
          </div>
          <div class="field">
            <span class="label">Team</span>
            <fu-team-picker formControlName="teamId" ariaLabel="Team" />
          </div>
          <div class="field">
            <label for="inst-dept">Department</label>
            <fu-lookup-select kind="department" formControlName="departmentId" selectId="inst-dept" emptyLabel="No department" />
          </div>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="fu-instantiate-form" class="btn btn-primary" [disabled]="busy()">
          <fu-icon name="play" [size]="15" /> {{ busy() ? 'Creating…' : 'Create tasks' }}
        </button>
      </ng-container>
    </fu-modal>
  `,
})
export class InstantiateTemplateDialogComponent implements OnInit {
  private readonly api = inject(PlanningApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder).nonNullable;

  readonly template = input.required<TemplateDto>();
  readonly closed = output<void>();

  protected readonly criticalities = CRITICALITY_VALUES;
  protected readonly busy = signal(false);
  protected readonly submitted = signal(false);
  protected readonly roles = computed(() => templateRoles(this.template()));
  protected readonly summary = computed(() => {
    const items = this.template().items ?? [];
    const within = maxDueOffset(items);
    const count = `Creates ${pluralize(items.length, 'task')}`;
    return within > 0 ? `${count}; the last is due ${pluralize(within, 'working day')} after the anchor date.` : `${count}.`;
  });

  protected readonly form = this.fb.group({
    title: [''],
    anchorDate: this.fb.control<DateOnly | null>(todayDateOnly(), Validators.required),
    roleAssignments: new FormRecord<FormControl<Guid | null>>({}),
    projectId: this.fb.control<Guid | null>(null),
    teamId: this.fb.control<Guid | null>(null),
    departmentId: this.fb.control<Guid | null>(null),
    criticality: this.fb.control<Criticality | ''>(''),
  });

  ngOnInit(): void {
    const record = this.form.controls.roleAssignments;
    for (const role of this.roles()) record.addControl(role, new FormControl<Guid | null>(null, Validators.required));
  }

  protected roleInvalid(role: string): boolean {
    return this.form.controls.roleAssignments.get([role])?.invalid ?? false;
  }

  protected submit(): void {
    this.submitted.set(true);
    if (this.form.invalid || this.busy()) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const roleAssignments: Record<string, Guid> = {};
    for (const [role, personId] of Object.entries(v.roleAssignments)) {
      if (personId) roleAssignments[role] = personId;
    }
    const t = this.template();
    const req: InstantiateTemplateRequest = {
      title: v.title.trim() || null,
      anchorDate: v.anchorDate,
      roleAssignments,
      projectId: v.projectId,
      teamId: v.teamId,
      departmentId: v.departmentId,
      criticality: v.criticality || null,
    };

    this.busy.set(true);
    this.api.instantiateTemplate(t.id, req).subscribe({
      next: (detail) => {
        this.busy.set(false);
        const task = detail?.summary;
        this.toast.success(task ? `Created ${task.number} from “${t.name}”` : `Tasks created from “${t.name}”`);
        this.closed.emit();
        if (task?.id) void this.router.navigate(['/tasks', task.id]);
      },
      error: () => this.busy.set(false),
    });
  }
}
