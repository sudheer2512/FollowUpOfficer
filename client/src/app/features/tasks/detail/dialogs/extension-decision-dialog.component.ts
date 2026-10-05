import { ChangeDetectionStrategy, Component, inject, input, linkedSignal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ExtensionDto } from '../../../../core/models/task.models';
import { DateInputComponent } from '../../../../shared/forms/date-input.component';
import { PersonComponent } from '../../../../shared/domain/indicators';
import { DateOnlyPipe, DateTimePipe } from '../../../../shared/pipes/format.pipes';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

/** Approve / reject an extension request → POST /api/extensions/{id}/decision (note, optional override date). */
@Component({
  selector: 'fu-extension-decision-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, DateInputComponent, PersonComponent, DateOnlyPipe, DateTimePipe],
  template: `
    <fu-modal title="Decide extension request" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" (closed)="closed.emit()">
      <form id="extd-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        <div class="card pad stack req-card">
          <div class="row small">
            <fu-person [person]="extension().requestedBy" />
            <span class="muted">asked {{ extension().requestedAt | dateTime: 'short' }}</span>
          </div>
          <div class="row wrap small">
            <span class="muted">Move due date</span>
            <span class="strong">{{ extension().currentDueDate ? (extension().currentDueDate | dateOnly: 'weekday') : 'not set' }}</span>
            <span class="muted">→</span>
            <span class="strong text-accent">{{ extension().proposedDueDate | dateOnly: 'weekday' }}</span>
          </div>
          <div class="small pre">“{{ extension().reason }}”</div>
        </div>
        <div class="segmented" role="group" aria-label="Decision">
          <button type="button" [class.active]="approve()" [attr.aria-pressed]="approve()" (click)="approve.set(true)">Approve</button>
          <button type="button" [class.active]="!approve()" [attr.aria-pressed]="!approve()" (click)="approve.set(false)">Reject</button>
        </div>
        @if (approve()) {
          <div class="field">
            <label for="extd-date">Approve a different date</label>
            <fu-date-input inputId="extd-date" formControlName="overrideDate" />
            <div class="hint">Leave empty to approve the proposed date.</div>
          </div>
        }
        <div class="field">
          <label for="extd-note" [class.req]="!approve()">Note</label>
          <textarea
            id="extd-note"
            class="textarea"
            rows="3"
            formControlName="note"
            maxlength="2000"
            [placeholder]="approve() ? 'Optional message to the requester' : 'Why the extension is not possible'"
          ></textarea>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="extd-form" class="btn" [class.btn-primary]="approve()" [class.btn-danger]="!approve()" [disabled]="busy() || !valid()">
          {{ approve() ? 'Approve extension' : 'Reject extension' }}
        </button>
      </ng-container>
    </fu-modal>
  `,
  styles: `.req-card { background: var(--surface-2); box-shadow: none; }`,
})
export class ExtensionDecisionDialogComponent extends TaskDialogBase {
  readonly extension = input.required<ExtensionDto>();
  /** Initial decision (from the Approve / Reject button that opened the dialog). */
  readonly initialApprove = input(true);

  protected readonly approve = linkedSignal(() => this.initialApprove());
  protected readonly form = inject(FormBuilder).nonNullable.group({
    note: [''],
    overrideDate: [''],
  });

  protected valid(): boolean {
    return this.approve() || this.form.controls.note.value.trim().length > 0;
  }

  protected submit(): void {
    if (!this.valid()) return;
    const { note, overrideDate } = this.form.getRawValue();
    const approve = this.approve();
    this.run(
      this.api.decideExtension(this.extension().id, {
        approve,
        note: note.trim() || null,
        overrideDate: approve && overrideDate ? overrideDate : null,
      }),
      approve ? 'Extension approved' : 'Extension rejected',
    );
  }
}
