import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AdminApi } from '../../../core/api/admin.api';
import { EmployeeDto, EmployeeInput } from '../../../core/models/admin.models';
import { Guid } from '../../../core/models/common.models';
import { NOTIFY_CHANNEL_VALUES } from '../../../core/models/enums';
import { parseFlags, toFlags } from '../../../core/util/flags';
import { LookupSelectComponent } from '../../../shared/forms/lookup-select.component';
import { PersonPickerComponent } from '../../../shared/forms/person-picker.component';
import { HumanizePipe } from '../../../shared/pipes/format.pipes';
import { ModalComponent } from '../../../shared/ui/modal.component';
import { checkedOf, formView, inOrder, textOrNull, toggleInList } from '../shared/form-view';

/** Create / edit an employee's profile (EmployeeInput). Login and roles live in the account modal. */
@Component({
  selector: 'fu-employee-edit-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, LookupSelectComponent, PersonPickerComponent, HumanizePipe, ModalComponent],
  template: `
    <fu-modal [title]="employee() ? 'Edit employee' : 'New employee'" [subtitle]="employee()?.displayName ?? ''" size="lg" [dismissOnScrim]="false" (closed)="closed.emit()">
      <form [formGroup]="form" class="form-grid" (ngSubmit)="save()" id="employee-form" novalidate>
        <div class="field">
          <label for="emp-code" class="req">Employee code</label>
          <input id="emp-code" class="input mono" formControlName="employeeCode" maxlength="20" autocomplete="off" />
          @if (form.controls.employeeCode.touched && form.controls.employeeCode.invalid) {
            <span class="error">An employee code is required.</span>
          }
        </div>
        <div class="field">
          <label for="emp-name" class="req">Display name</label>
          <input id="emp-name" class="input" formControlName="displayName" maxlength="160" />
          @if (form.controls.displayName.touched && form.controls.displayName.invalid) {
            <span class="error">A display name is required.</span>
          }
        </div>
        <div class="field">
          <label for="emp-email" class="req">Email</label>
          <input id="emp-email" class="input" type="email" formControlName="email" autocomplete="off" />
          @if (form.controls.email.touched && form.controls.email.invalid) {
            <span class="error">Enter a valid email address.</span>
          }
        </div>
        <div class="field">
          <label for="emp-phone">Phone</label>
          <input id="emp-phone" class="input" type="tel" formControlName="phone" />
        </div>
        <div class="field">
          <label for="emp-title">Job title</label>
          <input id="emp-title" class="input" formControlName="jobTitle" maxlength="120" />
        </div>
        <div class="field">
          <label for="emp-dep">Department</label>
          <fu-lookup-select kind="department" formControlName="departmentId" selectId="emp-dep" emptyLabel="No department" />
        </div>
        <div class="field full">
          <label for="emp-manager">Manager</label>
          <fu-person-picker formControlName="managerId" inputId="emp-manager" [excludeIds]="selfExclusion()" placeholder="Search for the reporting manager…" />
        </div>
        <fieldset class="fieldset full">
          <legend>Preferred channels</legend>
          <div class="checks">
            @for (c of channelValues; track c) {
              <label class="check">
                <input type="checkbox" [checked]="state().value.preferredChannels.includes(c)" (change)="toggleChannel(c, $event)" />
                {{ c | humanize }}
              </label>
            }
          </div>
          <div class="hint mt-4">Reminders and escalations use these channels when a policy does not say otherwise.</div>
        </fieldset>
        <div class="field full">
          <label class="switch"><input type="checkbox" formControlName="isActive" /> Active employee</label>
          <span class="hint">Inactive employees cannot be assigned new work and are hidden from pickers.</span>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="employee-form" class="btn btn-primary" [disabled]="saving()">
          {{ saving() ? 'Saving…' : employee() ? 'Save changes' : 'Create employee' }}
        </button>
      </ng-container>
    </fu-modal>
  `,
})
export class EmployeeEditModalComponent implements OnInit {
  private readonly api = inject(AdminApi);

  /** The employee being edited, or null to create one. */
  readonly employee = input<EmployeeDto | null>(null);
  readonly saved = output<EmployeeDto>();
  readonly closed = output<void>();

  protected readonly channelValues = NOTIFY_CHANNEL_VALUES;
  protected readonly saving = signal(false);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    employeeCode: ['', [Validators.required, Validators.maxLength(20)]],
    displayName: ['', [Validators.required, Validators.maxLength(160)]],
    email: ['', [Validators.required, Validators.email]],
    jobTitle: [''],
    phone: [''],
    departmentId: [null as Guid | null],
    managerId: [null as Guid | null],
    isActive: [true],
    preferredChannels: [['Email', 'InApp'] as string[]],
  });
  protected readonly state = formView(this.form);

  /** Nobody reports to themselves. */
  protected readonly selfExclusion = computed(() => {
    const id = this.employee()?.id;
    return id ? [id] : [];
  });

  ngOnInit(): void {
    const e = this.employee();
    if (!e) return;
    this.form.reset({
      employeeCode: e.employeeCode ?? '',
      displayName: e.displayName ?? '',
      email: e.email ?? '',
      jobTitle: e.jobTitle ?? '',
      phone: e.phone ?? '',
      departmentId: e.department?.id ?? null,
      managerId: e.manager?.id ?? null,
      isActive: e.isActive ?? true,
      preferredChannels: parseFlags(e.preferredChannels),
    });
  }

  protected toggleChannel(channel: string, event: Event): void {
    toggleInList(this.form.controls.preferredChannels, channel, checkedOf(event));
  }

  protected save(): void {
    if (this.saving()) return;
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    const input: EmployeeInput = {
      employeeCode: v.employeeCode.trim(),
      displayName: v.displayName.trim(),
      email: v.email.trim(),
      jobTitle: textOrNull(v.jobTitle),
      phone: textOrNull(v.phone),
      departmentId: v.departmentId,
      managerId: v.managerId,
      isActive: v.isActive,
      preferredChannels: toFlags(inOrder(NOTIFY_CHANNEL_VALUES, v.preferredChannels)),
    };
    const existing = this.employee();
    this.saving.set(true);
    const request = existing ? this.api.updateEmployee(existing.id, input) : this.api.createEmployee(input);
    request.subscribe({
      next: (dto) => {
        this.saving.set(false);
        this.saved.emit(dto);
      },
      error: () => this.saving.set(false),
    });
  }
}
