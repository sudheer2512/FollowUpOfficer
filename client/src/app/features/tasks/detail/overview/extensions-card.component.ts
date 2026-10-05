import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { ExtensionStatus } from '../../../../core/models/enums';
import { TaskDetail } from '../../../../core/models/task.models';
import { PersonComponent } from '../../../../shared/domain/indicators';
import { AgoPipe, DateOnlyPipe, HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { DialogRequest } from '../task-actions';

const STATUS_TONE: Record<ExtensionStatus, string> = {
  Pending: 'warning',
  Approved: 'good',
  Rejected: 'danger',
  Withdrawn: 'neutral',
};

/** Extension requests (newest first) with their decision, and Approve / Reject where the user decides. */
@Component({
  selector: 'fu-extensions-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PersonComponent, DateOnlyPipe, HumanizePipe, AgoPipe],
  template: `
    <section class="card">
      <header class="card-header">
        <h2>Extension requests</h2>
        <span class="count-pill">{{ extensions().length }}</span>
      </header>
      <div class="card-body flush">
        <div class="table-wrap">
          <table class="table compact">
            <thead>
              <tr>
                <th>Requested by</th>
                <th>Due date</th>
                <th>Reason</th>
                <th>Status</th>
                <th><span class="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              @for (e of extensions(); track e.id) {
                <tr>
                  <td>
                    <fu-person [person]="e.requestedBy" />
                    <div class="xsmall muted">{{ e.requestedAt | ago }} · {{ e.source | humanize }}</div>
                  </td>
                  <td class="nowrap">
                    <span class="muted">{{ e.currentDueDate ? (e.currentDueDate | dateOnly: 'short') : '—' }}</span>
                    →
                    <span class="strong">{{ e.proposedDueDate | dateOnly: 'short' }}</span>
                  </td>
                  <td class="reason">
                    <div class="clamp-2" [attr.title]="e.reason">{{ e.reason }}</div>
                    @if (e.decisionNote) {
                      <div class="xsmall muted">{{ e.approver?.name ?? 'Approver' }}: “{{ e.decisionNote }}”</div>
                    }
                  </td>
                  <td><span class="badge tone-{{ tone(e.status) }}">{{ e.status }}</span></td>
                  <td class="actions">
                    @if (e.canDecide && e.status === 'Pending') {
                      <button type="button" class="btn btn-xs btn-primary" (click)="open.emit({ kind: 'decide-extension', extension: e, approve: true })">Approve</button>
                      <button type="button" class="btn btn-xs btn-danger" (click)="open.emit({ kind: 'decide-extension', extension: e, approve: false })">Reject</button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </div>
    </section>
  `,
  styles: `
    .reason { min-width: 180px; max-width: 320px; }
    .actions .btn + .btn { margin-left: 4px; }
  `,
})
export class ExtensionsCardComponent {
  readonly detail = input.required<TaskDetail>();
  readonly open = output<DialogRequest>();

  protected readonly extensions = computed(() => [...this.detail().extensions].sort((a, b) => b.requestedAt.localeCompare(a.requestedAt)));

  protected tone(s: ExtensionStatus): string {
    return STATUS_TONE[s] ?? 'neutral';
  }
}
