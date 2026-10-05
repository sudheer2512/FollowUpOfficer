import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { tap } from 'rxjs';
import { loader } from '../../core/util/loader';
import { DialogService } from '../../shared/ui/dialog.service';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ToastService } from '../../shared/ui/toast.service';
import { eventChecked, eventValue } from './sim-feedback';
import { SimulatorApi } from './simulator.api';
import { SimFaults } from './simulator.models';

const NO_FAULTS: SimFaults = { outage: false, throttlePercent: 0, authFailure: false, latencyMs: 0 };
const MAX_LATENCY_MS = 60_000;

function sameFaults(a: SimFaults | undefined, b: SimFaults): boolean {
  return !!a && a.outage === b.outage && a.throttlePercent === b.throttlePercent && a.authFailure === b.authFailure && a.latencyMs === b.latencyMs;
}

/** Fault injection for resilience demos: outage, throttling, auth failure, latency and delta-token invalidation. */
@Component({
  selector: 'fu-sim-faults',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, SkeletonComponent],
  templateUrl: './sim-faults.component.html',
  styles: `
    .fault { display: grid; grid-template-columns: 240px minmax(0, 1fr); gap: 8px 24px; align-items: start; padding: 14px 16px; border-bottom: 1px solid var(--border); }
    .fault:last-child { border-bottom: 0; }
    .control { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
    .control .name { font-weight: 600; }
    .slider { display: flex; align-items: center; gap: 10px; }
    .slider input { flex: 1; accent-color: var(--accent); }
    .pct { width: 44px; text-align: right; font-weight: 600; }
    .latency { width: 120px; }
    .demo { font-size: 13px; color: var(--text-2); }
    .demo .label { display: block; font-size: 11.5px; text-transform: uppercase; letter-spacing: 0.05em; color: var(--muted); margin-bottom: 2px; }
    @media (max-width: 760px) { .fault { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class SimFaultsComponent {
  private readonly api = inject(SimulatorApi);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);

  protected readonly maxLatency = MAX_LATENCY_MS;
  protected readonly data = loader<SimFaults>();
  /** Edited values; applied with PUT faults. */
  protected readonly draft = signal<SimFaults>(NO_FAULTS);
  protected readonly saving = signal(false);
  protected readonly invalidating = signal(false);

  protected readonly dirty = computed(() => !sameFaults(this.data.value(), this.draft()));

  /** Faults currently in force on the emulator (not the unsaved draft). */
  protected readonly active = computed(() => {
    const f = this.data.value();
    if (!f) return [];
    const list: string[] = [];
    if (f.outage) list.push('Outage');
    if (f.throttlePercent > 0) list.push(`${f.throttlePercent}% throttling`);
    if (f.authFailure) list.push('Authentication failure');
    if (f.latencyMs > 0) list.push(`${f.latencyMs} ms latency`);
    return list;
  });

  constructor() {
    this.reload();
  }

  protected reload(): void {
    this.data.load(this.api.faults().pipe(tap((f) => this.draft.set(f))));
  }

  protected setOutage(event: Event): void {
    this.draft.update((d) => ({ ...d, outage: eventChecked(event) }));
  }

  protected setAuthFailure(event: Event): void {
    this.draft.update((d) => ({ ...d, authFailure: eventChecked(event) }));
  }

  protected setThrottle(event: Event): void {
    const v = Math.round(Number(eventValue(event)) || 0);
    this.draft.update((d) => ({ ...d, throttlePercent: Math.max(0, Math.min(100, v)) }));
  }

  protected setLatency(event: Event): void {
    const v = Math.round(Number(eventValue(event)) || 0);
    this.draft.update((d) => ({ ...d, latencyMs: Math.max(0, Math.min(MAX_LATENCY_MS, v)) }));
  }

  protected discard(): void {
    const f = this.data.value();
    if (f) this.draft.set(f);
  }

  protected apply(): void {
    this.save(this.draft(), 'Faults applied — watch the integration monitor');
  }

  protected restore(): void {
    this.save(NO_FAULTS, 'All faults cleared — syncs recover from their checkpoints');
  }

  private save(faults: SimFaults, message: string): void {
    if (this.saving()) return;
    this.saving.set(true);
    this.api.setFaults(faults).subscribe({
      next: (applied) => {
        this.saving.set(false);
        this.data.set(applied);
        this.draft.set(applied);
        this.toast.success(message);
      },
      error: () => this.saving.set(false),
    });
  }

  protected async invalidate(): Promise<void> {
    const ok = await this.dialog.confirm({
      title: 'Invalidate delta tokens',
      message:
        'Every stored delta link becomes invalid. The next sync of each mailbox, chat and channel gets 410 Gone and resyncs from its watermark — ' +
        'recorded as a resync event, without duplicates.',
      confirmText: 'Invalidate tokens',
      danger: true,
    });
    if (!ok) return;
    this.invalidating.set(true);
    this.api.invalidateDeltaTokens().subscribe({
      next: () => {
        this.invalidating.set(false);
        this.toast.success('Delta tokens invalidated — the next sync performs a resync');
      },
      error: () => this.invalidating.set(false),
    });
  }
}
