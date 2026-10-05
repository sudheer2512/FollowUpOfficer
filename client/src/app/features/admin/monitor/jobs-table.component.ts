import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { AdminApi } from '../../../core/api/admin.api';
import { JobDto } from '../../../core/models/admin.models';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { checkedOf } from '../shared/form-view';
import { JobExecutionsDrawerComponent } from './job-executions-drawer.component';
import { jobStateTone } from './monitor-format';

/** Background jobs (schedule, state, lease, failures) with run-now, enable and an execution-history drawer. */
@Component({
  selector: 'fu-jobs-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgoPipe, DateTimePipe, HumanizePipe, EmptyStateComponent, IconComponent, JobExecutionsDrawerComponent],
  template: `
    @if (!rows().length) {
      <fu-empty icon="clock" title="No background jobs registered" message="Jobs appear once the worker host has started." [compact]="true" />
    } @else {
      <div class="table-wrap">
        <table class="table compact">
          <thead>
            <tr>
              <th>Job</th>
              <th>Schedule</th>
              <th>State</th>
              <th>Next run</th>
              <th>Last success</th>
              <th>Last error</th>
              <th class="num" title="Consecutive failures">Failures</th>
              <th>Lease owner</th>
              <th>Enabled</th>
              <th><span class="sr-only">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (j of rows(); track j.key) {
              <tr [class.off]="!j.enabled">
                <td class="name">
                  <div class="strong small truncate" [attr.title]="j.description || j.name">{{ j.name || j.key }}</div>
                  <div class="mono xsmall muted">{{ j.key }}</div>
                </td>
                <td class="small">
                  {{ j.scheduleText || (j.scheduleKind | humanize) }}
                  <div class="xsmall muted">misfire: {{ j.misfire | humanize }}</div>
                </td>
                <td><span [class]="'badge tone-' + tone(j.state)">{{ j.state | humanize }}</span></td>
                <td class="nowrap small" [attr.title]="j.nextRunAt | dateTime">{{ (j.nextRunAt | ago) || '—' }}</td>
                <td class="nowrap small" [attr.title]="j.lastSucceededAt | dateTime">{{ (j.lastSucceededAt | ago) || '—' }}</td>
                <td class="error-cell">
                  @if (j.lastError) {
                    <span class="text-danger small truncate" [attr.title]="j.lastError">{{ j.lastError }}</span>
                    @if (j.lastFailedAt) {
                      <span class="xsmall muted">{{ j.lastFailedAt | ago }}</span>
                    }
                  } @else {
                    <span class="muted">—</span>
                  }
                </td>
                <td class="num" [class.text-danger]="j.consecutiveFailures > 0" [class.strong]="j.consecutiveFailures > 0">
                  {{ j.consecutiveFailures }}
                </td>
                <td class="small">
                  @if (j.leaseOwner) {
                    <div class="mono xsmall">{{ j.leaseOwner }}</div>
                    @if (j.leaseExpiresAt) {
                      <div class="xsmall muted" [attr.title]="j.leaseExpiresAt | dateTime">expires {{ j.leaseExpiresAt | ago }}</div>
                    }
                  } @else {
                    <span class="muted">—</span>
                  }
                </td>
                <td>
                  <label class="switch">
                    <input
                      type="checkbox"
                      [checked]="j.enabled"
                      [disabled]="busy() === j.key"
                      [attr.aria-label]="(j.enabled ? 'Disable ' : 'Enable ') + (j.name || j.key)"
                      (change)="setEnabled(j, $event)"
                    />
                  </label>
                </td>
                <td class="actions">
                  <button type="button" class="btn btn-xs" [disabled]="busy() === j.key || !j.enabled || j.state === 'Running'" (click)="runNow(j)">
                    <fu-icon name="play" [size]="12" /> Run now
                  </button>
                  <button type="button" class="btn btn-ghost btn-xs" [attr.aria-label]="'History of ' + (j.name || j.key)" (click)="history.set(j)">
                    <fu-icon name="clock" [size]="12" /> History
                  </button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }

    @if (history(); as j) {
      <fu-job-executions-drawer [job]="j" (closed)="history.set(null)" />
    }
  `,
  styles: `
    .name { max-width: 240px; }
    .error-cell { max-width: 260px; }
    .error-cell .truncate { display: block; }
    tr.off td { color: var(--muted); }
  `,
})
export class JobsTableComponent {
  private readonly api = inject(AdminApi);
  private readonly toast = inject(ToastService);

  readonly jobs = input<JobDto[] | null | undefined>([]);
  /** Emitted after an action so the page reloads the overview. */
  readonly changed = output<void>();

  protected readonly busy = signal<string | null>(null);
  protected readonly history = signal<JobDto | null>(null);
  protected readonly rows = computed(() =>
    (this.jobs() ?? [])
      .map((j) => ({ ...j, consecutiveFailures: j.consecutiveFailures ?? 0 }))
      .sort((a, b) => (a.name || a.key).localeCompare(b.name || b.key)),
  );

  protected tone(state: string): string {
    return jobStateTone(state);
  }

  protected runNow(j: JobDto): void {
    this.busy.set(j.key);
    this.api.runJob(j.key).subscribe({
      next: () => {
        this.busy.set(null);
        this.toast.success(`${j.name || j.key} started`);
        this.changed.emit();
      },
      error: () => this.busy.set(null),
    });
  }

  protected setEnabled(j: JobDto, event: Event): void {
    const input = event.target as HTMLInputElement;
    const enabled = checkedOf(event);
    this.busy.set(j.key);
    this.api.setJobEnabled(j.key, enabled).subscribe({
      next: () => {
        this.busy.set(null);
        this.toast.success(`${j.name || j.key} ${enabled ? 'enabled' : 'disabled'}`);
        this.changed.emit();
      },
      error: () => {
        this.busy.set(null);
        // Put the switch back: the bound value did not change, so Angular will not re-render it.
        input.checked = !enabled;
      },
    });
  }
}
