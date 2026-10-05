import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { WorkloadDto } from '../../core/models/task.models';
import { UiTone, loadTone } from '../../core/util/tone';
import { AvatarComponent } from '../../shared/domain/indicators';
import { IconComponent } from '../../shared/ui/icon.component';

let panelSeq = 0;

interface WorkloadCard {
  w: WorkloadDto;
  tone: UiTone;
}

/**
 * Workload of the people about to receive a task (spec §42): "Open 17 · Due this week 8 · Overdue 4 · Critical 3"
 * with the server's load label, so the CEO sees who is already stretched before delegating.
 */
@Component({
  selector: 'fu-workload-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AvatarComponent, IconComponent],
  template: `
    <section class="wl" [attr.aria-labelledby]="titleId" [attr.aria-busy]="loading()">
      <header class="wl-head">
        <h3 [id]="titleId" class="section-title"><fu-icon name="activity" [size]="13" /> Current workload</h3>
        @if (loading()) {
          <span class="xsmall muted">Updating…</span>
        }
      </header>
      @if (!cards().length) {
        @if (loading()) {
          <div class="stack">
            <div class="skeleton sk"></div>
            <div class="skeleton sk"></div>
          </div>
        } @else {
          <p class="hint">Pick who is responsible to see how loaded they already are.</p>
        }
      } @else {
        <div class="cards" [class.stale]="loading()">
          @for (c of cards(); track c.w.employeeId) {
            <article class="card-wl" [attr.aria-label]="c.w.name + ', ' + (c.w.load || 'load unknown')">
              <div class="who">
                <fu-avatar [name]="c.w.name" size="sm" />
                <div class="names">
                  <div class="nm truncate">{{ c.w.name }}</div>
                  @if (c.w.jobTitle) {
                    <div class="xsmall muted truncate">{{ c.w.jobTitle }}</div>
                  }
                </div>
                <span class="chip sm tone-{{ c.tone }}">{{ c.w.load || 'Unknown' }}</span>
              </div>
              <div class="stats">
                <span>Open <b>{{ c.w.openTasks }}</b></span>
                <span>Due this week <b>{{ c.w.dueThisWeek }}</b></span>
                <span>Overdue <b [class.text-danger]="c.w.overdue > 0">{{ c.w.overdue }}</b></span>
                <span>Critical <b [class.text-orange]="c.w.critical > 0">{{ c.w.critical }}</b></span>
              </div>
              @if (c.w.blocked > 0 || c.w.noResponse > 0) {
                <div class="stats minor">
                  @if (c.w.blocked > 0) {
                    <span>Blocked <b>{{ c.w.blocked }}</b></span>
                  }
                  @if (c.w.noResponse > 0) {
                    <span>No response <b>{{ c.w.noResponse }}</b></span>
                  }
                </div>
              }
            </article>
          }
        </div>
      }
    </section>
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .wl { border: 1px solid var(--border); border-radius: var(--radius); background: var(--surface-2); padding: 12px; }
    .wl-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 8px; }
    .wl-head .section-title { margin: 0; display: inline-flex; align-items: center; gap: 6px; }
    .hint { margin: 0; }
    .cards { display: flex; flex-direction: column; gap: 8px; transition: opacity .15s; }
    .cards.stale { opacity: .6; }
    .card-wl { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 9px 10px; min-width: 0; }
    .who { display: flex; align-items: center; gap: 8px; min-width: 0; }
    .names { flex: 1; min-width: 0; line-height: 1.25; }
    .nm { font-weight: 500; font-size: 13px; }
    .stats { display: flex; flex-wrap: wrap; gap: 2px 6px; margin-top: 6px; font-size: 12px; color: var(--muted); }
    .stats > span + span::before { content: '·'; margin-right: 6px; color: var(--subtle); }
    .stats b { color: var(--text); font-weight: 600; font-variant-numeric: tabular-nums; }
    .stats b.text-danger { color: var(--danger); }
    .stats b.text-orange { color: var(--orange); }
    .stats.minor { margin-top: 2px; }
    .sk { height: 58px; border-radius: var(--radius-sm); }
  `,
})
export class WorkloadPanelComponent {
  protected readonly titleId = `fu-workload-title-${++panelSeq}`;
  readonly items = input<WorkloadDto[]>([]);
  readonly loading = input(false);

  protected readonly cards = computed<WorkloadCard[]>(() => (this.items() ?? []).map((w) => ({ w, tone: loadTone(w.load) })));
}
