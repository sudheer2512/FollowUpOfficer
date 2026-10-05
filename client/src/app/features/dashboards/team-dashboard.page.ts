import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import type { EChartsCoreOption } from 'echarts/core';
import { ExecutiveApi } from '../../core/api/executive.api';
import { NamedRef } from '../../core/models/common.models';
import { TaskListItem } from '../../core/models/task.models';
import { MemberLoad, RecurringDefinitionDto, TeamDashboard } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { ThemeService } from '../../core/state/theme.service';
import { loader } from '../../core/util/loader';
import { PersonComponent } from '../../shared/domain/indicators';
import { TaskListComponent } from '../../shared/domain/task-list.component';
import { DateTimePipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { ChartComponent, chartPalette } from '../../shared/ui/chart.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { TileComponent } from '../../shared/ui/tile.component';

interface TaskCard {
  key: string;
  title: string;
  tasks: TaskListItem[];
  detail: string | null;
  showStatus: boolean;
  empty: string;
  /** Task-list query for "View all" (teamId is added). */
  query: Record<string, string>;
  tone: 'danger' | 'warning' | '';
}

interface MemberTotals {
  open: number;
  dueThisWeek: number;
  overdue: number;
  critical: number;
  blocked: number;
  noRecentUpdate: number;
  completed: number;
}

const SERIES_OVERDUE = 'Overdue';
const SERIES_DUE_WEEK = 'Due this week';
const SERIES_OTHER = 'Other open';

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] ?? c);
}

/**
 * Team dashboard (`/team`, `/team/:id`, spec §40): team picker, tiles, member load table and workload chart,
 * attention / overdue / blocked / no-recent-update lists and the team's recurring tasks.
 * `/team` without an id redirects to the first team the user leads or manages.
 */
@Component({
  selector: 'fu-team-dashboard-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    IconComponent,
    SkeletonComponent,
    EmptyStateComponent,
    TileComponent,
    PersonComponent,
    TaskListComponent,
    ChartComponent,
    DateTimePipe,
    DecimalPipe,
    HumanizePipe,
  ],
  templateUrl: './team-dashboard.page.html',
  styleUrl: './team-dashboard.page.css',
})
export class TeamDashboardPage {
  private readonly api = inject(ExecutiveApi);
  private readonly router = inject(Router);
  private readonly theme = inject(ThemeService);

  /** Route param `:id` (absent on `/team`). */
  readonly id = input<string>();

  protected readonly teams = loader<NamedRef[]>();
  protected readonly dash = loader<TeamDashboard>();

  /** Teams for the picker; the current team is kept even when it is not in the list (direct link). */
  protected readonly teamOptions = computed<NamedRef[]>(() => {
    const list = this.teams.value() ?? [];
    const current = this.dash.value()?.team;
    return current && !list.some((t) => t.id === current.id) ? [current, ...list] : list;
  });

  protected readonly noTeams = computed(() => !this.id() && !this.teams.loading() && this.teams.value()?.length === 0);

  /** Dashboard data for the selected team (hidden while another team's data is still shown). */
  protected readonly data = computed(() => {
    const d = this.dash.value();
    const id = this.id();
    return d && (!id || !d.team?.id || d.team.id === id) ? d : null;
  });

  protected readonly tiles = computed(() => this.data()?.tiles ?? []);
  /** Members with a guaranteed person ref (the unfinished backend may omit it). */
  protected readonly members = computed<MemberLoad[]>(() =>
    (this.data()?.members ?? []).map((m) => ({ ...m, person: m.person ?? { id: '', name: 'Unknown' } })),
  );
  protected readonly recurring = computed<RecurringDefinitionDto[]>(() => this.data()?.recurring ?? []);

  protected readonly totals = computed<MemberTotals>(() =>
    this.members().reduce<MemberTotals>(
      (t, m) => ({
        open: t.open + (m.open ?? 0),
        dueThisWeek: t.dueThisWeek + (m.dueThisWeek ?? 0),
        overdue: t.overdue + (m.overdue ?? 0),
        critical: t.critical + (m.critical ?? 0),
        blocked: t.blocked + (m.blocked ?? 0),
        noRecentUpdate: t.noRecentUpdate + (m.noRecentUpdate ?? 0),
        completed: t.completed + (m.completed ?? 0),
      }),
      { open: 0, dueThisWeek: 0, overdue: 0, critical: 0, blocked: 0, noRecentUpdate: 0, completed: 0 },
    ),
  );

  protected readonly taskCards = computed<TaskCard[]>(() => {
    const d = this.data();
    if (!d) return [];
    const cards: TaskCard[] = [
      { key: 'attention', title: 'Needs attention', tasks: d.attentionTasks ?? [], detail: null, showStatus: true, empty: 'Nothing in this team needs attention.', query: { open: 'true', sort: 'attention' }, tone: '' },
      { key: 'overdue', title: 'Overdue', tasks: d.overdue ?? [], detail: null, showStatus: false, empty: 'Nothing overdue.', query: { overdue: 'true' }, tone: 'danger' },
      { key: 'blocked', title: 'Blocked', tasks: d.blocked ?? [], detail: 'blocked', showStatus: false, empty: 'Nothing is blocked.', query: { blocked: 'true' }, tone: 'danger' },
      { key: 'no-update', title: 'No recent update', tasks: d.noRecentUpdate ?? [], detail: 'noResponse', showStatus: false, empty: 'Everyone has posted an update recently.', query: { noResponse: 'true' }, tone: 'warning' },
    ];
    return cards;
  });

  protected readonly chartHeight = computed(() => `${Math.max(1, this.members().length) * 40 + 60}px`);

  /** Horizontal stacked bars per member: overdue · due this week · other open; blocked and critical in the tooltip. */
  protected readonly chartOptions = computed<EChartsCoreOption>(() => {
    this.theme.effective(); // re-read the colour tokens when the theme changes
    const members = this.members();
    const p = chartPalette();
    const [accent, , danger, warning] = p.series;
    const bar = { type: 'bar', stack: 'load', barMaxWidth: 22, emphasis: { focus: 'series' } };
    return {
      grid: { left: 8, right: 24, top: 32, bottom: 8, containLabel: true },
      tooltip: {
        trigger: 'axis',
        confine: true,
        axisPointer: { type: 'shadow' },
        formatter: (params: unknown) => this.tooltip(params),
      },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: { type: 'category', inverse: true, axisTick: { show: false }, data: members.map((m) => m.person?.name ?? 'Unknown') },
      series: [
        { ...bar, name: SERIES_OVERDUE, itemStyle: { color: danger }, data: members.map((m) => m.overdue ?? 0) },
        { ...bar, name: SERIES_DUE_WEEK, itemStyle: { color: warning }, data: members.map((m) => m.dueThisWeek ?? 0) },
        {
          ...bar,
          name: SERIES_OTHER,
          itemStyle: { color: accent },
          data: members.map((m) => Math.max(0, (m.open ?? 0) - (m.overdue ?? 0) - (m.dueThisWeek ?? 0))),
        },
      ],
    };
  });

  constructor() {
    this.teams.load(this.api.dashboardTeams());

    // /team → /team/<first team>
    effect(() => {
      const list = this.teams.value();
      if (this.id() || !list?.length) return;
      untracked(() => void this.router.navigate(['/team', list[0].id], { replaceUrl: true }));
    });

    effect(() => {
      const id = this.id();
      if (id) untracked(() => this.dash.load(this.api.teamDashboard(id)));
    });

    refreshOn(['task.changed', 'task.created'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    const id = this.id();
    if (id) this.dash.load(this.api.teamDashboard(id), { silent });
    else this.teams.load(this.api.dashboardTeams(), { silent });
  }

  protected selectTeam(e: Event): void {
    const id = (e.target as HTMLSelectElement).value;
    if (id && id !== this.id()) void this.router.navigate(['/team', id]);
  }

  protected viewAll(card: TaskCard): Record<string, string> {
    const teamId = this.id() ?? this.data()?.team?.id;
    return teamId ? { ...card.query, teamId } : card.query;
  }

  protected memberQuery(m: MemberLoad): Record<string, string> {
    return { assignedToId: m.person?.id ?? '', open: 'true' };
  }

  /** Clicking a bar opens that member's tasks, filtered to the clicked segment. */
  protected onChartClick(e: { seriesName?: string; dataIndex: number }): void {
    const m = this.members()[e.dataIndex];
    if (!m?.person?.id) return;
    const query: Record<string, string> = this.memberQuery(m);
    if (e.seriesName === SERIES_OVERDUE) query['overdue'] = 'true';
    else if (e.seriesName === SERIES_DUE_WEEK) query['bucket'] = 'DueThisWeek';
    void this.router.navigate(['/tasks'], { queryParams: query });
  }

  protected stateTone(state: string): string {
    return state === 'Active' ? 'good' : state === 'Paused' ? 'warning' : 'neutral';
  }

  protected complianceTone(pct: number): string {
    return pct >= 90 ? 'good' : pct >= 70 ? 'warning' : 'danger';
  }

  private tooltip(params: unknown): string {
    const first = (Array.isArray(params) ? params[0] : params) as { dataIndex?: number } | undefined;
    const m = this.members()[first?.dataIndex ?? -1];
    if (!m) return '';
    const line = (label: string, value: number) => `${label}: <b>${value ?? 0}</b>`;
    return [
      `<b>${escapeHtml(m.person?.name ?? 'Unknown')}</b>`,
      line('Open', m.open),
      line(SERIES_OVERDUE, m.overdue),
      line(SERIES_DUE_WEEK, m.dueThisWeek),
      line('Critical', m.critical),
      line('Blocked', m.blocked),
      line('No recent update', m.noRecentUpdate),
    ].join('<br/>');
  }
}
