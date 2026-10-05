import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ESCALATION_TARGET_VALUES, EscalationTargetFlag } from '../../../../core/models/enums';
import { toFlags } from '../../../../core/util/flags';
import { HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

/** Escalate → POST /api/tasks/{id}/escalate with [Flags] targets ("TeamLeader, Ceo") and a required note. */
@Component({
  selector: 'fu-escalate-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, IconComponent, HumanizePipe],
  template: `
    <fu-modal title="Escalate task" [subtitle]="detail().summary.number + ' · ' + detail().summary.title" size="md" [dismissOnScrim]="false" (closed)="closed.emit()">
      <form id="escalate-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        @if (detail().summary.escalationLevel > 0) {
          <div class="alert tone-warning small">
            <fu-icon name="zap" /> Already escalated to level {{ detail().summary.escalationLevel }}. This raises it to level {{ detail().summary.escalationLevel + 1 }}.
          </div>
        }
        <fieldset class="fieldset">
          <legend>Notify</legend>
          <div class="checks">
            @for (t of targetOptions; track t) {
              <label class="check">
                <input type="checkbox" [checked]="targets().includes(t)" (change)="toggle(t, $any($event.target).checked)" />
                {{ t | humanize }}
              </label>
            }
          </div>
          @if (!targets().length) {
            <div class="error-text mt-8">Choose at least one recipient.</div>
          }
        </fieldset>
        <div class="field">
          <label for="es-note" class="req">Why are you escalating?</label>
          <textarea id="es-note" class="textarea" rows="4" formControlName="note" maxlength="2000" placeholder="What has happened and what you need from them"></textarea>
        </div>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="escalate-form" class="btn btn-danger solid" [disabled]="busy() || form.invalid || !targets().length">
          <fu-icon name="zap" [size]="15" /> Escalate
        </button>
      </ng-container>
    </fu-modal>
  `,
})
export class EscalateDialogComponent extends TaskDialogBase {
  protected readonly targetOptions = ESCALATION_TARGET_VALUES;
  protected readonly targets = signal<EscalationTargetFlag[]>(['TeamLeader', 'AccountableManager']);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    note: ['', [Validators.required, Validators.maxLength(2000), Validators.pattern(/\S/)]],
  });

  protected toggle(target: EscalationTargetFlag, on: boolean): void {
    this.targets.update((list) => (on ? [...new Set([...list, target])] : list.filter((t) => t !== target)));
  }

  protected submit(): void {
    if (this.form.invalid || !this.targets().length) return;
    // Keep the server's flag order so the comma list is canonical.
    const ordered = ESCALATION_TARGET_VALUES.filter((t) => this.targets().includes(t));
    this.run(this.api.escalate(this.taskId(), { targets: toFlags(ordered), note: this.form.getRawValue().note.trim() }), 'Task escalated');
  }
}
