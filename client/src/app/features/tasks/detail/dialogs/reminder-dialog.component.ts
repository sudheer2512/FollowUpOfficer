import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ReminderRequest } from '../../../../core/models/task.models';
import { AgoPipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { ReminderMode } from '../task-actions';
import { TaskDialogBase } from './task-dialog-base';

interface ModeCopy {
  title: string;
  submit: string;
  channels: string;
  success: string;
  placeholder: string;
}

const COPY: Record<ReminderMode, ModeCopy> = {
  reminder: {
    title: 'Send reminder',
    submit: 'Send reminder',
    channels: 'Sent by email and Teams.',
    success: 'Reminder queued',
    placeholder: 'Optional personal note added to the reminder',
  },
  update: {
    title: 'Request an update',
    submit: 'Request update',
    channels: 'Asks for a status update by email and Teams. Their reply is linked to this task automatically.',
    success: 'Update request queued',
    placeholder: 'What would you like to know? (optional)',
  },
  email: {
    title: 'Send email',
    submit: 'Send email',
    channels: 'Sent by email only. Replies are linked to this task automatically.',
    success: 'Email queued',
    placeholder: 'Message (optional — a standard follow-up is sent otherwise)',
  },
  teams: {
    title: 'Send Teams message',
    submit: 'Send message',
    channels: 'Sent as a Teams chat message only.',
    success: 'Teams message queued',
    placeholder: 'Message (optional — a standard follow-up is sent otherwise)',
  },
};

/** Send Reminder / Request Update / Send Email / Send Teams Message → POST /api/tasks/{id}/remind. */
@Component({
  selector: 'fu-reminder-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, IconComponent, AgoPipe],
  template: `
    <fu-modal [title]="copy().title" [subtitle]="recipient()" size="md" (closed)="closed.emit()">
      <form id="reminder-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
        <div class="alert tone-info small">
          <fu-icon [name]="mode() === 'email' ? 'mail' : mode() === 'teams' ? 'message' : 'bell'" />
          <span>
            {{ copy().channels }}
            @if (detail().followUp.remindersSinceResponse > 0) {
              <br />
              {{ detail().followUp.remindersSinceResponse }} sent since the last response
              @if (detail().followUp.lastReminderAt) {
                — the last {{ detail().followUp.lastReminderAt | ago }}
              }
            }
          </span>
        </div>
        <div class="field">
          <label for="rm-message">Message</label>
          <textarea id="rm-message" class="textarea" rows="4" formControlName="message" maxlength="2000" [placeholder]="copy().placeholder"></textarea>
        </div>
        <label class="check">
          <input type="checkbox" formControlName="includeTeamLeader" />
          Copy the team leader
        </label>
      </form>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="reminder-form" class="btn btn-primary" [disabled]="busy() || form.invalid">
          <fu-icon name="send" [size]="15" /> {{ copy().submit }}
        </button>
      </ng-container>
    </fu-modal>
  `,
})
export class ReminderDialogComponent extends TaskDialogBase {
  readonly mode = input<ReminderMode>('reminder');
  /** The reminder was queued (the API returns no task, so the page refreshes silently). */
  readonly sent = output<void>();

  protected readonly copy = computed(() => COPY[this.mode()]);
  protected readonly recipient = computed(() => {
    const s = this.detail().summary;
    const to = s.primaryAssignee?.name ?? s.teamName;
    return to ? `To ${to} · ${s.number}` : s.number;
  });

  protected readonly form = inject(FormBuilder).nonNullable.group({
    message: ['', Validators.maxLength(2000)],
    includeTeamLeader: [false],
  });

  protected submit(): void {
    if (this.busy() || this.form.invalid) return;
    const mode = this.mode();
    const { message, includeTeamLeader } = this.form.getRawValue();
    const req: ReminderRequest = {
      email: mode !== 'teams',
      teams: mode !== 'email',
      requestUpdate: mode === 'update',
      message: message.trim() || null,
      includeTeamLeader,
    };
    this.busy.set(true);
    this.api.remind(this.taskId(), req).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success(this.copy().success);
        this.sent.emit();
      },
      error: () => this.busy.set(false),
    });
  }
}
