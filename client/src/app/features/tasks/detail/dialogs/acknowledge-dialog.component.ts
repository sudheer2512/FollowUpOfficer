import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { AcknowledgementResponse } from '../../../../core/models/enums';
import { addDays, todayDateOnly } from '../../../../core/util/dates';
import { DateInputComponent } from '../../../../shared/forms/date-input.component';
import { DateOnlyPipe } from '../../../../shared/pipes/format.pipes';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

type NonAcceptResponse = Exclude<AcknowledgementResponse, 'Accept'>;

const COPY: Record<NonAcceptResponse, { title: string; submit: string; success: string; label: string; placeholder: string }> = {
  NeedClarification: {
    title: 'Ask for clarification',
    submit: 'Send question',
    success: 'Clarification requested',
    label: 'What needs clarifying?',
    placeholder: 'e.g. Which customers are in scope for the first phase?',
  },
  CannotAccept: {
    title: 'Cannot accept this task',
    submit: 'Decline task',
    success: 'Task declined — the delegator has been told',
    label: 'Why can’t you take it on?',
    placeholder: 'e.g. I am fully booked on the audit until the 20th',
  },
  ProposeDifferentDeadline: {
    title: 'Propose a different deadline',
    submit: 'Propose deadline',
    success: 'New deadline proposed',
    label: 'Comment',
    placeholder: 'Why the proposed date is realistic (optional)',
  },
};

/** Acknowledge with Need Clarification / Cannot Accept / Propose Different Deadline → POST /api/tasks/{id}/acknowledge. */
@Component({
  selector: 'fu-acknowledge-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, DateInputComponent, DateOnlyPipe],
  template: `
    <fu-modal [title]="copy().title" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" [dismissOnScrim]="false" (closed)="closed.emit()">
      <form id="ack-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        @if (response() === 'ProposeDifferentDeadline') {
          <div class="field">
            <label for="ack-date" class="req">Proposed due date</label>
            <fu-date-input inputId="ack-date" formControlName="proposedDueDate" [min]="today" [quickPicks]="true" />
            @if (detail().summary.dueDate) {
              <div class="hint">Currently due {{ detail().summary.dueDate | dateOnly: 'weekday' }}.</div>
            }
          </div>
        }
        <div class="field">
          <label for="ack-comment" [class.req]="commentRequired()">{{ copy().label }}</label>
          <textarea id="ack-comment" class="textarea" rows="4" formControlName="comment" maxlength="2000" [placeholder]="copy().placeholder"></textarea>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="ack-form" class="btn" [class.btn-primary]="response() !== 'CannotAccept'" [class.btn-danger]="response() === 'CannotAccept'" [disabled]="busy() || !valid()">
          {{ copy().submit }}
        </button>
      </ng-container>
    </fu-modal>
  `,
})
export class AcknowledgeDialogComponent extends TaskDialogBase {
  readonly response = input<NonAcceptResponse>('NeedClarification');

  protected readonly copy = computed(() => COPY[this.response()]);
  protected readonly commentRequired = computed(() => this.response() !== 'ProposeDifferentDeadline');
  protected readonly today = todayDateOnly();
  protected readonly form = inject(FormBuilder).nonNullable.group({
    comment: [''],
    proposedDueDate: [addDays(todayDateOnly(), 7)],
  });

  protected valid(): boolean {
    const { comment, proposedDueDate } = this.form.getRawValue();
    if (this.commentRequired() && !comment.trim()) return false;
    if (this.response() === 'ProposeDifferentDeadline' && !proposedDueDate) return false;
    return true;
  }

  protected submit(): void {
    if (!this.valid()) return;
    const { comment, proposedDueDate } = this.form.getRawValue();
    const response = this.response();
    this.run(
      this.api.acknowledge(this.taskId(), {
        response,
        comment: comment.trim() || null,
        proposedDueDate: response === 'ProposeDifferentDeadline' ? proposedDueDate : null,
      }),
      this.copy().success,
    );
  }
}
