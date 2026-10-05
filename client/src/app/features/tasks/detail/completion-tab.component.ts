import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ApprovalDecision } from '../../../core/models/enums';
import { TaskDetail } from '../../../core/models/task.models';
import { PersonComponent } from '../../../shared/domain/indicators';
import { AgoPipe, DateOnlyPipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { EvidenceListComponent } from './evidence-list.component';

const DECISION_TONE: Record<ApprovalDecision, string> = {
  Pending: 'warning',
  Approved: 'good',
  Reopened: 'danger',
  MoreWorkRequested: 'orange',
  Superseded: 'neutral',
};

/** Completion submissions (each round with its evidence) and the approval history. */
@Component({
  selector: 'fu-completion-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PersonComponent, EvidenceListComponent, EmptyStateComponent, DateOnlyPipe, DateTimePipe, AgoPipe, HumanizePipe],
  template: `
    <div class="stack-lg">
      <section class="card">
        <header class="card-header">
          <h2>Completion submissions</h2>
          <span class="count-pill">{{ submissions().length }}</span>
          <span class="spacer"></span>
          <span class="small muted">{{ detail().requiresApproval ? 'Completion requires approval' : 'No approval required' }}</span>
        </header>
        <div class="card-body flush list">
          @for (s of submissions(); track s.id) {
            <article class="list-row sub">
              <span class="round" [attr.aria-label]="'Round ' + s.round">R{{ s.round }}</span>
              <div class="main stack">
                <div class="row wrap">
                  <fu-person [person]="s.submittedBy" />
                  <span class="small muted" [attr.title]="s.submittedAt | dateTime">submitted {{ s.submittedAt | ago }}</span>
                  <span class="chip sm">Completed {{ s.completionDate | dateOnly }}</span>
                  @if (s.source !== 'User') {
                    <span class="chip sm tone-accent">via {{ s.source | humanize }}</span>
                  }
                </div>
                @if (s.note) {
                  <div class="pre small note">{{ s.note }}</div>
                }
                <fu-evidence-list [evidence]="s.evidence" />
              </div>
            </article>
          } @empty {
            <fu-empty icon="check-circle" title="Not submitted yet" message="When the assignee marks the task complete, their note and evidence appear here." [compact]="true" />
          }
        </div>
      </section>

      <section class="card">
        <header class="card-header">
          <h2>Approvals</h2>
          <span class="count-pill">{{ approvals().length }}</span>
        </header>
        <div class="card-body flush">
          @if (approvals().length) {
            <div class="table-wrap">
              <table class="table compact">
                <thead>
                  <tr>
                    <th>Round</th>
                    <th>Stage</th>
                    <th>Approver</th>
                    <th>Decision</th>
                    <th>Comment</th>
                    <th>Decided</th>
                  </tr>
                </thead>
                <tbody>
                  @for (a of approvals(); track a.id) {
                    <tr>
                      <td class="num">{{ a.round }}</td>
                      <td class="nowrap">{{ a.stage }} · {{ a.role | humanize }}</td>
                      <td><fu-person [person]="a.approver" /></td>
                      <td>
                        <span class="badge tone-{{ tone(a.decision) }}">{{ a.decision | humanize }}</span>
                        @if (a.canDecide && a.decision === 'Pending') {
                          <span class="chip sm tone-accent">You</span>
                        }
                      </td>
                      <td class="comment"><span class="clamp-2" [attr.title]="a.comment ?? ''">{{ a.comment || '—' }}</span></td>
                      <td class="nowrap small muted">{{ a.decidedAt ? (a.decidedAt | dateTime: 'short') : 'Requested ' + (a.requestedAt | ago) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <fu-empty icon="check-square" title="No approvals" [message]="detail().requiresApproval ? 'Approval steps start when the task is submitted.' : 'This task does not need approval.'" [compact]="true" />
          }
        </div>
      </section>
    </div>
  `,
  styles: `
    .sub { align-items: flex-start; }
    .round { flex: none; width: 32px; height: 24px; border-radius: 6px; background: var(--accent-soft); color: var(--accent-text); font-size: 12px; font-weight: 700; display: inline-flex; align-items: center; justify-content: center; }
    .note { color: var(--text-2); }
    .comment { max-width: 340px; }
  `,
})
export class CompletionTabComponent {
  readonly detail = input.required<TaskDetail>();

  protected readonly submissions = computed(() =>
    [...this.detail().submissions].sort((a, b) => b.round - a.round || b.submittedAt.localeCompare(a.submittedAt)),
  );
  protected readonly approvals = computed(() => [...this.detail().approvals].sort((a, b) => b.round - a.round || a.stage - b.stage));

  protected tone(d: ApprovalDecision): string {
    return DECISION_TONE[d] ?? 'neutral';
  }
}
