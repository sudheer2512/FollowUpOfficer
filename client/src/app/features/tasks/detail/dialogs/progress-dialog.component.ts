import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

/** Update progress → POST /api/tasks/{id}/progress (0–100 + note). */
@Component({
  selector: 'fu-progress-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent],
  template: `
    <fu-modal title="Update progress" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" (closed)="closed.emit()">
      <form id="progress-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        <div class="field">
          <label for="pg-value">Progress <span class="num strong">{{ form.controls.progressPercent.value }}%</span></label>
          <input id="pg-value" class="range" type="range" min="0" max="100" step="5" formControlName="progressPercent" />
          <div class="row gap-4">
            @for (p of presets; track p) {
              <button type="button" class="btn btn-xs" [class.btn-primary]="form.controls.progressPercent.value === p" (click)="form.controls.progressPercent.setValue(p)">{{ p }}%</button>
            }
          </div>
        </div>
        <div class="field">
          <label for="pg-note">What changed?</label>
          <textarea id="pg-note" class="textarea" rows="4" formControlName="note" maxlength="2000" placeholder="A short update for the delegator (optional)"></textarea>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="progress-form" class="btn btn-primary" [disabled]="busy() || form.invalid">Save progress</button>
      </ng-container>
    </fu-modal>
  `,
  styles: `.range { width: 100%; accent-color: var(--accent); }`,
})
export class ProgressDialogComponent extends TaskDialogBase implements OnInit {
  protected readonly presets = [0, 25, 50, 75, 100];
  protected readonly form = inject(FormBuilder).nonNullable.group({
    progressPercent: [0, [Validators.min(0), Validators.max(100)]],
    note: ['', Validators.maxLength(2000)],
  });

  ngOnInit(): void {
    this.form.controls.progressPercent.setValue(this.detail().summary.progressPercent ?? 0);
  }

  protected submit(): void {
    if (this.form.invalid) return;
    const { progressPercent, note } = this.form.getRawValue();
    this.run(this.api.progress(this.taskId(), { progressPercent: Number(progressPercent), note: note.trim() || null }), `Progress set to ${progressPercent}%`);
  }
}
