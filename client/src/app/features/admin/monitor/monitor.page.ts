import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval, tap } from 'rxjs';
import { AdminApi } from '../../../core/api/admin.api';
import { MonitorOverview } from '../../../core/models/admin.models';
import { refreshOn } from '../../../core/realtime/realtime.service';
import { loader } from '../../../core/util/loader';
import { IconComponent } from '../../../shared/ui/icon.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { CheckpointsTableComponent } from './checkpoints-table.component';
import { ConnectionCardComponent } from './connection-card.component';
import { ExecutionsTableComponent } from './executions-table.component';
import { FailedMessagesComponent } from './failed-messages.component';
import { HostsCardComponent } from './hosts-card.component';
import { IntegrationErrorsComponent } from './integration-errors.component';
import { JobsTableComponent } from './jobs-table.component';
import { MonitorStatsComponent } from './monitor-stats.component';
import { NotificationQueueComponent } from './notification-queue.component';

const AUTO_REFRESH_MS = 10_000;

/**
 * /admin/monitor — integration monitor (spec §67): Microsoft 365 connection, sync checkpoints, background jobs,
 * notification queue, integration errors, failed messages and host heartbeats. Auto-refreshes every 10 s
 * (skipped while the tab is hidden) and on sync / job hub events.
 */
@Component({
  selector: 'fu-monitor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    IconComponent,
    SkeletonComponent,
    ConnectionCardComponent,
    MonitorStatsComponent,
    CheckpointsTableComponent,
    JobsTableComponent,
    ExecutionsTableComponent,
    HostsCardComponent,
    NotificationQueueComponent,
    IntegrationErrorsComponent,
    FailedMessagesComponent,
  ],
  templateUrl: './monitor.page.html',
  styleUrl: './monitor.page.css',
})
export class MonitorPage {
  private readonly api = inject(AdminApi);
  private readonly toast = inject(ToastService);
  private readonly document = inject(DOCUMENT);

  private readonly queue = viewChild(NotificationQueueComponent);
  private readonly errors = viewChild(IntegrationErrorsComponent);
  private readonly failed = viewChild(FailedMessagesComponent);

  protected readonly data = loader<MonitorOverview>();
  protected readonly autoRefresh = signal(true);
  protected readonly syncing = signal(false);
  /** Ticks every second so relative times ("Updated 7s ago") stay current. */
  protected readonly now = signal(Date.now());
  private readonly updatedAt = signal<number | null>(null);

  protected readonly updatedText = computed(() => {
    const at = this.updatedAt();
    if (at === null) return '';
    const s = Math.max(0, Math.round((this.now() - at) / 1000));
    if (s < 5) return 'Updated just now';
    if (s < 60) return `Updated ${s}s ago`;
    return `Updated ${Math.floor(s / 60)} min ago`;
  });

  protected readonly checkpointCount = computed(() => (this.data.value()?.checkpoints ?? []).length);
  protected readonly jobCount = computed(() => (this.data.value()?.jobs ?? []).length);

  protected readonly jobNames = computed<Record<string, string>>(() =>
    Object.fromEntries((this.data.value()?.jobs ?? []).map((j) => [j.key, j.name || j.key])),
  );

  constructor() {
    this.reload();
    refreshOn(['sync.status', 'job.status'], () => this.reload(true));
    interval(1000)
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.now.set(Date.now()));
    interval(AUTO_REFRESH_MS)
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        if (this.autoRefresh() && !this.document.hidden) this.reload(true);
      });
  }

  /** Reloads the overview and (silently) every independently loaded section. */
  protected reload(silent = false): void {
    this.data.load(this.api.monitor().pipe(tap(() => this.updatedAt.set(Date.now()))), { silent });
    this.queue()?.reload(true);
    this.errors()?.reload(true);
    this.failed()?.reload(true);
  }

  protected toggleAutoRefresh(): void {
    const on = !this.autoRefresh();
    this.autoRefresh.set(on);
    if (on) this.reload(true);
  }

  protected runSyncNow(): void {
    this.syncing.set(true);
    this.api.runSyncNow().subscribe({
      next: () => {
        this.syncing.set(false);
        this.toast.success('Sync started — checkpoints update as each source completes');
        this.reload(true);
      },
      error: () => this.syncing.set(false),
    });
  }

  /** Stat cards scroll to the section that explains the number. */
  protected jump(id: string): void {
    this.document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
}
