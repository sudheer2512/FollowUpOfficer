import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { SETTABLE_STATUS_VALUES, WorkStatus } from '../../../../core/models/enums';
import { humanize } from '../../../../core/util/humanize';
import { StatusBadgeComponent } from '../../../../shared/domain/indicators';
import { HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

/** Statuses that need an explanation for the delegator. */
const REASON_REQUIRED: WorkStatus[] = ['Blocked', 'Deferred', 'Cancelled', 'WaitingForInformation', 'WaitingForExternalParty'];

/** Change status → POST /api/tasks/{id}/status (assignee-settable statuses only). */
@Component({
  selector: 'fu-status-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, StatusBadgeComponent, HumanizePipe],
  template: `
    <fu-modal title="Change status" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" (closed)="closed.emit()">
      <form id="status-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        <div class="row small muted">Current: <fu-status [status]="detail().summary.status" [text]="detail().summary.displayStatus" /></div>
        <div class="field">
          <label for="st-status" class="req">New status</label>
          <select id="st-status" class="select" formControlName="status">
            @for (s of statuses; track s) {
              <option [value]="s">{{ s | humanize }}</option>
            }
          </select>
        </div>
        <div class="field">
          <label for="st-reason" [class.req]="reasonRequired()">Reason</label>
          <textarea
            id="st-reason"
            class="textarea"
            rows="3"
            formControlName="reason"
            maxlength="2000"
            [placeholder]="reasonRequired() ? 'Explain what is holding the task up' : 'Optional'"
          ></textarea>
          @if (selected() === 'Blocked') {
            <div class="hint">Blocked tasks are highlighted to the delegator and may escalate.</div>
          }
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="status-form" class="btn btn-primary" [disabled]="busy() || !canSubmit()">Change status</button>
      </ng-container>
    </fu-modal>
  `,
})
export class StatusDialogComponent extends TaskDialogBase implements OnInit {
  protected readonly statuses = SETTABLE_STATUS_VALUES;
  protected readonly form = inject(FormBuilder).nonNullable.group({
    status: ['InProgress' as WorkStatus, Validators.required],
    reason: ['', Validators.maxLength(2000)],
  });
  protected readonly selected = toSignal(this.form.controls.status.valueChanges, { initialValue: this.form.controls.status.value });

  ngOnInit(): void {
    const current = this.detail().summary.status;
    const initial = SETTABLE_STATUS_VALUES.includes(current) ? current : 'InProgress';
    this.form.controls.status.setValue(initial);
  }

  protected reasonRequired(): boolean {
    return REASON_REQUIRED.includes(this.selected());
  }

  protected canSubmit(): boolean {
    const { status, reason } = this.form.getRawValue();
    return this.form.valid && status !== this.detail().summary.status && (!this.reasonRequired() || reason.trim().length > 0);
  }

  protected submit(): void {
    if (!this.canSubmit()) return;
    const { status, reason } = this.form.getRawValue();
    this.run(this.api.changeStatus(this.taskId(), { status, reason: reason.trim() || null }), `Status changed to ${humanize(status)}`);
  }
}
