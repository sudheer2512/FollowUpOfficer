import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { AdminApi } from '../../../core/api/admin.api';
import { IntegrationErrorDto } from '../../../core/models/admin.models';
import { loader } from '../../../core/util/loader';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { checkedOf } from '../shared/form-view';
import { errorKindTone } from './monitor-format';

const INITIAL_ROWS = 25;

/** Microsoft Graph / delivery errors (auth, permission, throttling, 410 Gone…) — full list from /api/admin/errors. */
@Component({
  selector: 'fu-integration-errors',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgoPipe, DateTimePipe, HumanizePipe, EmptyStateComponent, IconComponent],
  template: `
    <section class="card" aria-labelledby="mon-errors-title">
      <header class="card-header wrap">
        <fu-icon name="alert-triangle" class="muted" />
        <h2 id="mon-errors-title">Integration errors</h2>
        <span class="count-pill" [class.danger]="openCount() > 0">{{ openCount() }} open</span>
        <span class="spacer"></span>
        <label class="switch small">
          <input type="checkbox" [checked]="openOnly()" (change)="openOnly.set(checked($event))" />
          Unresolved only
        </label>
        <button type="button" class="btn btn-ghost btn-icon btn-sm" aria-label="Refresh integration errors" (click)="reload()">
          <fu-icon name="refresh" />
        </button>
      </header>
      <div class="card-body flush">
        @if (data.error() && !all().length) {
          <div class="card-body">
            <div class="alert tone-danger" role="alert">
              <fu-icon name="alert-circle" /> <span class="spacer">{{ data.error() }}</span>
              <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
            </div>
          </div>
        } @else if (!filtered().length) {
          <fu-empty icon="check-circle" [title]="openOnly() ? 'No unresolved errors' : 'No integration errors'" message="Graph calls are succeeding." [compact]="true" />
        } @else {
          <div class="table-wrap">
            <table class="table compact">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Integration</th>
                  <th>Source</th>
                  <th>Operation</th>
                  <th>Kind</th>
                  <th class="num">Status</th>
                  <th>Message</th>
                  <th class="num">Retry after</th>
                  <th>Resolved</th>
                </tr>
              </thead>
              <tbody>
                @for (e of visible(); track e.id) {
                  <tr>
                    <td class="nowrap small" [attr.title]="e.occurredAt | dateTime">{{ e.occurredAt | ago }}</td>
                    <td class="small nowrap">{{ e.integration | humanize }}</td>
                    <td class="mono xsmall source truncate" [attr.title]="e.sourceKey ?? ''">{{ e.sourceKey || '—' }}</td>
                    <td class="small">{{ e.operation }}</td>
                    <td><span [class]="'badge tone-' + tone(e.kind)">{{ e.kind | humanize }}</span></td>
                    <td class="num mono small">{{ e.statusCode ?? '—' }}</td>
                    <td class="message"><span class="small clamp-2" [attr.title]="e.message">{{ e.message }}</span></td>
                    <td class="num small nowrap">{{ e.retryAfterSeconds !== null && e.retryAfterSeconds !== undefined ? e.retryAfterSeconds + ' s' : '—' }}</td>
                    <td>
                      <span class="badge" [class.tone-good]="e.resolved" [class.tone-warning]="!e.resolved">{{ e.resolved ? 'Resolved' : 'Open' }}</span>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (filtered().length > visible().length) {
            <div class="card-footer">
              <button type="button" class="btn btn-ghost btn-sm" (click)="showAll.set(true)">Show all {{ filtered().length }}</button>
            </div>
          }
        }
      </div>
    </section>
  `,
  styles: `
    .source { max-width: 180px; }
    .message { min-width: 220px; max-width: 420px; }
  `,
})
export class IntegrationErrorsComponent {
  private readonly api = inject(AdminApi);

  /** MonitorOverview.recentErrors — shown until (or if) the full list cannot be loaded. */
  readonly recent = input<IntegrationErrorDto[] | null | undefined>([]);

  protected readonly data = loader<IntegrationErrorDto[]>();
  protected readonly openOnly = signal(false);
  protected readonly showAll = signal(false);

  protected readonly all = computed(() =>
    [...(this.data.value() ?? this.recent() ?? [])].sort((a, b) => (b.occurredAt ?? '').localeCompare(a.occurredAt ?? '')),
  );
  protected readonly filtered = computed(() => (this.openOnly() ? this.all().filter((e) => !e.resolved) : this.all()));
  protected readonly visible = computed(() => (this.showAll() ? this.filtered() : this.filtered().slice(0, INITIAL_ROWS)));
  protected readonly openCount = computed(() => this.all().filter((e) => !e.resolved).length);

  constructor() {
    this.reload();
  }

  reload(silent = false): void {
    this.data.load(this.api.errors(), { silent });
  }

  protected tone(kind: string): string {
    return errorKindTone(kind);
  }

  protected checked(event: Event): boolean {
    return checkedOf(event);
  }
}
