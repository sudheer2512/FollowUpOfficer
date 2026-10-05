import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TaskDetail } from '../../../core/models/task.models';
import { formatDateOnly, relativeTime } from '../../../core/util/dates';
import { AgoPipe, DateOnlyPipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../shared/ui/icon.component';
import { EvidenceListComponent } from './evidence-list.component';
import { DialogRequest } from './task-actions';
import { canAcknowledgeNow, decidableExtensions, latestSubmission, needsMyApproval, pendingApprovalForMe } from './task-detail.util';

/**
 * "Waiting for you" banners above the tabs: acknowledge a new task, approve a completion, decide extension requests,
 * and — for the delegator — an assignee's acknowledgement response (clarification, decline, different deadline).
 */
@Component({
  selector: 'fu-task-banners',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, EvidenceListComponent, AgoPipe, DateOnlyPipe, HumanizePipe],
  template: `
    @let d = detail();
    @if (ackPending()) {
      <section class="alert tone-accent banner" aria-label="Acknowledgement needed">
        <fu-icon name="user-check" [size]="18" />
        <div class="body">
          <div class="strong">Please acknowledge this task</div>
          <div class="small">{{ ackLine() }} Accept it, or tell them what you need.</div>
          <div class="actions">
            <button type="button" class="btn btn-sm btn-primary" [disabled]="busy()" (click)="accept.emit()"><fu-icon name="check" [size]="14" /> Accept</button>
            <button type="button" class="btn btn-sm" [disabled]="busy()" (click)="open.emit({ kind: 'acknowledge', response: 'NeedClarification' })">Need clarification</button>
            <button type="button" class="btn btn-sm" [disabled]="busy()" (click)="open.emit({ kind: 'acknowledge', response: 'ProposeDifferentDeadline' })">Propose different deadline</button>
            <button type="button" class="btn btn-sm btn-danger" [disabled]="busy()" (click)="open.emit({ kind: 'acknowledge', response: 'CannotAccept' })">Cannot accept</button>
          </div>
        </div>
      </section>
    }

    @if (approvalNeeded()) {
      <section class="alert tone-warning banner" aria-label="Approval needed">
        <fu-icon name="check-square" [size]="18" />
        <div class="body">
          <div class="strong">Completion awaiting your approval</div>
          @if (submission(); as sub) {
            <div class="small">
              {{ sub.submittedBy.name }} marked this complete {{ sub.submittedAt | ago }} (round {{ sub.round }})
              @if (myApproval(); as a) {
                · you approve as {{ a.role | humanize }}
              }
            </div>
            @if (sub.note) {
              <div class="small pre note">“{{ sub.note }}”</div>
            }
            @if (sub.evidence.length) {
              <fu-evidence-list [evidence]="sub.evidence" />
            }
          } @else {
            <div class="small">The assignee marked this task complete.</div>
          }
          <div class="actions">
            <button type="button" class="btn btn-sm btn-good" [disabled]="busy()" (click)="open.emit({ kind: 'approval', action: 'Confirm' })"><fu-icon name="check" [size]="14" /> Confirm</button>
            <button type="button" class="btn btn-sm" [disabled]="busy()" (click)="open.emit({ kind: 'approval', action: 'RequestMoreWork' })">Request more work</button>
            <button type="button" class="btn btn-sm btn-danger" [disabled]="busy()" (click)="open.emit({ kind: 'approval', action: 'Reopen' })">Reopen</button>
          </div>
        </div>
      </section>
    }

    @for (ext of extensions(); track ext.id) {
      <section class="alert tone-info banner" aria-label="Extension request">
        <fu-icon name="calendar-clock" [size]="18" />
        <div class="body">
          <div class="strong">{{ ext.requestedBy.name }} asks to move the due date to {{ ext.proposedDueDate | dateOnly: 'weekday' }}</div>
          <div class="small">
            @if (ext.currentDueDate) {
              Currently {{ ext.currentDueDate | dateOnly: 'weekday' }} ·
            }
            requested {{ ext.requestedAt | ago }}
          </div>
          <div class="small pre note">“{{ ext.reason }}”</div>
          <div class="actions">
            <button type="button" class="btn btn-sm btn-primary" [disabled]="busy()" (click)="open.emit({ kind: 'decide-extension', extension: ext, approve: true })">Approve</button>
            <button type="button" class="btn btn-sm btn-danger" [disabled]="busy()" (click)="open.emit({ kind: 'decide-extension', extension: ext, approve: false })">Reject</button>
          </div>
        </div>
      </section>
    }

    @if (ackResponse(); as r) {
      <section class="alert tone-warning banner" aria-label="Acknowledgement response">
        <fu-icon [name]="r.icon" [size]="18" />
        <div class="body">
          <div class="strong">{{ d.summary.primaryAssignee?.name ?? 'The assignee' }} {{ r.text }}</div>
          @if (d.acknowledgementComment) {
            <div class="small pre note">“{{ d.acknowledgementComment }}”</div>
          }
          <div class="actions">
            <button type="button" class="btn btn-sm" [disabled]="busy()" (click)="open.emit({ kind: 'comment' })"><fu-icon name="messages" [size]="14" /> Reply</button>
            @if (d.acknowledgement === 'DeadlineProposed') {
              <button type="button" class="btn btn-sm" [disabled]="busy()" (click)="open.emit({ kind: 'due-date' })"><fu-icon name="calendar" [size]="14" /> Change due date</button>
            }
            @if (d.acknowledgement === 'Declined') {
              <button type="button" class="btn btn-sm" [disabled]="busy()" (click)="open.emit({ kind: 'reassign' })"><fu-icon name="user-plus" [size]="14" /> Reassign</button>
            }
          </div>
        </div>
      </section>
    }
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 10px; margin-bottom: 16px; }
    :host:empty { display: none; }
    .banner { align-items: flex-start; padding: 12px 14px; }
    .banner > fu-icon { margin-top: 1px; }
    .body { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 6px; }
    .note { color: var(--text-2); font-style: italic; }
    .actions { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 2px; }
  `,
})
export class TaskBannersComponent {
  readonly detail = input.required<TaskDetail>();
  readonly busy = input(false);
  readonly open = output<DialogRequest>();
  /** Accept the task (no dialog needed). */
  readonly accept = output<void>();

  protected readonly ackPending = computed(() => canAcknowledgeNow(this.detail()));
  /** "Priya Shah assigned it to you 2h ago · due Fri 9 Oct." */
  protected readonly ackLine = computed(() => {
    const d = this.detail();
    const who = d.summary.delegator?.name ?? 'Your manager';
    const when = d.assignedAt ? ` ${relativeTime(d.assignedAt)}` : '';
    const due = d.summary.dueDate ? ` · due ${formatDateOnly(d.summary.dueDate, { withWeekday: true, short: true })}` : '';
    return `${who} assigned it to you${when}${due}.`;
  });
  protected readonly approvalNeeded = computed(() => needsMyApproval(this.detail()));
  protected readonly myApproval = computed(() => pendingApprovalForMe(this.detail()));
  protected readonly submission = computed(() => latestSubmission(this.detail()));
  protected readonly extensions = computed(() => decidableExtensions(this.detail()));

  /** The assignee's non-accepting acknowledgement, shown to whoever manages the task. */
  protected readonly ackResponse = computed(() => {
    const d = this.detail();
    if (!d.permissions.canManage) return null;
    switch (d.acknowledgement) {
      case 'ClarificationRequested':
        return { icon: 'help', text: 'asked for clarification before accepting' };
      case 'Declined':
        return { icon: 'ban', text: 'cannot accept this task' };
      case 'DeadlineProposed':
        return { icon: 'calendar-clock', text: 'proposed a different deadline' };
      default:
        return null;
    }
  });
}
