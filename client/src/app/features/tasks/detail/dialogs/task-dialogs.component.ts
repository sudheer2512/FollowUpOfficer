import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TaskDetail } from '../../../../core/models/task.models';
import { DialogRequest } from '../task-actions';
import { AcknowledgeDialogComponent } from './acknowledge-dialog.component';
import { ApprovalDialogComponent } from './approval-dialog.component';
import { CommentDialogComponent } from './comment-dialog.component';
import { CommitmentDialogComponent } from './commitment-dialog.component';
import { CompleteDialogComponent } from './complete-dialog.component';
import { DependencyDialogComponent } from './dependency-dialog.component';
import { DueDateDialogComponent } from './due-date-dialog.component';
import { EscalateDialogComponent } from './escalate-dialog.component';
import { ExtensionDecisionDialogComponent } from './extension-decision-dialog.component';
import { ExtensionRequestDialogComponent } from './extension-request-dialog.component';
import { LinkMessageDialogComponent } from './link-message-dialog.component';
import { ProgressDialogComponent } from './progress-dialog.component';
import { ReassignDialogComponent } from './reassign-dialog.component';
import { ReminderDialogComponent } from './reminder-dialog.component';
import { SnoozeDialogComponent } from './snooze-dialog.component';
import { StatusDialogComponent } from './status-dialog.component';

/**
 * Renders the one dialog the page asked for. Every dialog gets the task and reports back either the updated task
 * (`updated`), or — for reminders, whose API returns no task — a request to refresh (`reload`). Both close the dialog.
 */
@Component({
  selector: 'fu-task-dialogs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReminderDialogComponent,
    CommentDialogComponent,
    ProgressDialogComponent,
    StatusDialogComponent,
    DueDateDialogComponent,
    ReassignDialogComponent,
    EscalateDialogComponent,
    SnoozeDialogComponent,
    CompleteDialogComponent,
    AcknowledgeDialogComponent,
    ApprovalDialogComponent,
    ExtensionRequestDialogComponent,
    ExtensionDecisionDialogComponent,
    CommitmentDialogComponent,
    DependencyDialogComponent,
    LinkMessageDialogComponent,
  ],
  template: `
    @switch (request().kind) {
      @case ('reminder') {
        <fu-reminder-dialog [detail]="detail()" [mode]="reminderMode()" (sent)="refresh()" (closed)="closed.emit()" />
      }
      @case ('comment') {
        <fu-comment-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('progress') {
        <fu-progress-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('status') {
        <fu-status-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('due-date') {
        <fu-due-date-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('reassign') {
        <fu-reassign-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('escalate') {
        <fu-escalate-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('snooze') {
        <fu-snooze-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('complete') {
        <fu-complete-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('acknowledge') {
        <fu-acknowledge-dialog [detail]="detail()" [response]="ackResponse()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('approval') {
        <fu-approval-dialog [detail]="detail()" [action]="approvalAction()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('request-extension') {
        <fu-extension-request-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('decide-extension') {
        @if (extension(); as ext) {
          <fu-extension-decision-dialog
            [detail]="detail()"
            [extension]="ext.extension"
            [initialApprove]="ext.approve"
            (done)="finish($event)"
            (closed)="closed.emit()"
          />
        }
      }
      @case ('commitment') {
        <fu-commitment-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('dependency') {
        <fu-dependency-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
      @case ('link-message') {
        <fu-link-message-dialog [detail]="detail()" (done)="finish($event)" (closed)="closed.emit()" />
      }
    }
  `,
})
export class TaskDialogsComponent {
  readonly detail = input.required<TaskDetail>();
  readonly request = input.required<DialogRequest>();
  readonly updated = output<TaskDetail>();
  readonly reload = output<void>();
  readonly closed = output<void>();

  // Variant payloads, narrowed here so the template stays free of casts.
  protected readonly reminderMode = computed(() => {
    const r = this.request();
    return r.kind === 'reminder' ? r.mode : 'reminder';
  });
  protected readonly ackResponse = computed(() => {
    const r = this.request();
    return r.kind === 'acknowledge' ? r.response : 'NeedClarification';
  });
  protected readonly approvalAction = computed(() => {
    const r = this.request();
    return r.kind === 'approval' ? r.action : 'Confirm';
  });
  protected readonly extension = computed(() => {
    const r = this.request();
    return r.kind === 'decide-extension' ? r : null;
  });

  protected finish(updated: TaskDetail): void {
    this.updated.emit(updated);
    this.closed.emit();
  }

  protected refresh(): void {
    this.reload.emit();
    this.closed.emit();
  }
}
