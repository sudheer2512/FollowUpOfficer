import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { PlanningApi } from '../../core/api/planning.api';
import { SessionState } from '../../core/auth/session-state';
import { refreshOn } from '../../core/realtime/realtime.service';
import { ProjectSummary } from '../../core/models/view.models';
import { loader } from '../../core/util/loader';
import { DeadlineTextComponent, HealthDotComponent, PersonComponent } from '../../shared/domain/indicators';
import { DateOnlyPipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ProjectFormComponent } from './project-form.component';
import {
  ProjectStatusComponent,
  ProjectStatusKey,
  isTargetMissed,
  projectProgressClass,
  projectStatusKey,
} from './project-status';

interface StatusFilter {
  key: ProjectStatusKey | 'all';
  label: string;
  tone: string;
}

const STATUS_FILTERS: StatusFilter[] = [
  { key: 'all', label: 'All', tone: '' },
  { key: 'offtrack', label: 'Off track', tone: 'danger' },
  { key: 'atrisk', label: 'At risk', tone: 'warning' },
  { key: 'ontrack', label: 'On track', tone: '' },
  { key: 'completed', label: 'Completed', tone: 'accent' },
];

/** Severity order used to sort cards: the projects that need the CEO come first. */
const STATUS_RANK: Record<ProjectStatusKey, number> = { offtrack: 0, atrisk: 1, ontrack: 2, other: 3, completed: 4 };

/**
 * /projects — one card per initiative with its derived status, progress and task counts.
 * Cards are sorted by severity (Off Track first), then by target date.
 */
@Component({
  selector: 'fu-projects-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    ProjectFormComponent,
    ProjectStatusComponent,
    HealthDotComponent,
    PersonComponent,
    DeadlineTextComponent,
    EmptyStateComponent,
    SkeletonComponent,
    IconComponent,
    DateOnlyPipe,
  ],
  templateUrl: './projects.page.html',
  styleUrl: './projects.page.css',
})
export class ProjectsPage {
  private readonly api = inject(PlanningApi);
  private readonly router = inject(Router);
  private readonly session = inject(SessionState);

  protected readonly data = loader<ProjectSummary[]>();
  protected readonly query = signal('');
  protected readonly showInactive = signal(false);
  protected readonly statusFilter = signal<ProjectStatusKey | 'all'>('all');
  protected readonly formOpen = signal(false);
  protected readonly statusFilters = STATUS_FILTERS;

  protected readonly canManage = computed(
    () => this.session.isManager() || this.session.isExecutive() || this.session.isAdministrator(),
  );

  /** Projects after the active/inactive switch and text search (status filter not applied — used for counts). */
  private readonly searched = computed(() => {
    const q = this.query().trim().toLowerCase();
    const inactive = this.showInactive();
    return (this.data.value() ?? [])
      .filter((p) => inactive || p.isActive !== false)
      .filter(
        (p) =>
          !q ||
          [p.name, p.code, p.description, p.owner?.name, p.department?.name].some((s) => (s ?? '').toLowerCase().includes(q)),
      );
  });

  protected readonly statusCounts = computed(() => {
    const counts: Record<string, number> = { all: 0, ontrack: 0, atrisk: 0, offtrack: 0, completed: 0, other: 0 };
    for (const p of this.searched()) {
      counts['all']++;
      counts[projectStatusKey(p.status)]++;
    }
    return counts;
  });

  protected readonly visible = computed(() => {
    const f = this.statusFilter();
    return this.searched()
      .filter((p) => f === 'all' || projectStatusKey(p.status) === f)
      .slice()
      .sort((a, b) => {
        const rank = STATUS_RANK[projectStatusKey(a.status)] - STATUS_RANK[projectStatusKey(b.status)];
        if (rank !== 0) return rank;
        return (a.targetDate ?? '9999-12-31').localeCompare(b.targetDate ?? '9999-12-31');
      });
  });

  protected readonly hasAny = computed(() => (this.data.value() ?? []).length > 0);

  constructor() {
    this.reload();
    refreshOn(['task.changed', 'task.created'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    this.data.load(this.api.projects(), { silent });
  }

  protected statusClass(p: ProjectSummary): string {
    return projectProgressClass(p.status);
  }

  protected targetMissed(p: ProjectSummary): boolean {
    return isTargetMissed(p);
  }

  protected percent(p: ProjectSummary): number {
    return Math.max(0, Math.min(100, Math.round(p.percentComplete ?? 0)));
  }

  protected clearFilters(): void {
    this.query.set('');
    this.statusFilter.set('all');
  }

  protected onSaved(p: ProjectSummary): void {
    this.formOpen.set(false);
    if (p?.id) void this.router.navigate(['/projects', p.id]);
    else this.reload(true);
  }
}
