import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TeamStatusRow } from '../../core/models/view.models';
import { percent } from '../../core/util/humanize';
import { HealthDotComponent } from '../../shared/domain/indicators';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';

/**
 * Weekly review team status: open, overdue, completed, blocked, on-time % and health per team; the team name
 * opens its dashboard.
 */
@Component({
  selector: 'fu-team-status-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HealthDotComponent, EmptyStateComponent],
  template: `
    @if (!rows().length) {
      <fu-empty icon="users" title="No team activity" message="Teams with open or completed work this week appear here." [compact]="true" />
    } @else {
      <div class="table-wrap">
        <table class="table compact">
          <thead>
            <tr>
              <th>Team</th>
              <th class="num">Open</th>
              <th class="num">Overdue</th>
              <th class="num">Completed</th>
              <th class="num">Blocked</th>
              <th>On time</th>
              <th>Health</th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.teamId) {
              <tr>
                <td><a [routerLink]="['/team', r.teamId]" class="strong">{{ r.team }}</a></td>
                <td class="num">{{ r.open }}</td>
                <td class="num" [class.text-danger]="r.overdue > 0" [class.strong]="r.overdue > 0">{{ r.overdue }}</td>
                <td class="num">{{ r.completed }}</td>
                <td class="num" [class.text-danger]="r.blocked > 0">{{ r.blocked }}</td>
                <td>
                  <div class="row ontime">
                    <div class="progress {{ barTone(r.onTimePercent) }}" aria-hidden="true"><span [style.width.%]="clamp(r.onTimePercent)"></span></div>
                    <span class="small num">{{ pct(r.onTimePercent) }}</span>
                  </div>
                </td>
                <td><fu-health [value]="r.health" /></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: `:host { display: block; min-width: 0; } .ontime .progress { width: 72px; } .ontime span { min-width: 36px; text-align: right; }`,
})
export class TeamStatusTableComponent {
  readonly rows = input<TeamStatusRow[]>([]);

  protected clamp(v: number | null | undefined): number {
    return Math.max(0, Math.min(100, v ?? 0));
  }

  protected pct(v: number | null | undefined): string {
    return percent(v);
  }

  /** On-time bar colour carries meaning: ≥ 80 % good, ≥ 60 % watch, below that poor. */
  protected barTone(v: number | null | undefined): string {
    const p = v ?? 0;
    return p >= 80 ? 'good' : p >= 60 ? 'warning' : 'danger';
  }
}
