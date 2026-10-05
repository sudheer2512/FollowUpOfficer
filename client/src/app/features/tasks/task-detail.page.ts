import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable, map } from 'rxjs';
import { TasksApi } from '../../core/api/tasks.api';
import { SessionState } from '../../core/auth/session-state';
import { AuditEntryDto } from '../../core/models/admin.models';
import { TaskDetail } from '../../core/models/task.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { DelegateLauncher } from '../../core/state/delegate-launcher';
import { loader } from '../../core/util/loader';
import { DialogService } from '../../shared/ui/dialog.service';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { TabItem, TabsComponent } from '../../shared/ui/tabs.component';
import { ToastService } from '../../shared/ui/toast.service';
import { AuditTabComponent } from './detail/audit-tab.component';
import { CommunicationsTabComponent } from './detail/communications-tab.component';
import { CompletionTabComponent } from './detail/completion-tab.component';
import { TaskDialogsComponent } from './detail/dialogs/task-dialogs.component';
import { OverviewTabComponent } from './detail/overview-tab.component';
import { QuickActionsComponent } from './detail/quick-actions.component';
import { SubtasksTabComponent } from './detail/subtasks-tab.component';
import { DialogRequest, QuickAction } from './detail/task-actions';
import { TaskBannersComponent } from './detail/task-banners.component';
import { countSubtasks, isFollowing, needsMyApproval, normaliseDetail } from './detail/task-detail.util';
import { TaskHeaderComponent } from './detail/task-header.component';
import { TimelineTabComponent } from './detail/timeline-tab.component';

const TAB_KEYS = ['overview', 'subtasks', 'timeline', 'communications', 'completion', 'audit'] as const;
type TabKey = (typeof TAB_KEYS)[number];

function toTabKey(value: string | null | undefined): TabKey {
  return (TAB_KEYS as readonly string[]).includes(value ?? '') ? (value as TabKey) : 'overview';
}

/**
 * /tasks/:id — task detail. Loads the TaskDetail, hands it to the header, quick actions, banners and tabs, and applies
 * the TaskDetail every command returns (no refetch). Real-time `task.changed` / `communication.imported` events for this
 * task refresh silently. The active tab lives in the `tab` query parameter so links can open a specific section.
 */
@Component({
  selector: 'fu-task-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    IconComponent,
    SkeletonComponent,
    TabsComponent,
    TaskHeaderComponent,
    QuickActionsComponent,
    TaskBannersComponent,
    OverviewTabComponent,
    SubtasksTabComponent,
    TimelineTabComponent,
    CommunicationsTabComponent,
    CompletionTabComponent,
    AuditTabComponent,
    TaskDialogsComponent,
  ],
  templateUrl: './task-detail.page.html',
  styleUrl: './task-detail.page.css',
})
export class TaskDetailPage {
  private readonly api = inject(TasksApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly session = inject(SessionState);
  private readonly toast = inject(ToastService);
  private readonly dialogService = inject(DialogService);
  private readonly delegate = inject(DelegateLauncher);
  private readonly pageTitle = inject(Title);

  /** Route parameter :id. */
  readonly id = input.required<string>();
  /** Query parameter ?tab=… */
  readonly tab = input<string>();

  protected readonly data = loader<TaskDetail>();
  protected readonly audit = loader<AuditEntryDto[]>();
  /** Task id the audit trail was loaded for; reset whenever the task changes so the trail is refetched. */
  private auditLoadedFor: string | null = null;
  /** True while the global delegate dialog is open for a subtask of this task. */
  private subtaskDialogOpen = false;

  protected readonly dialog = signal<DialogRequest | null>(null);
  protected readonly busy = signal(false);
  protected readonly activeTab = linkedSignal<TabKey>(() => toTabKey(this.tab()));

  protected readonly following = computed(() => {
    const d = this.data.value();
    return !!d && isFollowing(d, this.session.employeeId());
  });

  protected readonly tabs = computed<TabItem[]>(() => {
    const d = this.data.value();
    if (!d) return [];
    const suggested = d.messages.filter((m) => m.status === 'Suggested').length;
    return [
      { key: 'overview', label: 'Overview' },
      { key: 'subtasks', label: 'Subtasks', count: countSubtasks(d.subtasks) },
      { key: 'timeline', label: 'Timeline', count: d.timeline.length },
      { key: 'communications', label: 'Communications', count: d.messages.filter((m) => m.status !== 'Rejected').length, tone: suggested ? 'accent' : undefined },
      { key: 'completion', label: 'Completion', count: d.submissions.length, tone: needsMyApproval(d) ? 'warning' : undefined },
      { key: 'audit', label: 'Audit' },
    ];
  });
  protected readonly activeTabLabel = computed(() => this.tabs().find((t) => t.key === this.activeTab())?.label ?? '');

  constructor() {
    // (Re)load whenever the route id changes; the page instance is reused between tasks.
    effect(() => {
      this.id();
      untracked(() => {
        this.dialog.set(null);
        this.data.set(undefined);
        this.audit.set(undefined);
        this.auditLoadedFor = null;
        this.load(false);
      });
    });

    // The audit trail loads lazily, the first time its tab is shown (and again after the task changes).
    effect(() => {
      if (this.activeTab() === 'audit' && this.data.value()) untracked(() => this.ensureAudit());
    });

    effect(() => {
      const d = this.data.value();
      if (d) this.pageTitle.setTitle(`${d.summary.number} · ${d.summary.title} · FollowUp Officer`);
    });

    // Refresh once the global delegate dialog, opened for a subtask of this task, closes.
    effect(() => {
      const request = this.delegate.request();
      untracked(() => {
        if (request?.parentTaskId && request.parentTaskId === this.id()) {
          this.subtaskDialogOpen = true;
        } else if (!request && this.subtaskDialogOpen) {
          this.subtaskDialogOpen = false;
          this.load(true);
        }
      });
    });

    refreshOn(['task.changed', 'communication.imported'], () => this.load(true), { taskId: () => this.id() });
  }

  protected load(silent: boolean): void {
    this.data.load(this.api.get(this.id()).pipe(map(normaliseDetail)), { silent });
  }

  /** Applies the TaskDetail a command returned. Results for another task (a stale response after navigation) are ignored. */
  protected applyUpdate(updated: TaskDetail): void {
    if (!updated?.summary || updated.summary.id !== this.id()) return;
    this.data.set(normaliseDetail(updated));
    this.auditLoadedFor = null;
  }

  protected selectTab(key: string): void {
    const tab = toTabKey(key);
    this.activeTab.set(tab);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: tab === 'overview' ? null : tab },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected retryAudit(): void {
    this.auditLoadedFor = null;
    this.ensureAudit();
  }

  private ensureAudit(): void {
    const id = this.id();
    if (this.auditLoadedFor === id) return;
    this.auditLoadedFor = id;
    this.audit.load(this.api.audit(id).pipe(map((list) => list ?? [])), { silent: !!this.audit.value() });
  }

  protected onAction(action: QuickAction): void {
    if (action.dialog) {
      this.dialog.set(action.dialog);
      return;
    }
    switch (action.key) {
      case 'subtask':
        this.openSubtaskDialog();
        break;
      case 'resume':
        this.resumeFollowUp();
        break;
      case 'reopen':
        void this.reopen();
        break;
    }
  }

  protected openSubtaskDialog(): void {
    const d = this.data.value();
    if (d) this.delegate.open({ parentTaskId: d.summary.id, parentNumber: d.summary.number });
  }

  protected acceptTask(): void {
    this.mutate(this.api.acknowledge(this.id(), { response: 'Accept' }), 'Task accepted');
  }

  protected resumeFollowUp(): void {
    this.mutate(this.api.resumeFollowUp(this.id()), 'Follow-up resumed');
  }

  private async reopen(): Promise<void> {
    const reason = await this.dialogService.prompt({
      title: 'Reopen task',
      message: 'The assignee is notified and follow-up starts again.',
      label: 'Reason',
      multiline: true,
      required: true,
      confirmText: 'Reopen',
    });
    if (reason?.trim()) this.mutate(this.api.reopen(this.id(), reason.trim()), 'Task reopened');
  }

  protected toggleFollow(): void {
    const me = this.session.employeeId();
    const d = this.data.value();
    if (!me || !d || this.busy()) return;
    const follow = !isFollowing(d, me);
    this.busy.set(true);
    (follow ? this.api.follow(d.summary.id, me) : this.api.unfollow(d.summary.id, me)).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success(follow ? `You're following ${d.summary.number}` : `You've stopped following ${d.summary.number}`);
        this.load(true);
      },
      error: () => this.busy.set(false),
    });
  }

  private mutate(command: Observable<TaskDetail>, success: string): void {
    if (this.busy()) return;
    this.busy.set(true);
    command.subscribe({
      next: (updated) => {
        this.busy.set(false);
        this.applyUpdate(updated);
        this.toast.success(success);
      },
      error: () => this.busy.set(false),
    });
  }
}
