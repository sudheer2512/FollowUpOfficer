import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { FOLLOW_UP_PAUSE_REASON_VALUES, FollowUpPauseReason } from '../../../../core/models/enums';
import { addDays, formatDateOnly, todayDateOnly } from '../../../../core/util/dates';
import { DateInputComponent } from '../../../../shared/forms/date-input.component';
import { HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

/** Snooze follow-up → POST /api/tasks/{id}/snooze (reason + resume date + note). */
@Component({
  selector: 'fu-snooze-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, DateInputComponent, HumanizePipe],
  template: `
    <fu-modal title="Snooze follow-up" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" (closed)="closed.emit()">
      <form id="snooze-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        <p class="small muted">
          Automatic reminders stop until the resume date — for example while the task waits for a customer or vendor. The task still appears
          as overdue if its due date passes.
        </p>
        <div class="form-grid">
          <div class="field">
            <label for="sz-reason" class="req">Waiting on</label>
            <select id="sz-reason" class="select" formControlName="reason">
              @for (r of reasons; track r) {
                <option [value]="r">{{ r | humanize }}</option>
              }
            </select>
          </div>
          <div class="field">
            <label for="sz-resume" class="req">Resume on</label>
            <fu-date-input inputId="sz-resume" formControlName="resumeOn" [min]="tomorrow" [quickPicks]="true" />
          </div>
          <div class="field full">
            <label for="sz-note">Note</label>
            <textarea id="sz-note" class="textarea" rows="3" formControlName="note" maxlength="1000" placeholder="e.g. Vendor confirmed delivery on the 20th"></textarea>
          </div>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="snooze-form" class="btn btn-primary" [disabled]="busy() || form.invalid">Snooze follow-up</button>
      </ng-container>
    </fu-modal>
  `,
})
export class SnoozeDialogComponent extends TaskDialogBase {
  protected readonly reasons = FOLLOW_UP_PAUSE_REASON_VALUES;
  protected readonly tomorrow = addDays(todayDateOnly(), 1);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    reason: ['Customer' as FollowUpPauseReason, Validators.required],
    resumeOn: [addDays(todayDateOnly(), 7), Validators.required],
    note: ['', Validators.maxLength(1000)],
  });

  protected submit(): void {
    if (this.form.invalid) return;
    const { reason, resumeOn, note } = this.form.getRawValue();
    this.run(
      this.api.snooze(this.taskId(), { reason, resumeOn, note: note.trim() || null }),
      `Follow-up paused until ${formatDateOnly(resumeOn, { withWeekday: true, short: true })}`,
    );
  }
}
