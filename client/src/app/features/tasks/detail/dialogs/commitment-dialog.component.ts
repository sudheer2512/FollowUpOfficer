import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { formatDateOnly } from '../../../../core/util/dates';
import { DateInputComponent } from '../../../../shared/forms/date-input.component';
import { PersonPickerComponent } from '../../../../shared/forms/person-picker.component';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

/** Add commitment → POST /api/tasks/{id}/commitments ("Rahul will finish the draft by Friday"). */
@Component({
  selector: 'fu-commitment-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, PersonPickerComponent, DateInputComponent],
  template: `
    <fu-modal title="Record a commitment" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" (closed)="closed.emit()">
      <form id="commit-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        <p class="small muted">Commitments are tracked: if the date passes without completion, the commitment is marked missed and surfaced.</p>
        <div class="form-grid">
          <div class="field">
            <label for="cm-person">Who committed</label>
            <fu-person-picker inputId="cm-person" formControlName="employeeId" />
          </div>
          <div class="field">
            <label for="cm-date" class="req">By when</label>
            <fu-date-input inputId="cm-date" formControlName="commitmentDate" [quickPicks]="true" />
          </div>
          <div class="field full">
            <label for="cm-text" class="req">What was promised</label>
            <textarea id="cm-text" class="textarea" rows="3" formControlName="text" maxlength="1000" placeholder="e.g. Will share the revised vendor quotation by Friday"></textarea>
          </div>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="commit-form" class="btn btn-primary" [disabled]="busy() || form.invalid">Record commitment</button>
      </ng-container>
    </fu-modal>
  `,
})
export class CommitmentDialogComponent extends TaskDialogBase implements OnInit {
  private readonly fb = inject(FormBuilder);
  protected readonly form = this.fb.group({
    employeeId: this.fb.control<string | null>(null),
    commitmentDate: this.fb.nonNullable.control('', Validators.required),
    text: this.fb.nonNullable.control('', [Validators.required, Validators.maxLength(1000), Validators.pattern(/\S/)]),
  });

  ngOnInit(): void {
    this.form.controls.employeeId.setValue(this.detail().summary.primaryAssignee?.id ?? null);
  }

  protected submit(): void {
    if (this.form.invalid) return;
    const { employeeId, commitmentDate, text } = this.form.getRawValue();
    this.run(
      this.api.addCommitment(this.taskId(), { employeeId, commitmentDate, text: text.trim() }),
      `Commitment for ${formatDateOnly(commitmentDate, { withWeekday: true, short: true })} recorded`,
    );
  }
}
