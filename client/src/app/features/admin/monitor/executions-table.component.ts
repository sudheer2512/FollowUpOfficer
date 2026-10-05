import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { JobExecutionDto } from '../../../core/models/admin.models';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { formatDuration, jobOutcomeTone, jobTriggerTone } from './monitor-format';

/**
 * Job executions: when, trigger (Schedule / CatchUp / Manual / Startup), host, outcome
 * (incl. Abandoned / Skipped), duration, items and message or error.
 * Pass `jobNames` to add a Job column (the overview's recent executions span all jobs).
 */
@Component({
  selector: 'fu-executions-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgoPipe, DateTimePipe, HumanizePipe, EmptyStateComponent],
  template: `
    @if (!rows().length) {
      <fu-empty icon="clock" title="No executions yet" [message]="emptyMessage()" [compact]="true" />
    } @else {
      <div class="table-wrap">
        <table class="table compact">
          <thead>
            <tr>
              <th>Started</th>
              @if (jobNames()) {
                <th>Job</th>
              }
              <th>Trigger</th>
              <th>Host</th>
              <th>Outcome</th>
              <th class="num">Duration</th>
              <th class="num">Items</th>
              @if (showDue()) {
                <th>Due at</th>
              }
              <th>Message</th>
            </tr>
          </thead>
          <tbody>
            @for (x of rows(); track x.id) {
              <tr>
                <td class="nowrap small" [attr.title]="x.startedAt | ago">{{ x.startedAt | dateTime: 'short' }}</td>
                @if (jobNames(); as names) {
                  <td class="small nowrap">{{ names[x.jobKey] || x.jobKey }}</td>
                }
                <td><span [class]="'badge tone-' + triggerTone(x.trigger)">{{ x.trigger | humanize }}</span></td>
                <td class="mono xsmall nowrap">{{ x.host || '—' }}</td>
                <td><span [class]="'badge tone-' + outcomeTone(x.outcome)">{{ x.outcome | humanize }}</span></td>
                <td class="num nowrap small">{{ duration(x) }}</td>
                <td class="num small">{{ x.itemsProcessed }}</td>
                @if (showDue()) {
                  <td class="nowrap small muted">{{ (x.dueAt | dateTime: 'short') || '—' }}</td>
                }
                <td class="msg">
                  @if (x.error) {
                    <span class="text-danger small clamp-2" [attr.title]="x.error">{{ x.error }}</span>
                  } @else if (x.message) {
                    <span class="small clamp-2" [attr.title]="x.message">{{ x.message }}</span>
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
  styles: `.msg { min-width: 200px; max-width: 360px; }`,
})
export class ExecutionsTableComponent {
  readonly executions = input<JobExecutionDto[] | null | undefined>([]);
  /** Job key → name; when set, a Job column is shown. */
  readonly jobNames = input<Record<string, string> | null>(null);
  readonly showDue = input(false);
  readonly emptyMessage = input('Runs appear here as jobs execute.');

  protected readonly rows = computed(() =>
    (this.executions() ?? [])
      .map((x) => ({ ...x, itemsProcessed: x.itemsProcessed ?? 0 }))
      .sort((a, b) => (b.startedAt ?? '').localeCompare(a.startedAt ?? '')),
  );

  protected triggerTone(trigger: string): string {
    return jobTriggerTone(trigger);
  }

  protected outcomeTone(outcome: string): string {
    return jobOutcomeTone(outcome);
  }

  /** Server duration when present, else derived from start / finish (running jobs show "—"). */
  protected duration(x: JobExecutionDto): string {
    if (x.durationMs !== null && x.durationMs !== undefined) return formatDuration(x.durationMs);
    if (!x.finishedAt) return '—';
    return formatDuration(new Date(x.finishedAt).getTime() - new Date(x.startedAt).getTime());
  }
}
