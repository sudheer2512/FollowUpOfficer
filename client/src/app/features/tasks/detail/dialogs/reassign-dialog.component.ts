import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { LookupsStore } from '../../../../core/state/lookups.store';
import { PersonComponent } from '../../../../shared/domain/indicators';
import { PersonPickerComponent } from '../../../../shared/forms/person-picker.component';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { DelegationChainComponent } from '../delegation-chain.component';
import { TaskDialogBase } from './task-dialog-base';

/** Reassign → POST /api/tasks/{id}/reassign (new primary assignee + delegation note). */
@Component({
  selector: 'fu-reassign-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, PersonPickerComponent, PersonComponent, DelegationChainComponent],
  template: `
    <fu-modal title="Reassign task" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" [dismissOnScrim]="false" (closed)="closed.emit()">
      <form id="reassign-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        <div class="field">
          <span class="label">Delegation chain</span>
          <fu-delegation-chain [chain]="detail().delegationChain" [compact]="true" />
        </div>
        <div class="row small">
          <span class="muted">Currently responsible:</span>
          <fu-person [person]="detail().summary.primaryAssignee" [name]="detail().summary.teamName" />
        </div>
        <div class="field">
          <label for="ra-person" class="req">New responsible person</label>
          <fu-person-picker inputId="ra-person" formControlName="newPrimaryAssigneeId" [excludeIds]="excluded()" placeholder="Search people…" />
        </div>
        <div class="field">
          <label for="ra-note">Delegation note</label>
          <textarea id="ra-note" class="textarea" rows="3" formControlName="note" maxlength="2000" placeholder="Context for the new owner — shown on the delegation chain"></textarea>
        </div>
        @if (detail().summary.primaryAssignee) {
          <label class="check">
            <input type="checkbox" formControlName="keepPreviousAsSupporting" />
            Keep {{ detail().summary.primaryAssignee?.name }} as a supporting member
          </label>
        }
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="reassign-form" class="btn btn-primary" [disabled]="busy() || form.invalid">Reassign</button>
      </ng-container>
    </fu-modal>
  `,
})
export class ReassignDialogComponent extends TaskDialogBase {
  private readonly lookups = inject(LookupsStore);

  protected readonly excluded = computed(() => {
    const id = this.detail().summary.primaryAssignee?.id;
    return id ? [id] : [];
  });

  private readonly fb = inject(FormBuilder);
  protected readonly form = this.fb.group({
    newPrimaryAssigneeId: this.fb.control<string | null>(null, Validators.required),
    note: this.fb.nonNullable.control(''),
    keepPreviousAsSupporting: this.fb.nonNullable.control(true),
  });

  protected submit(): void {
    const { newPrimaryAssigneeId, note, keepPreviousAsSupporting } = this.form.getRawValue();
    if (this.form.invalid || !newPrimaryAssigneeId) return;
    const name = this.lookups.personName(newPrimaryAssigneeId) || 'the new owner';
    this.run(
      this.api.reassign(this.taskId(), {
        newPrimaryAssigneeId,
        note: note.trim() || null,
        keepPreviousAsSupporting,
      }),
      `Reassigned to ${name}`,
    );
  }
}
