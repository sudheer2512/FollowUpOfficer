import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TaskDetail } from '../../../../core/models/task.models';
import { toTimeInput } from '../../../../core/util/dates';
import { DeadlineTextComponent } from '../../../../shared/domain/indicators';
import { AgoPipe, DateOnlyPipe, DateTimePipe } from '../../../../shared/pipes/format.pipes';

/** Assigned, start, due (+ time), hard deadline, review date, original due date and how often it moved. */
@Component({
  selector: 'fu-dates-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DeadlineTextComponent, DateOnlyPipe, DateTimePipe, AgoPipe],
  template: `
    @let d = detail();
    <section class="card">
      <header class="card-header"><h2>Dates</h2></header>
      <div class="card-body">
        <dl class="kv">
          <dt>Due</dt>
          <dd>
            @if (d.summary.dueDate) {
              <span class="strong">{{ d.summary.dueDate | dateOnly: 'weekday' }}</span>
              @if (dueTime()) {
                <span class="muted"> at {{ dueTime() }}</span>
              }
              <div><fu-deadline [text]="d.summary.deadlineText" [overdue]="d.summary.isOverdue" [bucket]="d.summary.bucket" /></div>
            } @else {
              <span class="muted">No due date</span>
            }
          </dd>
          @if (d.hardDeadline) {
            <dt>Hard deadline</dt>
            <dd class="text-danger">{{ d.hardDeadline | dateOnly: 'weekday' }}</dd>
          }
          @if (d.originalDueDate && d.dueDateChangeCount > 0) {
            <dt>Originally due</dt>
            <dd>
              {{ d.originalDueDate | dateOnly }}
              <span class="chip sm" [class.tone-warning]="d.dueDateChangeCount >= 2">changed {{ d.dueDateChangeCount }} time{{ d.dueDateChangeCount === 1 ? '' : 's' }}</span>
            </dd>
          }
          @if (d.summary.openCommitmentDate) {
            <dt>Committed for</dt>
            <dd>{{ d.summary.openCommitmentDate | dateOnly: 'weekday' }}</dd>
          }
          @if (d.startDate) {
            <dt>Start</dt>
            <dd>{{ d.startDate | dateOnly }}</dd>
          }
          @if (d.reviewDate) {
            <dt>Review</dt>
            <dd>{{ d.reviewDate | dateOnly: 'weekday' }}</dd>
          }
          @if (d.assignedAt) {
            <dt>Assigned</dt>
            <dd [attr.title]="d.assignedAt | dateTime">{{ d.assignedAt | dateTime: 'short' }} <span class="muted small">({{ d.assignedAt | ago }})</span></dd>
          }
          @if (d.acknowledgedAt) {
            <dt>Acknowledged</dt>
            <dd>{{ d.acknowledgedAt | dateTime: 'short' }}</dd>
          }
          @if (d.summary.completedAt) {
            <dt>Completed</dt>
            <dd class="text-good">{{ d.summary.completedAt | dateTime: 'short' }}</dd>
          }
        </dl>
      </div>
    </section>
  `,
})
export class DatesCardComponent {
  readonly detail = input.required<TaskDetail>();
  protected readonly dueTime = computed(() => toTimeInput(this.detail().dueTime));
}
