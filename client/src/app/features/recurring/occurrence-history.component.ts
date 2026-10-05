import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { RecurringInstanceDto } from '../../core/models/view.models';
import { StatusBadgeComponent } from '../../shared/domain/indicators';
import { OutcomeBadgeComponent } from '../../shared/domain/recurring-instance-table.component';
import { DateOnlyPipe, DateTimePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { latenessText } from './recurring-display';

interface HistoryRow {
  i: RecurringInstanceDto;
  /** The holiday rule moved the occurrence off its nominal date. */
  moved: boolean;
  late: string;
}

/**
 * Occurrence history of one recurring definition: key, nominal / actual date, scheduled create time, due date,
 * outcome (Created / Created late / Skipped – holiday / Skipped – catch-up / Skipped by user / Paused), reason,
 * task link + status. A richer variant of the shared <fu-instance-table> with the nominal-vs-actual date.
 */
@Component({
  selector: 'fu-occurrence-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, OutcomeBadgeComponent, StatusBadgeComponent, EmptyStateComponent, SkeletonComponent, DateOnlyPipe, DateTimePipe],
  template: `
    @if (loading() && !rows().length) {
      <fu-skeleton variant="table" [rows]="5" />
    } @else if (!rows().length) {
      <fu-empty icon="repeat" [compact]="true" [title]="emptyTitle()" [message]="emptyMessage()" />
    } @else {
      <div class="table-wrap">
        <table class="table compact">
          <thead>
            <tr>
              <th>Occurrence</th>
              <th>Date</th>
              <th>Scheduled</th>
              <th>Due</th>
              <th>Outcome</th>
              <th>Reason</th>
              <th>Task</th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.i.id) {
              <tr>
                <td class="nowrap"><span class="mono">{{ r.i.occurrenceKey }}</span></td>
                <td class="nowrap small">
                  @if (r.i.occurrenceDate) {
                    {{ r.i.occurrenceDate | dateOnly: 'weekday' }}
                    @if (r.moved) {
                      <div class="xsmall moved">moved from {{ r.i.nominalDate | dateOnly: 'weekday' }}</div>
                    }
                  } @else {
                    <span class="muted">{{ r.i.nominalDate | dateOnly: 'weekday' }}</span>
                    <div class="xsmall muted">not created</div>
                  }
                </td>
                <td class="nowrap small">{{ r.i.scheduledCreateAt | dateTime: 'short' }}</td>
                <td class="nowrap small">
                  @if (r.i.dueDate) {
                    {{ r.i.dueDate | dateOnly: 'short' }}
                  } @else {
                    <span class="muted">—</span>
                  }
                </td>
                <td class="nowrap">
                  <fu-outcome [value]="r.i.outcome" />
                  @if (r.late) {
                    <div class="xsmall muted">{{ r.late }} late</div>
                  }
                </td>
                <td class="small muted reason">{{ r.i.reason || '—' }}</td>
                <td class="nowrap">
                  @if (r.i.taskId) {
                    <a class="task-number" [routerLink]="['/tasks', r.i.taskId]">{{ r.i.taskNumber || 'Open task' }}</a>
                    @if (r.i.taskStatus) {
                      <fu-status class="st" [status]="r.i.taskStatus" />
                    }
                  } @else {
                    <span class="muted">—</span>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .reason { max-width: 320px; }
    .moved { color: var(--warning); }
    .st { margin-left: 6px; }
  `,
})
export class OccurrenceHistoryComponent {
  readonly instances = input<RecurringInstanceDto[]>([]);
  readonly loading = input(false);
  readonly emptyTitle = input('No occurrences yet');
  readonly emptyMessage = input('');

  protected readonly rows = computed<HistoryRow[]>(() =>
    (this.instances() ?? []).map((i) => ({
      i,
      moved: !!i.occurrenceDate && i.occurrenceDate !== i.nominalDate,
      late: (i.latenessMinutes ?? 0) > 0 ? latenessText(i.latenessMinutes) : '',
    })),
  );
}
