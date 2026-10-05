import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { OccurrenceOutcome } from '../../core/models/enums';
import { RecurringInstanceDto } from '../../core/models/view.models';
import { DateOnlyPipe, DateTimePipe, HumanizePipe } from '../pipes/format.pipes';
import { EmptyStateComponent } from '../ui/empty-state.component';
import { SkeletonComponent } from '../ui/skeleton.component';
import { StatusBadgeComponent } from './indicators';

/** Created green · Created late amber · Skipped – holiday / catch-up / by user grey · Paused grey. */
export function outcomeTone(o: OccurrenceOutcome | string): string {
  switch (o) {
    case 'Created':
      return 'good';
    case 'CreatedLate':
      return 'warning';
    case 'SkippedCatchUp':
    case 'Skipped':
      return 'orange';
    default:
      return 'neutral';
  }
}

/** Occurrence outcome badge (Created / Created late / Skipped – holiday / Skipped – catch-up / Skipped by user / Paused). */
@Component({
  selector: 'fu-outcome',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HumanizePipe],
  template: `<span class="badge tone-{{ tone() }}">{{ value() | humanize }}</span>`,
  host: { style: 'display:inline-flex' },
})
export class OutcomeBadgeComponent {
  readonly value = input<OccurrenceOutcome | string>('Created');
  protected readonly tone = computed(() => outcomeTone(this.value()));
}

/**
 * Recurring occurrence history: key, scheduled create time, due date, outcome, reason, task link + status.
 *   <fu-instance-table [instances]="list" [showDefinition]="true" />
 */
@Component({
  selector: 'fu-instance-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, OutcomeBadgeComponent, StatusBadgeComponent, EmptyStateComponent, SkeletonComponent, DateOnlyPipe, DateTimePipe],
  template: `
    @if (loading() && !instances().length) {
      <fu-skeleton variant="table" [rows]="5" />
    } @else if (!instances().length) {
      <fu-empty icon="repeat" [title]="emptyTitle()" [message]="emptyMessage()" [compact]="true" />
    } @else {
      <div class="table-wrap">
        <table class="table compact">
          <thead>
            <tr>
              @if (showDefinition()) {
                <th>Recurring task</th>
              }
              <th>Occurrence</th>
              <th>Scheduled</th>
              <th>Due</th>
              <th>Outcome</th>
              <th>Reason</th>
              <th>Task</th>
            </tr>
          </thead>
          <tbody>
            @for (i of visible(); track i.id) {
              <tr>
                @if (showDefinition()) {
                  <td><a [routerLink]="['/recurring', i.definitionId]" class="truncate" style="max-width:240px;display:inline-block">{{ i.definitionTitle }}</a></td>
                }
                <td class="mono nowrap">{{ i.occurrenceKey }}</td>
                <td class="nowrap small">{{ i.scheduledCreateAt | dateTime: 'short' }}</td>
                <td class="nowrap small">{{ i.dueDate | dateOnly: 'short' }}</td>
                <td>
                  <fu-outcome [value]="i.outcome" />
                  @if (i.latenessMinutes > 0) {
                    <div class="xsmall muted">{{ lateText(i.latenessMinutes) }} late</div>
                  }
                </td>
                <td class="small muted reason">{{ i.reason }}</td>
                <td class="nowrap">
                  @if (i.taskId) {
                    <a [routerLink]="['/tasks', i.taskId]" class="task-number">{{ i.taskNumber }}</a>
                    @if (i.taskStatus) {
                      <fu-status [status]="i.taskStatus" />
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
      @if (limit() && instances().length > limit()!) {
        <div class="small muted more">+ {{ instances().length - limit()! }} more</div>
      }
    }
  `,
  styles: `:host { display: block; min-width: 0; } .reason { max-width: 280px; } td fu-status { margin-left: 6px; } .more { padding: 8px 16px; border-top: 1px solid var(--border); }`,
})
export class RecurringInstanceTableComponent {
  readonly instances = input<RecurringInstanceDto[]>([]);
  readonly loading = input(false);
  readonly showDefinition = input(false);
  /** Show at most N rows. */
  readonly limit = input<number | null>(null);

  protected readonly visible = computed(() => {
    const l = this.limit();
    return l ? this.instances().slice(0, l) : this.instances();
  });
  readonly emptyTitle = input('No occurrences');
  readonly emptyMessage = input('');

  protected lateText(min: number): string {
    if (min < 60) return `${min} min`;
    if (min < 1440) return `${Math.round(min / 60)}h`;
    return `${Math.round(min / 1440)} days`;
  }
}
