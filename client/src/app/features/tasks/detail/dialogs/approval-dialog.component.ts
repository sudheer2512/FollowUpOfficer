import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ApprovalAction } from '../../../../core/models/enums';
import { DateOnlyPipe, DateTimePipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { EvidenceListComponent } from '../evidence-list.component';
import { latestSubmission } from '../task-detail.util';
import { TaskDialogBase } from './task-dialog-base';

const COPY: Record<ApprovalAction, { title: string; submit: string; success: string; label: string }> = {
  Confirm: { title: 'Confirm completion', submit: 'Confirm completion', success: 'Completion confirmed', label: 'Comment (optional)' },
  RequestMoreWork: { title: 'Request more work', submit: 'Request more work', success: 'More work requested', label: 'What still needs to be done?' },
  Reopen: { title: 'Reopen task', submit: 'Reopen task', success: 'Task reopened', label: 'Why is the task being reopened?' },
};

/** Approval decision on a completion → POST /api/tasks/{id}/approval (Confirm / RequestMoreWork / Reopen). */
@Component({
  selector: 'fu-approval-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, IconComponent, EvidenceListComponent, DateOnlyPipe, DateTimePipe],
  template: `
    <fu-modal [title]="copy().title" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" (closed)="closed.emit()">
      <form id="approval-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        @if (submission(); as s) {
          <div class="card pad stack sub">
            <div class="small">
              <span class="strong">{{ s.submittedBy.name }}</span>
              <span class="muted"> submitted round {{ s.round }} · {{ s.submittedAt | dateTime: 'short' }} · completed {{ s.completionDate | dateOnly }}</span>
            </div>
            @if (s.note) {
              <div class="pre small">{{ s.note }}</div>
            }
            <fu-evidence-list [evidence]="s.evidence" />
          </div>
        }
        <div class="field">
          <label for="ap-comment" [class.req]="commentRequired()">{{ copy().label }}</label>
          <textarea id="ap-comment" class="textarea" rows="4" formControlName="comment" maxlength="2000"></textarea>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button
          type="submit"
          form="approval-form"
          class="btn"
          [class.btn-primary]="action() === 'Confirm'"
          [class.btn-danger]="action() === 'Reopen'"
          [disabled]="busy() || (commentRequired() && !hasComment())"
        >
          @if (action() === 'Confirm') {
            <fu-icon name="check" [size]="15" />
          }
          {{ copy().submit }}
        </button>
      </ng-container>
    </fu-modal>
  `,
  styles: `.sub { background: var(--surface-2); box-shadow: none; }`,
})
export class ApprovalDialogComponent extends TaskDialogBase {
  readonly action = input<ApprovalAction>('Confirm');

  protected readonly copy = computed(() => COPY[this.action()]);
  protected readonly commentRequired = computed(() => this.action() !== 'Confirm');
  protected readonly submission = computed(() => latestSubmission(this.detail()));
  protected readonly form = inject(FormBuilder).nonNullable.group({ comment: [''] });

  protected hasComment(): boolean {
    return this.form.controls.comment.value.trim().length > 0;
  }

  protected submit(): void {
    if (this.commentRequired() && !this.hasComment()) return;
    this.run(this.api.approval(this.taskId(), { action: this.action(), comment: this.form.controls.comment.value.trim() || null }), this.copy().success);
  }
}
