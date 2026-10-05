import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';
import { loader } from '../../core/util/loader';
import { humanize } from '../../core/util/humanize';
import { AgoPipe, DateTimePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { eventValue } from './sim-feedback';
import { SimulatorApi } from './simulator.api';
import { SimActivity } from './simulator.models';

const AUTO_REFRESH_MS = 10_000;

/** Colour by what happened: injected faults and errors stand out, normal traffic stays quiet. */
function kindTone(kind: string): string {
  const k = kind.toLowerCase();
  if (k.includes('error') || k.includes('outage') || k.includes('auth') || k.includes('fail')) return 'danger';
  if (k.includes('throttl') || k.includes('429') || k.includes('fault') || k.includes('latency') || k.includes('delta') || k.includes('gone')) return 'warning';
  if (k.includes('mail') || k.includes('chat') || k.includes('channel') || k.includes('post')) return 'info';
  return 'neutral';
}

/** Activity feed of the emulated tenant (posts, Graph requests, injected faults). Auto-refreshes every 10 s. */
@Component({
  selector: 'fu-sim-activity',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgoPipe, DateTimePipe, EmptyStateComponent, IconComponent, SkeletonComponent],
  template: `
    <section class="card">
      <header class="card-header wrap">
        <fu-icon name="activity" class="muted" />
        <h2>Activity feed</h2>
        <span class="count-pill">{{ visible().length }}</span>
        <span class="xsmall muted">Refreshes every 10 seconds</span>
        <span class="spacer"></span>
        <label for="sim-activity-kind" class="sr-only">Kind</label>
        <select id="sim-activity-kind" class="select input-sm kind" (change)="kind.set(value($event))">
          <option value="" [selected]="!kind()">All kinds</option>
          @for (k of kinds(); track k) {
            <option [value]="k" [selected]="k === kind()">{{ label(k) }}</option>
          }
        </select>
        <input class="input input-sm search" type="search" placeholder="Filter…" aria-label="Filter activity" [value]="query()" (input)="query.set(value($event))" />
        <button type="button" class="btn btn-ghost btn-icon btn-sm" aria-label="Refresh activity" (click)="reload()"><fu-icon name="refresh" /></button>
      </header>
      <div class="card-body flush">
        @if (data.loading() && !data.value()) {
          <fu-skeleton variant="table" [rows]="8" />
        } @else if (data.error()) {
          <div class="card-body">
            <div class="alert tone-danger" role="alert">
              <fu-icon name="alert-circle" /> <span class="spacer">{{ data.error() }}</span>
              <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
            </div>
          </div>
        } @else if (!visible().length) {
          <fu-empty icon="activity" title="No activity" [message]="query() || kind() ? 'Nothing matches the filter.' : 'Send a mail or post a message to see it here.'" [compact]="true" />
        } @else {
          <ul class="feed">
            @for (a of visible(); track a.id) {
              <li class="entry">
                <span class="when small muted nowrap" [attr.title]="a.at | dateTime">{{ (a.at | ago) || '—' }}</span>
                <span [class]="'chip sm tone-' + tone(a.kind)">{{ label(a.kind) }}</span>
                <div class="what">
                  <div class="small">
                    @if (a.actor) {
                      <strong>{{ a.actor }}</strong> ·
                    }
                    {{ a.summary }}
                  </div>
                  @if (a.details) {
                    <div class="mono xsmall muted truncate" [attr.title]="a.details">{{ a.details }}</div>
                  }
                </div>
              </li>
            }
          </ul>
        }
      </div>
    </section>
  `,
  styles: `
    .kind { width: auto; min-width: 140px; }
    .search { width: 180px; }
    .feed { list-style: none; margin: 0; padding: 0; max-height: 640px; overflow-y: auto; }
    .entry { display: grid; grid-template-columns: 90px auto minmax(0, 1fr); gap: 10px; align-items: baseline; padding: 9px 16px; border-bottom: 1px solid var(--border); }
    .entry:last-child { border-bottom: 0; }
    .what { min-width: 0; }
  `,
})
export class SimActivityComponent {
  private readonly api = inject(SimulatorApi);
  private readonly document = inject(DOCUMENT);

  protected readonly data = loader<SimActivity[]>();
  protected readonly kind = signal('');
  protected readonly query = signal('');

  protected readonly all = computed(() => [...(this.data.value() ?? [])].sort((a, b) => (b.at ?? '').localeCompare(a.at ?? '')));
  protected readonly kinds = computed(() => [...new Set(this.all().map((a) => a.kind))].sort());
  protected readonly visible = computed(() => {
    const kind = this.kind();
    const q = this.query().trim().toLowerCase();
    return this.all().filter(
      (a) =>
        (!kind || a.kind === kind) &&
        (!q || a.summary.toLowerCase().includes(q) || (a.details ?? '').toLowerCase().includes(q) || (a.actor ?? '').toLowerCase().includes(q)),
    );
  });

  constructor() {
    this.reload();
    interval(AUTO_REFRESH_MS)
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        if (!this.document.hidden) this.data.load(this.api.activity(), { silent: true });
      });
  }

  protected reload(): void {
    this.data.load(this.api.activity(), { silent: !!this.data.value() });
  }

  protected tone(kind: string): string {
    return kindTone(kind);
  }

  protected label(kind: string): string {
    return humanize(kind);
  }

  protected value(event: Event): string {
    return eventValue(event);
  }
}
