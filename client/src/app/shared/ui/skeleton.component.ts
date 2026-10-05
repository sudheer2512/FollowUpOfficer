import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * Loading placeholders.
 *   <fu-skeleton variant="table" [rows]="6" />   <fu-skeleton variant="tiles" [rows]="8" />
 *   <fu-skeleton variant="lines" [rows]="3" />   <fu-skeleton variant="cards" [rows]="4" />
 */
@Component({
  selector: 'fu-skeleton',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (variant()) {
      @case ('tiles') {
        <div class="kpi-grid">
          @for (i of items(); track i) {
            <div class="card pad"><div class="skeleton" style="height:12px;width:60%"></div><div class="skeleton mt-8" style="height:24px;width:40%"></div></div>
          }
        </div>
      }
      @case ('cards') {
        <div class="grid-cards">
          @for (i of items(); track i) {
            <div class="card pad stack">
              <div class="skeleton" style="height:14px;width:55%"></div>
              <div class="skeleton" style="height:10px;width:90%"></div>
              <div class="skeleton" style="height:10px;width:75%"></div>
              <div class="skeleton mt-8" style="height:6px;width:100%"></div>
            </div>
          }
        </div>
      }
      @case ('table') {
        <div class="tbl">
          @for (i of items(); track i) {
            <div class="tr">
              <div class="skeleton" style="height:12px;width:70px"></div>
              <div class="skeleton" [style.width.%]="40 + ((i * 17) % 35)" style="height:12px"></div>
              <div class="skeleton" style="height:12px;width:80px"></div>
              <div class="skeleton" style="height:12px;width:60px"></div>
            </div>
          }
        </div>
      }
      @default {
        <div class="stack">
          @for (i of items(); track i) {
            <div class="skeleton" [style.width.%]="60 + ((i * 13) % 40)" style="height:12px"></div>
          }
        </div>
      }
    }
  `,
  styles: `
    :host { display: block; }
    .tbl { display: flex; flex-direction: column; }
    .tr { display: flex; gap: 24px; align-items: center; padding: 13px 16px; border-bottom: 1px solid var(--border); }
    .tr:last-child { border-bottom: 0; }
  `,
  host: { 'aria-busy': 'true', 'aria-label': 'Loading' },
})
export class SkeletonComponent {
  readonly variant = input<'lines' | 'table' | 'tiles' | 'cards'>('lines');
  readonly rows = input(4);
  protected readonly items = computed(() => Array.from({ length: this.rows() }, (_, i) => i));
}
