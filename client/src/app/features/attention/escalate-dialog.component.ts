import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { ESCALATION_TARGET_VALUES, EscalationTargetFlag } from '../../core/models/enums';
import { TaskListItem } from '../../core/models/task.models';
import { HumanizePipe } from '../../shared/pipes/format.pipes';
import { ModalComponent } from '../../shared/ui/modal.component';
import { QuickActions } from './quick-actions.service';

let escalateSeq = 0;

/**
 * Manual escalation of one task: who to notify ([Flags] EscalationTarget) and a required note.
 *   @if (escalating(); as t) { <fu-escalate-dialog [task]="t" (closed)="escalating.set(null)" (escalated)="…" /> }
 */
@Component({
  selector: 'fu-escalate-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ModalComponent, HumanizePipe],
  template: `
    <fu-modal title="Escalate" [subtitle]="task().number + ' · ' + task().title" (closed)="closed.emit()">
      <div class="stack-lg">
        <fieldset class="fieldset">
          <legend>Notify</legend>
          <div class="checks">
            @for (t of targetOptions; track t) {
              <label class="check">
                <input type="checkbox" [checked]="targets().includes(t)" (change)="toggle(t, $event)" />
                {{ t | humanize }}
              </label>
            }
          </div>
          @if (!targets().length) {
            <div class="error-text mt-8">Choose at least one person to notify.</div>
          }
        </fieldset>
        <div class="field">
          <label class="req" [attr.for]="noteId">Note</label>
          <textarea
            class="textarea"
            rows="4"
            [id]="noteId"
            [value]="note()"
            (input)="onNote($event)"
            placeholder="Why this needs escalating and what you expect next"
          ></textarea>
          <span class="hint">Sent with the escalation and recorded on the task timeline.</span>
        </div>
      </div>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="button" class="btn btn-primary" [disabled]="!canSubmit()" (click)="submit()">
          {{ busy() ? 'Escalating…' : 'Escalate' }}
        </button>
      </ng-container>
    </fu-modal>
  `,
})
export class EscalateDialogComponent {
  private readonly actions = inject(QuickActions);

  readonly task = input.required<TaskListItem>();
  readonly closed = output<void>();
  /** Emitted after the escalation was accepted by the server. */
  readonly escalated = output<void>();

  protected readonly targetOptions = ESCALATION_TARGET_VALUES;
  protected readonly noteId = `fu-escalate-note-${++escalateSeq}`;
  protected readonly targets = signal<EscalationTargetFlag[]>(['TeamLeader', 'DepartmentHead']);
  protected readonly note = signal('');
  protected readonly busy = signal(false);
  protected readonly canSubmit = computed(() => !this.busy() && this.targets().length > 0 && this.note().trim().length > 0);

  protected toggle(target: EscalationTargetFlag, e: Event): void {
    const on = (e.target as HTMLInputElement).checked;
    // Keep the server's flag order so the comma list is stable.
    this.targets.update((list) => ESCALATION_TARGET_VALUES.filter((t) => (t === target ? on : list.includes(t))));
  }

  protected onNote(e: Event): void {
    this.note.set((e.target as HTMLTextAreaElement).value);
  }

  protected async submit(): Promise<void> {
    if (!this.canSubmit()) return;
    this.busy.set(true);
    const ok = await this.actions.escalate(this.task().id, this.targets(), this.note().trim());
    this.busy.set(false);
    if (ok) {
      this.escalated.emit();
      this.closed.emit();
    }
  }
}
