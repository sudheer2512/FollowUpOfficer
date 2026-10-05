import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ExecutiveApi } from '../../core/api/executive.api';
import { Guid } from '../../core/models/common.models';
import { ReportDto, ReportRequest } from '../../core/models/view.models';
import { LookupsStore } from '../../core/state/lookups.store';
import { ThemeService } from '../../core/state/theme.service';
import { loader } from '../../core/util/loader';
import { CommitmentTableComponent } from '../../shared/domain/commitment-table.component';
import { TaskColumn, TaskTableComponent } from '../../shared/domain/task-table.component';
import { LookupSelectComponent } from '../../shared/forms/lookup-select.component';
import { TeamPickerComponent } from '../../shared/forms/team-picker.component';
import { ChartComponent } from '../../shared/ui/chart.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { TabItem, TabsComponent } from '../../shared/ui/tabs.component';
import { PerformanceTableComponent } from './performance-table.component';
import { RecurringComplianceTableComponent } from './recurring-compliance-table.component';
import {
  ageingOptions,
  chartColors,
  escalationLevelOptions,
  escalationTriggerOptions,
  hasCounts,
  hasTrend,
  onTimeByDepartmentOptions,
  trendOptions,
} from './report-charts';
import { formatMetric, periodLabel } from './report-format';
import { PERIOD_PRESETS, isPreset, resolvePeriod } from './report-period';

type FilterParams = Partial<Record<'preset' | 'from' | 'to' | 'departmentId' | 'teamId', string | null>>;

const NO_RESPONSE_COLUMNS: TaskColumn[] = ['number', 'title', 'responsible', 'due', 'updated'];

/**
 * Executive reports (/reports, executives only): period presets (FY from 1 April) or a custom range,
 * department / team filters (all synced to the query string), metric tiles, five charts and performance tables.
 */
@Component({
  selector: 'fu-reports-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    RouterLink,
    IconComponent,
    SkeletonComponent,
    EmptyStateComponent,
    ChartComponent,
    TabsComponent,
    LookupSelectComponent,
    TeamPickerComponent,
    TaskTableComponent,
    CommitmentTableComponent,
    PerformanceTableComponent,
    RecurringComplianceTableComponent,
  ],
  templateUrl: './reports.page.html',
  styleUrl: './reports.page.css',
})
export class ReportsPage {
  private readonly api = inject(ExecutiveApi);
  private readonly router = inject(Router);
  private readonly lookups = inject(LookupsStore);
  private readonly theme = inject(ThemeService);

  // Query parameters (withComponentInputBinding).
  readonly preset = input<string | undefined>();
  readonly from = input<string | undefined>();
  readonly to = input<string | undefined>();
  readonly departmentId = input<Guid | undefined>();
  readonly teamId = input<Guid | undefined>();

  protected readonly presets = PERIOD_PRESETS;
  protected readonly noResponseColumns = NO_RESPONSE_COLUMNS;
  protected readonly report = loader<ReportDto>();
  protected readonly tab = signal('departments');

  protected readonly period = computed(() => resolvePeriod({ preset: this.preset(), from: this.from(), to: this.to() }));
  protected readonly request = computed<ReportRequest>(() => {
    const p = this.period();
    return { from: p.from, to: p.to, departmentId: this.departmentId() || null, teamId: this.teamId() || null };
  });

  protected readonly hasFilters = computed(() => !!(this.preset() || this.from() || this.to() || this.departmentId() || this.teamId()));

  protected readonly subtitle = computed(() => {
    const r = this.report.value();
    const p = this.period();
    const range = periodLabel(r?.from || p.from, r?.to || p.to);
    const dep = this.lookups.departments().find((d) => d.id === this.departmentId())?.name ?? 'All departments';
    const team = this.lookups.team(this.teamId())?.name ?? 'All teams';
    return `${range} · ${dep} · ${team}`;
  });

  protected readonly metrics = computed(() =>
    (this.report.value()?.metrics ?? []).map((m) => ({ ...m, display: formatMetric(m), link: m.drilldown?.route ? m.drilldown : null })),
  );

  /** Chart options are rebuilt when the data or the theme changes (semantic colours come from CSS tokens). */
  private readonly colors = computed(() => {
    this.theme.effective();
    return chartColors();
  });
  protected readonly trendChart = computed(() => {
    const t = this.report.value()?.trend ?? [];
    return hasTrend(t) ? trendOptions(t, this.colors()) : null;
  });
  protected readonly ageingChart = computed(() => {
    const b = this.report.value()?.overdueAgeing ?? [];
    return hasCounts(b) ? ageingOptions(b, this.colors()) : null;
  });
  protected readonly levelChart = computed(() => {
    const l = this.report.value()?.escalationsByLevel ?? [];
    return hasCounts(l) ? escalationLevelOptions(l, this.colors()) : null;
  });
  protected readonly triggerChart = computed(() => {
    const t = this.report.value()?.escalationsByTrigger ?? [];
    return hasCounts(t) ? escalationTriggerOptions(t, this.colors()) : null;
  });
  protected readonly onTimeChart = computed(() => {
    const d = (this.report.value()?.departments ?? []).filter((x) => (x.completed ?? 0) > 0);
    return d.length ? onTimeByDepartmentOptions(d, this.colors()) : null;
  });

  /** Table data with defaults for lists the (unfinished) server may omit. */
  protected readonly tables = computed(() => {
    const r = this.report.value();
    return {
      departments: r?.departments ?? [],
      teams: r?.teams ?? [],
      people: r?.people ?? [],
      recurring: r?.recurringCompliance ?? [],
      missed: r?.missedCommitments ?? [],
      noResponse: r?.noResponseTasks ?? [],
    };
  });

  protected readonly tabs = computed<TabItem[]>(() => {
    const t = this.tables();
    return [
      { key: 'departments', label: 'Departments', count: t.departments.length },
      { key: 'teams', label: 'Teams', count: t.teams.length },
      { key: 'people', label: 'People', count: t.people.length },
      { key: 'recurring', label: 'Recurring', count: t.recurring.length },
      { key: 'missed', label: 'Missed commitments', count: t.missed.length, tone: t.missed.length ? 'danger' : undefined },
      { key: 'no-response', label: 'No response', count: t.noResponse.length, tone: t.noResponse.length ? 'warning' : undefined },
    ];
  });

  constructor() {
    this.lookups.load();
    effect(() => {
      const req = this.request();
      untracked(() => this.load(req));
    });
  }

  protected load(req: ReportRequest = this.request()): void {
    this.report.load(this.api.reports(req));
  }

  protected setPreset(value: string): void {
    if (!isPreset(value)) return;
    if (value === 'custom') {
      const p = this.period();
      this.navigate({ preset: 'custom', from: p.from, to: p.to });
    } else {
      this.navigate({ preset: value, from: null, to: null });
    }
  }

  protected setDate(bound: 'from' | 'to', value: string): void {
    if (!value) return;
    const p = this.period();
    this.navigate({ preset: 'custom', from: bound === 'from' ? value : p.from, to: bound === 'to' ? value : p.to });
  }

  protected setDepartment(id: Guid | null): void {
    // A team filter from another department would produce an empty report: clear it.
    this.navigate({ departmentId: id || null, teamId: null });
  }

  protected setTeam(id: Guid | null): void {
    this.navigate({ teamId: id || null });
  }

  protected clearFilters(): void {
    this.navigate({ preset: null, from: null, to: null, departmentId: null, teamId: null });
  }

  /** Overdue-ageing bar click → that bucket's task list. */
  protected openAgeing(index: number): void {
    const bucket = this.report.value()?.overdueAgeing?.[index];
    if (bucket?.drilldown?.route) void this.router.navigate([bucket.drilldown.route], { queryParams: bucket.drilldown.query });
  }

  private navigate(params: FilterParams): void {
    void this.router.navigate([], { queryParams: params, queryParamsHandling: 'merge', replaceUrl: true });
  }
}
