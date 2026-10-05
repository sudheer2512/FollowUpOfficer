import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import { ExecutiveApi } from '../../core/api/executive.api';
import { SessionState } from '../../core/auth/session-state';
import { DateOnly } from '../../core/models/common.models';
import { CommitmentDto, TaskListItem } from '../../core/models/task.models';
import { RecurringInstanceDto, WeeklyReviewDto } from '../../core/models/view.models';
import { addDays, startOfWeek, todayDateOnly } from '../../core/util/dates';
import { loader } from '../../core/util/loader';
import { CommitmentTableComponent } from '../../shared/domain/commitment-table.component';
import { RecurringInstanceTableComponent } from '../../shared/domain/recurring-instance-table.component';
import { TaskTableComponent } from '../../shared/domain/task-table.component';
import { DateTimePipe } from '../../shared/pipes/format.pipes';
import { DialogService } from '../../shared/ui/dialog.service';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ToastService } from '../../shared/ui/toast.service';
import { DigestCardComponent } from './digest-card.component';
import { queuedMessage, resolveWeekStart, weekRangeLabel } from './review-format';
import { SummaryPointsComponent } from './summary-points.component';
import { TeamStatusTableComponent } from './team-status-table.component';

interface SectionView {
  key: string;
  title: string;
  tasks: TaskListItem[];
  commitments: CommitmentDto[];
  instances: RecurringInstanceDto[];
  lines: string[];
  count: number;
  empty: boolean;
}

/**
 * Weekly Executive Review (/review?week=yyyy-MM-dd, executives only): executive summary, review sections,
 * team status, plus the Daily Digest preview. Executives can send the digest or the weekly review now.
 */
@Component({
  selector: 'fu-review-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    IconComponent,
    SkeletonComponent,
    EmptyStateComponent,
    TaskTableComponent,
    CommitmentTableComponent,
    RecurringInstanceTableComponent,
    SummaryPointsComponent,
    TeamStatusTableComponent,
    DigestCardComponent,
    DateTimePipe,
  ],
  templateUrl: './review.page.html',
  styleUrl: './review.page.css',
})
export class ReviewPage {
  private readonly api = inject(ExecutiveApi);
  private readonly router = inject(Router);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  protected readonly session = inject(SessionState);

  /** `?week=` — any date; snapped to its Monday. */
  readonly week = input<string | undefined>();

  protected readonly review = loader<WeeklyReviewDto>();
  protected readonly sending = signal(false);

  protected readonly thisWeek = startOfWeek(todayDateOnly());
  protected readonly weekStart = computed(() => resolveWeekStart(this.week()));
  protected readonly isThisWeek = computed(() => this.weekStart() === this.thisWeek);
  protected readonly heading = computed(() => {
    const r = this.review.value();
    return weekRangeLabel(r?.weekStart || this.weekStart(), r?.weekEnd);
  });
  protected readonly teams = computed(() => this.review.value()?.teams ?? []);
  /** Sections with defaults for lists the (unfinished) server may omit, plus the item count shown in the header. */
  protected readonly sections = computed<SectionView[]>(() =>
    (this.review.value()?.sections ?? []).map((s) => {
      const tasks = s.tasks ?? [];
      const commitments = s.commitments ?? [];
      const instances = s.instances ?? [];
      const lines = s.lines ?? [];
      const count = tasks.length + commitments.length + instances.length;
      return { key: s.key, title: s.title, tasks, commitments, instances, lines, count, empty: count === 0 && !lines.length };
    }),
  );

  constructor() {
    effect(() => {
      const week = this.weekStart();
      untracked(() => {
        // Drop the previous week's data so its content never shows under the new week's heading.
        this.review.set(undefined);
        this.load(week);
      });
    });
  }

  protected load(week: DateOnly = this.weekStart()): void {
    this.review.load(this.api.weeklyReview(week));
  }

  protected shiftWeek(weeks: number): void {
    this.goToWeek(addDays(this.weekStart(), weeks * 7));
  }

  protected pickDate(value: string): void {
    if (value) this.goToWeek(value);
  }

  protected goToWeek(date: DateOnly): void {
    const monday = resolveWeekStart(date);
    void this.router.navigate([], { queryParams: { week: monday === this.thisWeek ? null : monday }, queryParamsHandling: 'merge' });
  }

  protected async sendWeeklyReview(): Promise<void> {
    const week = this.weekStart();
    const ok = await this.dialog.confirm({
      title: 'Send the weekly review?',
      message: `The review for the ${this.heading().replace(/^Week/, 'week')} will be sent to its recipients now.`,
      confirmText: 'Send review',
    });
    if (!ok) return;
    this.sending.set(true);
    this.api.sendWeeklyReview(week).subscribe({
      next: (r) => {
        this.sending.set(false);
        this.toast.success(queuedMessage(r));
      },
      error: () => this.sending.set(false),
    });
  }
}
