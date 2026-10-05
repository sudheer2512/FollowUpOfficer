import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { AdminApi } from '../../../core/api/admin.api';
import { CheckpointDto } from '../../../core/models/admin.models';
import { formatDateTime } from '../../../core/util/dates';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { DialogService } from '../../../shared/ui/dialog.service';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { checkedOf } from '../shared/form-view';
import { syncStateTone } from './monitor-format';

/** Sync checkpoints (one per mailbox / chat / channel source) with reset, resync, enable and run-now actions. */
@Component({
  selector: 'fu-checkpoints-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgoPipe, DateTimePipe, HumanizePipe, EmptyStateComponent, IconComponent],
  template: `
    @if (!rows().length) {
      <fu-empty icon="database" title="No sync checkpoints" message="Checkpoints appear after the first Outlook or Teams sync runs." [compact]="true" />
    } @else {
      <div class="table-wrap">
        <table class="table compact">
          <thead>
            <tr>
              <th>Type</th>
              <th>Source</th>
              <th>Status</th>
              <th>Last success</th>
              <th>Last attempt</th>
              <th>Next run</th>
              <th title="Last processed item (resume point)">Watermark</th>
              <th>Delta token</th>
              <th>Mid-pagination</th>
              <th class="num">Backlog</th>
              <th class="num" title="Last run / total imported">Items</th>
              <th>Error</th>
              <th>Enabled</th>
              <th><span class="sr-only">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (c of rows(); track c.id) {
              <tr [class.off]="!c.enabled">
                <td class="nowrap small">{{ c.type | humanize }}</td>
                <td class="source">
                  <div class="truncate strong small" [attr.title]="c.displayName">{{ c.displayName || '—' }}</div>
                  <div class="mono xsmall muted truncate" [attr.title]="c.sourceKey">{{ c.sourceKey }}</div>
                </td>
                <td class="nowrap">
                  <span [class]="'badge tone-' + tone(c.status)">{{ c.status | humanize }}</span>
                  @if (c.retryCount > 0) {
                    <div class="xsmall muted mt-4">retry {{ c.retryCount }}</div>
                  }
                </td>
                <td class="nowrap small" [attr.title]="c.lastSuccessfulSyncAt | dateTime">{{ (c.lastSuccessfulSyncAt | ago) || '—' }}</td>
                <td class="nowrap small" [attr.title]="c.lastAttemptAt | dateTime">{{ (c.lastAttemptAt | ago) || '—' }}</td>
                <td class="nowrap small" [attr.title]="c.nextRunAt | dateTime">{{ (c.nextRunAt | ago) || '—' }}</td>
                <td class="nowrap small" [attr.title]="watermarkTitle(c)">{{ (c.lastProcessedAt | dateTime: 'short') || '—' }}</td>
                <td>
                  <span class="badge" [class.tone-good]="c.hasDeltaToken">{{ c.hasDeltaToken ? 'Yes' : 'No' }}</span>
                </td>
                <td>
                  <span class="badge" [class.tone-info]="c.midPagination">{{ c.midPagination ? 'Yes' : 'No' }}</span>
                </td>
                <td class="num" [class.text-warning]="c.pendingMessages > 0">{{ c.pendingMessages }}</td>
                <td class="num nowrap">{{ c.lastRunItemCount }} / {{ c.itemsImportedTotal }}</td>
                <td class="error-cell">
                  @if (c.error) {
                    <span class="text-danger small truncate" [attr.title]="c.error">{{ c.error }}</span>
                  } @else {
                    <span class="muted">—</span>
                  }
                </td>
                <td>
                  <label class="switch">
                    <input
                      type="checkbox"
                      [checked]="c.enabled"
                      [disabled]="busy() === c.id"
                      [attr.aria-label]="(c.enabled ? 'Disable ' : 'Enable ') + (c.displayName || c.sourceKey)"
                      (change)="setEnabled(c, $event)"
                    />
                  </label>
                </td>
                <td class="actions">
                  <button type="button" class="btn btn-ghost btn-icon btn-xs" title="Run sync now" [attr.aria-label]="'Run sync now for ' + c.displayName" [disabled]="busy() === c.id || !c.enabled" (click)="runNow(c)">
                    <fu-icon name="play" [size]="13" />
                  </button>
                  <button type="button" class="btn btn-ghost btn-icon btn-xs" title="Reset (clear error, resume from watermark)" [attr.aria-label]="'Reset ' + c.displayName" [disabled]="busy() === c.id" (click)="reset(c)">
                    <fu-icon name="rotate" [size]="13" />
                  </button>
                  <button type="button" class="btn btn-ghost btn-icon btn-xs danger" title="Resync from scratch" [attr.aria-label]="'Resync ' + c.displayName + ' from scratch'" [disabled]="busy() === c.id" (click)="resync(c)">
                    <fu-icon name="refresh" [size]="13" />
                  </button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: `
    .source { max-width: 240px; }
    .error-cell { max-width: 240px; }
    .error-cell .truncate { display: block; }
    tr.off td { color: var(--muted); }
    .btn.danger { color: var(--danger); }
  `,
})
export class CheckpointsTableComponent {
  private readonly api = inject(AdminApi);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);

  readonly checkpoints = input<CheckpointDto[] | null | undefined>([]);
  /** Emitted after an action so the page reloads the overview. */
  readonly changed = output<void>();

  protected readonly busy = signal<string | null>(null);
  protected readonly rows = computed(() =>
    (this.checkpoints() ?? [])
      .map((c) => ({
        ...c,
        pendingMessages: c.pendingMessages ?? 0,
        lastRunItemCount: c.lastRunItemCount ?? 0,
        itemsImportedTotal: c.itemsImportedTotal ?? 0,
        retryCount: c.retryCount ?? 0,
      }))
      .sort((a, b) => (a.type ?? '').localeCompare(b.type ?? '') || (a.displayName ?? '').localeCompare(b.displayName ?? '')),
  );

  protected tone(status: string): string {
    return syncStateTone(status);
  }

  protected watermarkTitle(c: CheckpointDto): string {
    const at = c.lastProcessedAt ? formatDateTime(c.lastProcessedAt) : 'nothing processed yet';
    return c.lastProcessedExternalId ? `${at} · last item ${c.lastProcessedExternalId}` : at;
  }

  protected runNow(c: CheckpointDto): void {
    this.act(c, this.api.runSyncNow(), 'Sync started — results appear here in a few seconds');
  }

  protected async reset(c: CheckpointDto): Promise<void> {
    const ok = await this.dialog.confirm({
      title: 'Reset checkpoint',
      message: `Reset “${c.displayName}”? The error and retry count are cleared and the next sync resumes from the stored watermark — nothing is re-imported.`,
      confirmText: 'Reset',
    });
    if (ok) this.act(c, this.api.resetCheckpoint(c.id, false), `${c.displayName} reset`);
  }

  protected async resync(c: CheckpointDto): Promise<void> {
    const ok = await this.dialog.confirm({
      title: 'Resync from scratch',
      message:
        `Re-import “${c.displayName}” from the beginning? The delta token and watermark are discarded and every message is read again. ` +
        'Messages that were already imported are recognised and skipped, so no duplicates are created — but a full resync takes longer and uses Graph quota.',
      confirmText: 'Resync from scratch',
      danger: true,
    });
    if (ok) this.act(c, this.api.resetCheckpoint(c.id, true), `${c.displayName} will resync from scratch`);
  }

  protected setEnabled(c: CheckpointDto, event: Event): void {
    const input = event.target as HTMLInputElement;
    const enabled = checkedOf(event);
    this.busy.set(c.id);
    this.api.setCheckpointEnabled(c.id, enabled).subscribe({
      next: () => {
        this.busy.set(null);
        this.toast.success(`${c.displayName} ${enabled ? 'enabled' : 'paused'}`);
        this.changed.emit();
      },
      error: () => {
        this.busy.set(null);
        // Put the switch back: the bound value did not change, so Angular will not re-render it.
        input.checked = !enabled;
      },
    });
  }

  private act(c: CheckpointDto, request: Observable<unknown>, success: string): void {
    this.busy.set(c.id);
    request.subscribe({
      next: () => {
        this.busy.set(null);
        this.toast.success(success);
        this.changed.emit();
      },
      error: () => this.busy.set(null),
    });
  }
}
