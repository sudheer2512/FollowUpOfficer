import { Injectable, inject } from '@angular/core';
import { Observable, lastValueFrom } from 'rxjs';
import { ExecutiveApi } from '../../core/api/executive.api';
import { TasksApi } from '../../core/api/tasks.api';
import { Guid } from '../../core/models/common.models';
import { AttentionDecisionAction } from '../../core/models/view.models';
import { addDays, combineDateTime, formatDateTime, fromLocalDateTimeInput, toLocalDateTimeInput, todayDateOnly } from '../../core/util/dates';
import { toFlags } from '../../core/util/flags';
import { DialogService } from '../../shared/ui/dialog.service';
import { ToastService } from '../../shared/ui/toast.service';

/** Default "snooze until" for the prompt: tomorrow 09:00 local time. */
export function defaultSnoozeInput(): string {
  return toLocalDateTimeInput(combineDateTime(addDays(todayDateOnly(), 1), '09:00'));
}

/**
 * Executive quick actions shared by the attention list and the approval queue (workbench, attention page,
 * dashboards). Each method asks for any input it needs (note, reason, snooze time), calls the API, toasts the
 * success and resolves `true` when server state changed — `false` when cancelled or failed. HTTP errors are
 * already toasted by the interceptor, so failures are not reported again here.
 */
@Injectable({ providedIn: 'root' })
export class QuickActions {
  private readonly tasks = inject(TasksApi);
  private readonly executive = inject(ExecutiveApi);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);

  /** Email + Teams reminder that also asks for a status update. */
  remind(taskId: Guid): Promise<boolean> {
    return this.run(this.tasks.remind(taskId, { email: true, teams: true, requestUpdate: true }), 'Reminder queued');
  }

  escalate(taskId: Guid, targets: readonly string[], note: string): Promise<boolean> {
    return this.run(this.tasks.escalate(taskId, { targets: toFlags(targets), note }), 'Escalation sent');
  }

  approveCompletion(taskId: Guid): Promise<boolean> {
    return this.run(this.tasks.approval(taskId, { action: 'Confirm' }), 'Completion approved');
  }

  async reopenCompletion(taskId: Guid): Promise<boolean> {
    const comment = await this.dialog.prompt({
      title: 'Reopen task',
      message: 'The work goes back to the assignee with your comment.',
      label: 'What still needs to be done?',
      multiline: true,
      required: true,
      confirmText: 'Reopen task',
    });
    if (comment === null) return false;
    return this.run(this.tasks.approval(taskId, { action: 'Reopen', comment: comment.trim() }), 'Task reopened');
  }

  async decideExtension(extensionRequestId: Guid, approve: boolean): Promise<boolean> {
    const note = await this.dialog.prompt({
      title: approve ? 'Approve extension' : 'Reject extension',
      message: approve
        ? 'The due date moves to the proposed date and the assignee is notified.'
        : 'The current due date stays. The assignee sees your reason.',
      label: approve ? 'Note (optional)' : 'Reason',
      multiline: true,
      required: !approve,
      confirmText: approve ? 'Approve' : 'Reject',
      danger: !approve,
    });
    if (note === null) return false;
    return this.run(
      this.tasks.decideExtension(extensionRequestId, { approve, note: note.trim() || null }),
      approve ? 'Extension approved' : 'Extension rejected',
    );
  }

  acceptSuggestion(suggestionId: Guid): Promise<boolean> {
    return this.run(this.tasks.acceptSuggestion(suggestionId), 'Suggestion applied');
  }

  dismissSuggestion(suggestionId: Guid): Promise<boolean> {
    return this.run(this.tasks.dismissSuggestion(suggestionId), 'Suggestion dismissed');
  }

  /** Resolve / dismiss / reopen an attention item. */
  decideAttention(id: Guid, action: Exclude<AttentionDecisionAction, 'snooze'>): Promise<boolean> {
    const success = action === 'resolve' ? 'Marked as resolved' : action === 'dismiss' ? 'Dismissed' : 'Back on your list';
    return this.run(this.executive.decideAttention(id, { action }), success);
  }

  async snoozeAttention(id: Guid): Promise<boolean> {
    const value = await this.dialog.prompt({
      title: 'Snooze',
      message: 'The item leaves your list and comes back at this time.',
      label: 'Snooze until',
      inputType: 'datetime-local',
      value: defaultSnoozeInput(),
      required: true,
      confirmText: 'Snooze',
    });
    if (value === null) return false;
    const until = fromLocalDateTimeInput(value);
    if (!until || new Date(until).getTime() <= Date.now()) {
      this.toast.warning('Pick a time in the future to snooze until.');
      return false;
    }
    return this.run(
      this.executive.decideAttention(id, { action: 'snooze', snoozeUntil: until }),
      `Snoozed until ${formatDateTime(until, { short: true })}`,
    );
  }

  private async run(source: Observable<unknown>, success: string): Promise<boolean> {
    try {
      await lastValueFrom(source, { defaultValue: null });
    } catch {
      return false; // already toasted by the HTTP interceptor
    }
    this.toast.success(success);
    return true;
  }
}
