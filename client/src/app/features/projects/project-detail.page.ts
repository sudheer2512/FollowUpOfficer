import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PlanningApi } from '../../core/api/planning.api';
import { SessionState } from '../../core/auth/session-state';
import { Guid, Tile } from '../../core/models/common.models';
import { CLOSED_STATUSES } from '../../core/models/enums';
import { ProjectDetail, ProjectSummary } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { DelegateLauncher } from '../../core/state/delegate-launcher';
import { loader } from '../../core/util/loader';
import { CommitmentTableComponent } from '../../shared/domain/commitment-table.component';
import { HealthDotComponent, PersonComponent } from '../../shared/domain/indicators';
import { TaskListComponent } from '../../shared/domain/task-list.component';
import { DEFAULT_TASK_COLUMNS, TaskTableComponent } from '../../shared/domain/task-table.component';
import { DateOnlyPipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { TileComponent } from '../../shared/ui/tile.component';
import { ProjectFormComponent } from './project-form.component';
import { ProjectStatusComponent, isTargetMissed, projectProgressClass } from './project-status';

/**
 * /projects/:id — project header, KPI tiles (each drills into the filtered task list), blockers, commitments and the
 * project's tasks (open / all toggle, client-side).
 */
@Component({
  selector: 'fu-project-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    ProjectFormComponent,
    ProjectStatusComponent,
    HealthDotComponent,
    PersonComponent,
    TaskListComponent,
    TaskTableComponent,
    CommitmentTableComponent,
    TileComponent,
    EmptyStateComponent,
    SkeletonComponent,
    IconComponent,
    DateOnlyPipe,
  ],
  templateUrl: './project-detail.page.html',
  styles: `
    .head-meta { display: flex; flex-wrap: wrap; gap: 6px 16px; align-items: center; margin-top: 8px; font-size: 13px; }
    .head-meta .lbl { color: var(--muted); margin-right: 4px; }
    .progress-wide { display: flex; align-items: center; gap: 10px; margin-top: 12px; max-width: 520px; }
    .progress-wide .progress { flex: 1; height: 8px; }
    .desc { color: var(--text-2); max-width: 900px; margin: 10px 0 0; }
  `,
})
export class ProjectDetailPage {
  private readonly api = inject(PlanningApi);
  private readonly session = inject(SessionState);
  private readonly launcher = inject(DelegateLauncher);

  /** Route parameter :id. */
  readonly id = input.required<Guid>();

  protected readonly data = loader<ProjectDetail>();
  protected readonly showAll = signal(false);
  protected readonly editOpen = signal(false);
  protected readonly columns = DEFAULT_TASK_COLUMNS;

  protected readonly summary = computed<ProjectSummary | null>(() => this.data.value()?.summary ?? null);
  protected readonly tasks = computed(() => this.data.value()?.tasks ?? []);
  protected readonly blockers = computed(() => this.data.value()?.blockers ?? []);
  protected readonly commitments = computed(() => this.data.value()?.commitments ?? []);
  protected readonly openTasks = computed(() => this.tasks().filter((t) => !CLOSED_STATUSES.includes(t.status)));
  protected readonly shownTasks = computed(() => (this.showAll() ? this.tasks() : this.openTasks()));

  protected readonly canManage = computed(
    () => this.session.isManager() || this.session.isExecutive() || this.session.isAdministrator(),
  );

  protected readonly percent = computed(() => Math.max(0, Math.min(100, Math.round(this.summary()?.percentComplete ?? 0))));
  protected readonly progressClass = computed(() => projectProgressClass(this.summary()?.status));
  protected readonly targetMissed = computed(() => {
    const s = this.summary();
    return !!s && isTargetMissed(s);
  });

  /** KPI tiles; every tile lands on /tasks filtered to this project (TaskFilter query keys). */
  protected readonly tiles = computed<Tile[]>(() => {
    const s = this.summary();
    if (!s) return [];
    const projectId = s.id;
    const q = (extra: Record<string, string> = {}) => ({ route: '/tasks', query: { scope: 'all', projectId, ...extra } });
    return [
      { key: 'total', label: 'Total tasks', value: s.totalTasks ?? 0, tone: 'neutral', drilldown: q() },
      { key: 'open', label: 'Open', value: s.openTasks ?? 0, tone: 'info', drilldown: q({ open: 'true' }) },
      { key: 'completed', label: 'Completed', value: s.completedTasks ?? 0, tone: 'good', drilldown: q({ statuses: 'Completed' }) },
      { key: 'overdue', label: 'Overdue', value: s.overdueTasks ?? 0, tone: 'danger', drilldown: q({ overdue: 'true' }) },
      { key: 'blocked', label: 'Blocked', value: s.blockedTasks ?? 0, tone: 'orange', drilldown: q({ blocked: 'true' }) },
      {
        key: 'critical',
        label: 'Critical open',
        value: s.criticalTasks ?? 0,
        tone: 'orange',
        drilldown: q({ open: 'true', minCriticality: 'Critical' }),
      },
      {
        key: 'percent',
        label: '% complete',
        value: this.percent(),
        tone: this.percent() >= 100 ? 'good' : 'neutral',
        hint: `${s.completedTasks ?? 0} of ${s.totalTasks ?? 0} done`,
        drilldown: q(),
      },
    ];
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.data.load(this.api.project(id)));
    });
    refreshOn(['task.changed', 'task.created'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    this.data.load(this.api.project(this.id()), { silent });
  }

  protected delegate(): void {
    this.launcher.open({ projectId: this.id() });
  }

  protected onSaved(): void {
    this.editOpen.set(false);
    this.reload(true);
  }
}
