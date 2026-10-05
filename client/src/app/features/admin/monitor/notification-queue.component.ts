import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, map, of, switchMap, tap } from 'rxjs';
import { AdminApi } from '../../../core/api/admin.api';
import { Guid } from '../../../core/models/common.models';
import { NOTIFICATION_STATUS_VALUES, NotificationStatus } from '../../../core/models/enums';
import { NotificationRow } from '../../../core/models/view.models';
import { loader } from '../../../core/util/loader';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { DialogService } from '../../../shared/ui/dialog.service';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { checkedOf, valueOf } from '../shared/form-view';
import { notificationStatusTone } from './monitor-format';

/** Statuses a delivery can be retried from. */
const RETRYABLE = new Set<string>(['Failed', 'DeadLetter', 'Expired']);

/** Outbound notification queue (email / Teams / in-app) with a status filter and retry of failed deliveries. */
@Component({
  selector: 'fu-notification-queue',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, AgoPipe, DateTimePipe, HumanizePipe, EmptyStateComponent, IconComponent, SkeletonComponent],
  template: `
    <section class="card" aria-labelledby="mon-notifications-title">
      <header class="card-header wrap">
        <fu-icon name="send" class="muted" />
        <h2 id="mon-notifications-title">Notification queue</h2>
        <span class="count-pill">{{ rows().length }}</span>
        <span class="spacer"></span>
        <label for="mon-ntf-status" class="sr-only">Status</label>
        <select id="mon-ntf-status" class="select input-sm status" (change)="setStatus($event)">
          <option value="" [selected]="!status()">All statuses</option>
          @for (s of statuses; track s) {
            <option [value]="s" [selected]="s === status()">{{ s | humanize }}</option>
          }
        </select>
        <button type="button" class="btn btn-sm" [disabled]="!selected().size || retrying()" (click)="retrySelected()">
          <fu-icon name="rotate" /> Retry selected{{ selected().size ? ' (' + selected().size + ')' : '' }}
        </button>
        <button type="button" class="btn btn-sm" [disabled]="retrying()" (click)="retryAllDeadLetters()">Retry all dead letters</button>
        <button type="button" class="btn btn-ghost btn-icon btn-sm" aria-label="Refresh notification queue" (click)="reload()">
          <fu-icon name="refresh" />
        </button>
      </header>
      <div class="card-body flush">
        @if (data.loading() && !data.value()) {
          <fu-skeleton variant="table" [rows]="4" />
        } @else if (data.error()) {
          <div class="card-body">
            <div class="alert tone-danger" role="alert">
              <fu-icon name="alert-circle" /> <span class="spacer">{{ data.error() }}</span>
              <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
            </div>
          </div>
        } @else if (!rows().length) {
          <fu-empty
            icon="check-circle"
            [title]="status() ? 'No ' + (status() | humanize).toLowerCase() + ' notifications' : 'The queue is empty'"
            message="Deliveries that fail repeatedly land in the dead-letter queue for a manual retry."
            [compact]="true"
          />
        } @else {
          <div class="table-wrap">
            <table class="table compact">
              <thead>
                <tr>
                  <th class="pick">
                    <input
                      type="checkbox"
                      aria-label="Select all retryable notifications"
                      [checked]="allSelected()"
                      [indeterminate]="someSelected()"
                      [disabled]="!retryable().length"
                      (change)="toggleAll($event)"
                    />
                  </th>
                  <th>Created</th>
                  <th>Recipient</th>
                  <th>Channel</th>
                  <th>Category</th>
                  <th>Subject</th>
                  <th>Status</th>
                  <th class="num">Attempts</th>
                  <th>Next attempt</th>
                  <th>Error</th>
                  <th>Task</th>
                </tr>
              </thead>
              <tbody>
                @for (n of rows(); track n.id) {
                  <tr [class.picked]="selected().has(n.id)">
                    <td class="pick">
                      <input
                        type="checkbox"
                        [attr.aria-label]="'Select ' + n.subject"
                        [checked]="selected().has(n.id)"
                        [disabled]="!isRetryable(n)"
                        (change)="toggle(n.id, $event)"
                      />
                    </td>
                    <td class="nowrap small" [attr.title]="n.createdAt | dateTime">{{ n.createdAt | ago }}</td>
                    <td class="small truncate recipient" [attr.title]="n.recipient ?? ''">{{ n.recipient || '—' }}</td>
                    <td class="small nowrap">{{ n.channel | humanize }}</td>
                    <td class="small nowrap">{{ n.category | humanize }}</td>
                    <td class="subject"><span class="small clamp-2" [attr.title]="n.subject">{{ n.subject }}</span></td>
                    <td><span [class]="'badge tone-' + tone(n.status)">{{ n.status | humanize }}</span></td>
                    <td class="num">{{ n.attemptCount }}</td>
                    <td class="nowrap small" [attr.title]="n.nextAttemptAt | dateTime">{{ (n.nextAttemptAt | ago) || '—' }}</td>
                    <td class="error-cell">
                      @if (n.error) {
                        <span class="text-danger small truncate" [attr.title]="n.error">{{ n.error }}</span>
                      } @else {
                        <span class="muted">—</span>
                      }
                    </td>
                    <td class="nowrap">
                      @if (n.taskId) {
                        <a class="task-number" [routerLink]="['/tasks', n.taskId]">{{ n.taskNumber || 'Open task' }}</a>
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
      </div>
    </section>
  `,
  styles: `
    .status { width: auto; min-width: 140px; }
    .pick { width: 32px; }
    .pick input { width: 15px; height: 15px; accent-color: var(--accent); margin: 0; }
    .recipient { max-width: 200px; }
    .subject { min-width: 180px; max-width: 320px; }
    .error-cell { max-width: 220px; }
    .error-cell .truncate { display: block; }
    tr.picked td { background: var(--accent-soft); }
  `,
})
export class NotificationQueueComponent {
  private readonly api = inject(AdminApi);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);

  protected readonly statuses = NOTIFICATION_STATUS_VALUES;
  protected readonly status = signal<NotificationStatus | ''>('DeadLetter');
  protected readonly data = loader<NotificationRow[]>();
  protected readonly selected = signal<ReadonlySet<Guid>>(new Set());
  protected readonly retrying = signal(false);

  protected readonly rows = computed(() =>
    (this.data.value() ?? [])
      .map((n) => ({ ...n, attemptCount: n.attemptCount ?? 0 }))
      .sort((a, b) => (b.createdAt ?? '').localeCompare(a.createdAt ?? '')),
  );
  protected readonly retryable = computed(() => this.rows().filter((n) => this.isRetryable(n)));
  protected readonly allSelected = computed(() => this.retryable().length > 0 && this.retryable().every((n) => this.selected().has(n.id)));
  protected readonly someSelected = computed(() => this.selected().size > 0 && !this.allSelected());

  constructor() {
    this.reload();
  }

  /** Reloads the current filter; the selection keeps only rows that are still present. */
  reload(silent = false): void {
    this.data.load(
      this.api.notifications(this.status() || null).pipe(
        tap((rows) => {
          const ids = new Set((rows ?? []).map((r) => r.id));
          this.selected.update((sel) => new Set([...sel].filter((id) => ids.has(id))));
        }),
      ),
      { silent },
    );
  }

  protected isRetryable(n: NotificationRow): boolean {
    return RETRYABLE.has(n.status);
  }

  protected tone(status: string): string {
    return notificationStatusTone(status);
  }

  protected setStatus(event: Event): void {
    this.status.set(valueOf(event) as NotificationStatus | '');
    this.selected.set(new Set());
    this.reload();
  }

  protected toggle(id: Guid, event: Event): void {
    const on = checkedOf(event);
    this.selected.update((sel) => {
      const next = new Set(sel);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  protected toggleAll(event: Event): void {
    this.selected.set(checkedOf(event) ? new Set(this.retryable().map((n) => n.id)) : new Set());
  }

  protected retrySelected(): void {
    const ids = [...this.selected()];
    if (ids.length) this.retry(of(ids));
  }

  protected async retryAllDeadLetters(): Promise<void> {
    const ok = await this.dialog.confirm({
      title: 'Retry all dead letters',
      message: 'Re-queue every dead-lettered notification for delivery? Notifications that have expired since are skipped by the sender.',
      confirmText: 'Retry all',
    });
    if (!ok) return;
    const ids$ =
      this.status() === 'DeadLetter'
        ? of(this.rows().map((n) => n.id))
        : this.api.notifications('DeadLetter').pipe(map((rows) => (rows ?? []).map((n) => n.id)));
    this.retry(ids$);
  }

  private retry(ids$: Observable<Guid[]>): void {
    this.retrying.set(true);
    ids$
      .pipe(
        switchMap((ids) =>
          ids.length ? this.api.retryNotifications(ids).pipe(map((r) => r?.retried ?? ids.length)) : of(0),
        ),
      )
      .subscribe({
        next: (count) => {
          this.retrying.set(false);
          this.selected.set(new Set());
          this.toast.success(count ? `Retried ${count}` : 'Nothing to retry');
          this.reload(true);
        },
        error: () => this.retrying.set(false),
      });
  }
}
