import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Drilldown } from '../../core/models/common.models';
import { GroupPerformance } from '../../core/models/view.models';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { clampPercent, onTimeTone, oneDecimal } from './report-format';

export type PerformanceKind = 'department' | 'team' | 'person';

type SortKey =
  | 'name' | 'assigned' | 'completed' | 'onTimePercent' | 'avgCompletionDays' | 'avgDelayDays' | 'overdue' | 'escalations'
  | 'commitmentsMissed' | 'reopened';

interface Column {
  key: SortKey;
  label: string;
  num: boolean;
}

const COLUMNS: Column[] = [
  { key: 'assigned', label: 'Assigned', num: true },
  { key: 'completed', label: 'Completed', num: true },
  { key: 'onTimePercent', label: 'On time', num: false },
  { key: 'avgCompletionDays', label: 'Avg completion', num: true },
  { key: 'avgDelayDays', label: 'Avg delay', num: true },
  { key: 'overdue', label: 'Overdue', num: true },
  { key: 'escalations', label: 'Escalations', num: true },
  { key: 'commitmentsMissed', label: 'Commitments met / missed', num: true },
  { key: 'reopened', label: 'Reopened', num: true },
];

const NAME_LABEL: Record<PerformanceKind, string> = { department: 'Department', team: 'Team', person: 'Person' };

/**
 * Department / team / people performance with client-side sorting (click a header; click again to reverse).
 * The name opens the matching task list (departments, people) or team dashboard.
 *   <fu-performance-table [rows]="report.teams" kind="team" />
 */
@Component({
  selector: 'fu-performance-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, EmptyStateComponent],
  template: `
    @if (!rows().length) {
      <fu-empty icon="chart" [title]="'No ' + nameLabel().toLowerCase() + ' data'" message="Nothing was assigned in this period for the selected filters." [compact]="true" />
    } @else {
      <div class="table-wrap">
        <table class="table compact">
          <thead>
            <tr>
              <th [attr.aria-sort]="ariaSort('name')">
                <button type="button" class="sort" (click)="sortBy('name')">
                  {{ nameLabel() }}
                  @if (sort().key === 'name') {
                    <fu-icon [name]="sort().dir === 'asc' ? 'arrow-up' : 'arrow-down'" [size]="11" />
                  }
                </button>
              </th>
              @for (c of columns; track c.key) {
                <th [class.num]="c.num" [attr.aria-sort]="ariaSort(c.key)">
                  <button type="button" class="sort" (click)="sortBy(c.key)">
                    {{ c.label }}
                    @if (sort().key === c.key) {
                      <fu-icon [name]="sort().dir === 'asc' ? 'arrow-up' : 'arrow-down'" [size]="11" />
                    }
                  </button>
                </th>
              }
            </tr>
          </thead>
          <tbody>
            @for (v of sorted(); track v.row.id) {
              @let r = v.row;
              <tr>
                <td class="name">
                  @if (r.id) {
                    <a [routerLink]="v.link.route" [queryParams]="v.link.query" class="strong">{{ r.name }}</a>
                  } @else {
                    <span class="strong">{{ r.name }}</span>
                  }
                </td>
                <td class="num">{{ r.assigned }}</td>
                <td class="num">{{ r.completed }}</td>
                <td>
                  <div class="row ontime" [attr.title]="r.completedOnTime + ' of ' + r.completed + ' completed on time'">
                    <span class="small num cnt">{{ r.completedOnTime }}</span>
                    <div class="progress {{ tone(r.onTimePercent) }}" aria-hidden="true"><span [style.width.%]="clamp(r.onTimePercent)"></span></div>
                    <span class="small num pct">{{ r.completed ? round(r.onTimePercent) + '%' : '—' }}</span>
                  </div>
                </td>
                <td class="num">{{ r.completed ? days(r.avgCompletionDays) : '—' }}</td>
                <td class="num" [class.text-warning]="r.avgDelayDays > 0">{{ r.completed ? days(r.avgDelayDays) : '—' }}</td>
                <td class="num" [class.text-danger]="r.overdue > 0" [class.strong]="r.overdue > 0">{{ r.overdue }}</td>
                <td class="num" [class.text-orange]="r.escalations > 0">{{ r.escalations }}</td>
                <td class="num nowrap">
                  <span class="text-good">{{ r.commitmentsMet }}</span>
                  <span class="subtle"> / </span>
                  <span [class.text-danger]="r.commitmentsMissed > 0" [class.strong]="r.commitmentsMissed > 0">{{ r.commitmentsMissed }}</span>
                </td>
                <td class="num">{{ r.reopened }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .sort { all: unset; cursor: pointer; display: inline-flex; align-items: center; gap: 3px; white-space: nowrap; }
    .sort:hover { color: var(--text); }
    .sort:focus-visible { box-shadow: var(--focus); border-radius: 3px; }
    .name { min-width: 160px; }
    .ontime { gap: 6px; }
    .ontime .progress { width: 64px; }
    .ontime .cnt { min-width: 22px; text-align: right; color: var(--muted); }
    .ontime .pct { min-width: 34px; text-align: right; }
  `,
})
export class PerformanceTableComponent {
  readonly rows = input<GroupPerformance[]>([]);
  readonly kind = input<PerformanceKind>('department');

  protected readonly columns = COLUMNS;
  protected readonly nameLabel = computed(() => NAME_LABEL[this.kind()]);
  /** null key = the server's order. */
  protected readonly sort = signal<{ key: SortKey | null; dir: 'asc' | 'desc' }>({ key: null, dir: 'desc' });

  /** Rows in display order, each with its drill-down link (built once per change, not on every check). */
  protected readonly sorted = computed(() => {
    const { key, dir } = this.sort();
    const list = [...(this.rows() ?? [])];
    if (key) {
      const sign = dir === 'asc' ? 1 : -1;
      list.sort((a, b) => (key === 'name' ? sign * (a.name ?? '').localeCompare(b.name ?? '') : sign * ((a[key] ?? 0) - (b[key] ?? 0))));
    }
    return list.map((row) => ({ row, link: this.link(row) }));
  });

  /** First click sorts names A→Z and numbers high→low; a second click on the same header reverses. */
  protected sortBy(key: SortKey): void {
    const cur = this.sort();
    if (cur.key === key) this.sort.set({ key, dir: cur.dir === 'asc' ? 'desc' : 'asc' });
    else this.sort.set({ key, dir: key === 'name' ? 'asc' : 'desc' });
  }

  protected ariaSort(key: SortKey): 'ascending' | 'descending' | null {
    const s = this.sort();
    return s.key === key ? (s.dir === 'asc' ? 'ascending' : 'descending') : null;
  }

  private link(r: GroupPerformance): Drilldown {
    switch (this.kind()) {
      case 'team':
        return { route: `/team/${r.id}`, query: {} };
      case 'person':
        return { route: '/tasks', query: { assignedToId: r.id, open: 'true' } };
      default:
        return { route: '/tasks', query: { departmentId: r.id, open: 'true' } };
    }
  }

  protected tone(v: number | null | undefined): string {
    return onTimeTone(v);
  }

  protected clamp(v: number | null | undefined): number {
    return clampPercent(v);
  }

  protected round(v: number | null | undefined): number {
    return Math.round(v ?? 0);
  }

  protected days(v: number | null | undefined): string {
    return `${oneDecimal(v)} d`;
  }
}
