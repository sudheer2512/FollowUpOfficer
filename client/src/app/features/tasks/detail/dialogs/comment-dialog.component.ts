import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

/** Add Comment → POST /api/tasks/{id}/comments. */
@Component({
  selector: 'fu-comment-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent],
  template: `
    <fu-modal title="Add comment" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" [dismissOnScrim]="false" (closed)="closed.emit()">
      <form id="comment-form" [formGroup]="form" (ngSubmit)="submit()">
        <div class="field">
          <label for="cm-body" class="req">Comment</label>
          <textarea id="cm-body" class="textarea" rows="5" formControlName="body" maxlength="4000" placeholder="Visible to everyone who can see this task"></textarea>
          <div class="hint">Comments appear on the timeline. Mentioned dates such as “by Friday” may be detected as commitments.</div>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="comment-form" class="btn btn-primary" [disabled]="busy() || form.invalid">Add comment</button>
      </ng-container>
    </fu-modal>
  `,
})
export class CommentDialogComponent extends TaskDialogBase {
  protected readonly form = inject(FormBuilder).nonNullable.group({
    body: ['', [Validators.required, Validators.maxLength(4000), Validators.pattern(/\S/)]],
  });

  protected submit(): void {
    if (this.form.invalid) return;
    this.run(this.api.comment(this.taskId(), { body: this.form.getRawValue().body.trim() }), 'Comment added');
  }
}
