import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HostHeartbeat } from '../../../core/models/admin.models';
import { DateTimeUtc } from '../../../core/models/common.models';
import { AgoPipe, DateTimePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';

/** A host that has not checked in for this long is considered down. */
const STALE_MS = 2 * 60_000;

/** Worker / API hosts and their heartbeats (job leases fail over when a host goes stale). */
@Component({
  selector: 'fu-hosts-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgoPipe, DateTimePipe, EmptyStateComponent, IconComponent],
  template: `
    <section class="card" aria-labelledby="mon-hosts-title">
      <header class="card-header">
        <fu-icon name="server" class="muted" />
        <h2 id="mon-hosts-title">Hosts</h2>
        <span class="count-pill">{{ rows().length }}</span>
        @if (staleCount()) {
          <span class="badge tone-danger">{{ staleCount() }} stale</span>
        }
      </header>
      <div class="card-body flush">
        @if (!rows().length) {
          <fu-empty icon="server" title="No host heartbeats" message="Hosts report in while they run background jobs." [compact]="true" />
        } @else {
          <div class="list">
            @for (h of rows(); track h.host) {
              <div class="list-row">
                <span [class]="'dot ' + (h.stale ? 'tone-danger' : 'tone-good')" aria-hidden="true"></span>
                <div class="main">
                  <div class="title mono small truncate">{{ h.host }}</div>
                  <div class="meta">
                    <span [class.text-danger]="h.stale" [attr.title]="h.lastSeen | dateTime">
                      {{ h.stale ? 'Stale — last seen ' : 'Last seen ' }}{{ h.lastSeen | ago }}
                    </span>
                  </div>
                </div>
                <span class="small muted nowrap"><span class="num strong">{{ h.executions24h }}</span> runs (24h)</span>
              </div>
            }
          </div>
        }
      </div>
    </section>
  `,
})
export class HostsCardComponent {
  readonly hosts = input<HostHeartbeat[] | null | undefined>([]);
  /** Reference instant for staleness (MonitorOverview.serverTime avoids client clock skew). */
  readonly serverTime = input<DateTimeUtc | null | undefined>(null);

  protected readonly rows = computed(() => {
    const ref = this.serverTime() ? new Date(this.serverTime() as string).getTime() : Date.now();
    const now = isNaN(ref) ? Date.now() : ref;
    return [...(this.hosts() ?? [])]
      .map((h) => {
        const seen = new Date(h.lastSeen).getTime();
        return { ...h, executions24h: h.executions24h ?? 0, stale: isNaN(seen) || now - seen > STALE_MS };
      })
      .sort((a, b) => (b.lastSeen ?? '').localeCompare(a.lastSeen ?? ''));
  });

  protected readonly staleCount = computed(() => this.rows().filter((h) => h.stale).length);
}
