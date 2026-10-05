import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SummaryPoint } from '../../core/models/view.models';
import { toneOf } from '../../core/util/tone';

/**
 * Executive-summary points: a tone dot per point and, when the server supplies one, a drill-down link.
 *   <fu-summary-points [points]="summary.points" [limit]="5" />
 */
@Component({
  selector: 'fu-summary-points',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `<ul class="points" [class.compact]="compact()">
    @for (p of visible(); track $index) {
      <li>
        <span class="dot tone-{{ tone(p.tone) }}" aria-hidden="true"></span>
        @if (p.drilldown?.route) {
          <a [routerLink]="p.drilldown!.route" [queryParams]="p.drilldown!.query">{{ p.text }}</a>
        } @else {
          <span>{{ p.text }}</span>
        }
      </li>
    }
  </ul>`,
  styles: `
    .points { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
    li { display: flex; gap: 10px; align-items: baseline; font-size: 13.5px; line-height: 1.45; }
    li .dot { position: relative; top: -1px; }
    .compact { gap: 6px; }
    .compact li { font-size: 12.5px; }
    a { color: var(--text); text-decoration: underline; text-decoration-color: var(--border-strong); text-underline-offset: 3px; }
    a:hover { color: var(--accent-text); text-decoration-color: currentColor; }
  `,
})
export class SummaryPointsComponent {
  readonly points = input<SummaryPoint[] | null | undefined>([]);
  readonly limit = input<number | null>(null);
  readonly compact = input(false);

  protected readonly visible = computed(() => {
    const list = this.points() ?? [];
    const l = this.limit();
    return l ? list.slice(0, l) : list;
  });

  protected tone(t: string | null | undefined): string {
    return toneOf(t);
  }
}
