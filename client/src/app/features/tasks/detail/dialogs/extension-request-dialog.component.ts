import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { formatDateOnly, todayDateOnly } from '../../../../core/util/dates';
import { DateInputComponent } from '../../../../shared/forms/date-input.component';
import { DateOnlyPipe } from '../../../../shared/pipes/format.pipes';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

/** Request Extension → POST /api/tasks/{id}/extensions (proposed date + reason). */
@Component({
  selector: 'fu-extension-request-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, DateInputComponent, DateOnlyPipe],
  template: `
    <fu-modal title="Request an extension" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" (closed)="closed.emit()">
      <form id="ext-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        <div class="small muted">
          @if (detail().summary.dueDate) {
            Currently due <span class="strong">{{ detail().summary.dueDate | dateOnly: 'weekday' }}</span>.
          }
          The delegator is asked to approve the new date.
        </div>
        <div class="field">
          <label for="ext-date" class="req">Proposed due date</label>
          <fu-date-input inputId="ext-date" formControlName="proposedDueDate" [min]="today" [quickPicks]="true" />
        </div>
        <div class="field">
          <label for="ext-reason" class="req">Reason</label>
          <textarea id="ext-reason" class="textarea" rows="4" formControlName="reason" maxlength="2000" placeholder="What changed, and why the new date is realistic"></textarea>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="ext-form" class="btn btn-primary" [disabled]="busy() || form.invalid">Request extension</button>
      </ng-container>
    </fu-modal>
  `,
})
export class ExtensionRequestDialogComponent extends TaskDialogBase {
  protected readonly today = todayDateOnly();
  protected readonly form = inject(FormBuilder).nonNullable.group({
    proposedDueDate: ['', Validators.required],
    reason: ['', [Validators.required, Validators.maxLength(2000), Validators.pattern(/\S/)]],
  });

  protected submit(): void {
    if (this.form.invalid) return;
    const { proposedDueDate, reason } = this.form.getRawValue();
    this.run(
      this.api.requestExtension(this.taskId(), { proposedDueDate, reason: reason.trim() }),
      `Extension to ${formatDateOnly(proposedDueDate, { withWeekday: true, short: true })} requested`,
    );
  }
}
