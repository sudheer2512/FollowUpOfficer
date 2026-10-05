import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { formatDateOnly, toTimeInput, toTimeOnly } from '../../../../core/util/dates';
import { DateInputComponent } from '../../../../shared/forms/date-input.component';
import { DateOnlyPipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

/** Change Due Date → PUT /api/tasks/{id} with dueDate / dueTime / clearDueTime / changeReason. */
@Component({
  selector: 'fu-due-date-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, DateInputComponent, IconComponent, DateOnlyPipe],
  template: `
    <fu-modal title="Change due date" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" (closed)="closed.emit()">
      <form id="due-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        <dl class="kv small">
          <dt>Current due date</dt>
          <dd>{{ detail().summary.dueDate ? (detail().summary.dueDate | dateOnly: 'weekday') : 'Not set' }}</dd>
          @if (detail().originalDueDate) {
            <dt>Original due date</dt>
            <dd>
              {{ detail().originalDueDate | dateOnly: 'weekday' }}
              @if (detail().dueDateChangeCount > 0) {
                <span class="chip sm tone-warning">changed {{ detail().dueDateChangeCount }} time{{ detail().dueDateChangeCount === 1 ? '' : 's' }}</span>
              }
            </dd>
          }
          @if (detail().hardDeadline) {
            <dt>Hard deadline</dt>
            <dd class="text-danger">{{ detail().hardDeadline | dateOnly: 'weekday' }}</dd>
          }
        </dl>
        <div class="form-grid">
          <div class="field">
            <label for="dd-date" class="req">New due date</label>
            <fu-date-input inputId="dd-date" formControlName="dueDate" [quickPicks]="true" />
          </div>
          <div class="field">
            <label for="dd-time">Due time</label>
            <input id="dd-time" class="input" type="time" formControlName="dueTime" />
            <div class="hint">Leave empty for end of day.</div>
          </div>
        </div>
        @if (beyondHardDeadline()) {
          <div class="alert tone-warning small"><fu-icon name="alert-triangle" /> The new date is after the hard deadline of {{ hardDeadlineText() }}.</div>
        }
        <div class="field">
          <label for="dd-reason" class="req">Reason for the change</label>
          <textarea id="dd-reason" class="textarea" rows="3" formControlName="changeReason" maxlength="1000" placeholder="Recorded in the audit trail and shown on the timeline"></textarea>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="due-form" class="btn btn-primary" [disabled]="busy() || form.invalid">Change due date</button>
      </ng-container>
    </fu-modal>
  `,
})
export class DueDateDialogComponent extends TaskDialogBase implements OnInit {
  protected readonly form = inject(FormBuilder).nonNullable.group({
    dueDate: ['', Validators.required],
    dueTime: [''],
    changeReason: ['', [Validators.required, Validators.maxLength(1000), Validators.pattern(/\S/)]],
  });

  ngOnInit(): void {
    const d = this.detail();
    this.form.patchValue({ dueDate: d.summary.dueDate ?? '', dueTime: toTimeInput(d.dueTime) });
  }

  protected beyondHardDeadline(): boolean {
    const hard = this.detail().hardDeadline;
    const due = this.form.controls.dueDate.value;
    return !!hard && !!due && due > hard;
  }

  protected hardDeadlineText(): string {
    return formatDateOnly(this.detail().hardDeadline);
  }

  protected submit(): void {
    if (this.form.invalid) return;
    const { dueDate, dueTime, changeReason } = this.form.getRawValue();
    const time = toTimeOnly(dueTime);
    this.run(
      this.api.update(this.taskId(), {
        dueDate,
        dueTime: time,
        clearDueTime: !time && !!this.detail().dueTime,
        changeReason: changeReason.trim(),
      }),
      `Due date changed to ${formatDateOnly(dueDate, { withWeekday: true, short: true })}`,
    );
  }
}
