import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { RecurringDefinitionDto } from '../../core/models/view.models';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { clampPercent, onTimeTone } from './report-format';

/**
 * Recurring compliance for the period, worst first: schedule, compliance %, occurrences generated, missed,
 * completed on time / late. The title opens the recurring definition.
 */
@Component({
  selector: 'fu-recurring-compliance-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, EmptyStateComponent],
  template: `
    @if (!rows().length) {
      <fu-empty icon="repeat" title="No recurring activity" message="Recurring tasks due in this period appear here with their compliance." [compact]="true" />
    } @else {
      <div class="table-wrap">
        <table class="table compact">
          <thead>
            <tr>
              <th>Recurring task</th>
              <th>Schedule</th>
              <th>Compliance</th>
              <th class="num">Generated</th>
              <th class="num">Missed</th>
              <th class="num">On time</th>
              <th class="num">Late</th>
            </tr>
          </thead>
          <tbody>
            @for (d of sorted(); track d.id) {
              <tr>
                <td class="title">
                  <a [routerLink]="['/recurring', d.id]" class="strong">{{ d.titleTemplate }}</a>
                  <div class="xsmall muted">
                    <span class="mono">{{ d.code }}</span>
                    @if (d.primaryAssignee?.name || d.team?.name) {
                      · {{ d.primaryAssignee?.name ?? d.team?.name }}
                    }
                  </div>
                </td>
                <td class="small muted rule">{{ d.ruleText }}</td>
                <td>
                  <div class="row compliance">
                    <div class="progress {{ tone(d.compliancePercent) }}" aria-hidden="true"><span [style.width.%]="clamp(d.compliancePercent)"></span></div>
                    <span class="small num">{{ round(d.compliancePercent) }}%</span>
                  </div>
                </td>
                <td class="num">{{ d.generatedCount }}</td>
                <td class="num" [class.text-danger]="d.missedCount > 0" [class.strong]="d.missedCount > 0">{{ d.missedCount }}</td>
                <td class="num">{{ d.completedOnTime }}</td>
                <td class="num" [class.text-warning]="d.completedLate > 0">{{ d.completedLate }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .title { min-width: 220px; max-width: 340px; }
    .rule { max-width: 260px; }
    .compliance .progress { width: 72px; }
    .compliance span { min-width: 36px; text-align: right; }
  `,
})
export class RecurringComplianceTableComponent {
  readonly rows = input<RecurringDefinitionDto[]>([]);

  /** Lowest compliance first — the exceptions the CEO should look at. */
  protected readonly sorted = computed(() => [...(this.rows() ?? [])].sort((a, b) => (a.compliancePercent ?? 0) - (b.compliancePercent ?? 0)));

  protected tone(v: number | null | undefined): string {
    return onTimeTone(v);
  }

  protected clamp(v: number | null | undefined): number {
    return clampPercent(v);
  }

  protected round(v: number | null | undefined): number {
    return Math.round(v ?? 0);
  }
}
