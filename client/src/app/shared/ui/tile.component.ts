import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Drilldown, Tile } from '../../core/models/common.models';
import { toneOf } from '../../core/util/tone';

/**
 * KPI tile; the whole tile is a drill-down link (Drilldown {route, query}).
 *   <div class="kpi-grid">@for (t of tiles(); track t.key) { <fu-tile [tile]="t" /> }</div>
 */
@Component({
  selector: 'fu-tile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `<a
    class="kpi"
    [class]="'kpi tone-' + tone()"
    [class.zero]="tile().value === 0"
    [routerLink]="link()?.route || null"
    [queryParams]="link()?.query"
    [attr.aria-label]="tile().label + ': ' + tile().value + ' — open list'"
  >
    <span class="kpi-label truncate">{{ tile().label }}</span>
    <span class="kpi-value">{{ tile().value }}</span>
    @if (tile().hint) {
      <span class="kpi-hint truncate">{{ tile().hint }}</span>
    }
  </a>`,
  host: { style: 'display:contents' },
})
export class TileComponent {
  readonly tile = input.required<Tile>();
  /** Defensive: the server may omit the drilldown. */
  protected readonly link = computed(() => this.tile().drilldown as Drilldown | null | undefined);
  protected readonly tone = computed(() => {
    const t = this.tile();
    // A zero count never needs alarm colours.
    return t.value === 0 ? 'neutral' : toneOf(t.tone);
  });
}
