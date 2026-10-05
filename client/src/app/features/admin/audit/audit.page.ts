import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, debounceTime, distinctUntilChanged, map, of, switchMap } from 'rxjs';
import { AdminApi } from '../../../core/api/admin.api';
import { TasksApi } from '../../../core/api/tasks.api';
import { AuditEntryDto, AuditQuery, AuditVerifyResult } from '../../../core/models/admin.models';
import { Guid } from '../../../core/models/common.models';
import { TaskListItem } from '../../../core/models/task.models';
import { loader } from '../../../core/util/loader';
import { DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { valueOf } from '../shared/form-view';

const PAGE_SIZE = 100;
const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

const SOURCE_TONE: Record<string, string> = {
  User: 'neutral',
  System: 'info',
  Ai: 'accent',
  Email: 'info',
  Teams: 'info',
  Recurring: 'neutral',
  Seed: 'neutral',
};

/**
 * /admin/audit — the tamper-evident audit trail with filters synced to the URL
 * (`taskId`, `entityType`, `action`, `from`, `to`, `page`) and hash-chain verification for one task.
 */
@Component({
  selector: 'fu-audit-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DateTimePipe, HumanizePipe, EmptyStateComponent, IconComponent, SkeletonComponent],
  templateUrl: './audit.page.html',
  styleUrl: './audit.page.css',
})
export class AuditPage {
  private readonly api = inject(AdminApi);
  private readonly tasks = inject(TasksApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  // ── URL state (router component-input binding) ──
  readonly taskId = input<string | undefined>();
  readonly entityType = input<string | undefined>();
  readonly action = input<string | undefined>();
  readonly from = input<string | undefined>();
  readonly to = input<string | undefined>();
  readonly page = input<string | undefined>();

  protected readonly pageSize = PAGE_SIZE;
  protected readonly pageNo = computed(() => Math.max(1, Math.floor(Number(this.page()) || 1)));
  private readonly query = computed<AuditQuery>(() => ({
    taskId: this.taskId() || null,
    entityType: this.entityType() || null,
    action: this.action() || null,
    from: this.from() || null,
    to: this.to() || null,
    page: this.pageNo(),
    pageSize: PAGE_SIZE,
  }));
  protected readonly hasFilters = computed(() => {
    const q = this.query();
    return !!(q.taskId || q.entityType || q.action || q.from || q.to);
  });

  // ── Draft filters (applied on submit) ──
  protected readonly draftEntity = signal('');
  protected readonly draftAction = signal('');
  protected readonly draftFrom = signal('');
  protected readonly draftTo = signal('');

  // ── Task picker ──
  protected readonly taskText = signal('');
  protected readonly taskMenuOpen = signal(false);
  /** Label for the chosen task (number · title) when it was picked from the search. */
  private readonly pickedLabel = signal<{ id: Guid; label: string } | null>(null);
  protected readonly taskResults = toSignal(
    toObservable(this.taskText).pipe(
      debounceTime(250),
      map((t) => t.trim()),
      distinctUntilChanged(),
      switchMap((text) =>
        text.length < 2 || GUID_RE.test(text)
          ? of<TaskListItem[]>([])
          : this.tasks.query({ text, pageSize: 8 }).pipe(
              map((r) => r?.items ?? []),
              catchError(() => of<TaskListItem[]>([])),
            ),
      ),
    ),
    { initialValue: [] as TaskListItem[] },
  );

  // ── Data ──
  protected readonly data = loader<AuditEntryDto[]>();
  protected readonly rows = computed(() => this.data.value() ?? []);
  protected readonly verify = signal<AuditVerifyResult | null>(null);
  protected readonly verifying = signal(false);
  private lastTaskId: string | null = null;

  /** "T-0042 · Renew the trade licence", else the task number seen in the rows, else the raw id. */
  protected readonly taskLabel = computed(() => {
    const id = this.taskId();
    if (!id) return '';
    const picked = this.pickedLabel();
    if (picked?.id === id) return picked.label;
    const number = this.rows().find((r) => r.taskId === id && r.taskNumber)?.taskNumber;
    return number ?? id;
  });

  protected readonly brokenEntryId = computed(() => {
    const v = this.verify();
    return v && !v.ok ? (v.brokenEntryId ?? null) : null;
  });

  constructor() {
    effect(() => {
      const q = this.query();
      untracked(() => {
        this.draftEntity.set(q.entityType ?? '');
        this.draftAction.set(q.action ?? '');
        this.draftFrom.set(q.from ?? '');
        this.draftTo.set(q.to ?? '');
        if ((q.taskId ?? null) !== this.lastTaskId) {
          this.lastTaskId = q.taskId ?? null;
          this.verify.set(null);
        }
        this.data.load(this.api.audit(q));
      });
    });
  }

  protected reload(): void {
    this.data.load(this.api.audit(this.query()));
  }

  private navigate(params: Record<string, string | number | null>): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: params, queryParamsHandling: 'merge' });
  }

  protected apply(): void {
    this.navigate({
      entityType: this.draftEntity().trim() || null,
      action: this.draftAction().trim() || null,
      from: this.draftFrom() || null,
      to: this.draftTo() || null,
      page: null,
    });
  }

  protected clear(): void {
    this.taskText.set('');
    this.pickedLabel.set(null);
    this.navigate({ taskId: null, entityType: null, action: null, from: null, to: null, page: null });
  }

  protected goPage(page: number): void {
    this.navigate({ page: page <= 1 ? null : page });
  }

  protected setDraft(target: 'entity' | 'action' | 'from' | 'to', event: Event): void {
    const v = valueOf(event);
    if (target === 'entity') this.draftEntity.set(v);
    else if (target === 'action') this.draftAction.set(v);
    else if (target === 'from') this.draftFrom.set(v);
    else this.draftTo.set(v);
  }

  protected onTaskInput(event: Event): void {
    this.taskText.set(valueOf(event));
    this.taskMenuOpen.set(true);
  }

  /** Enter picks a pasted task id, or the first search result. */
  protected onTaskEnter(event: Event): void {
    event.preventDefault();
    const text = this.taskText().trim();
    if (GUID_RE.test(text)) {
      this.pickTaskId(text, text);
      return;
    }
    const first = this.taskResults()[0];
    if (first) this.pickTask(first);
  }

  protected closeTaskMenuSoon(): void {
    setTimeout(() => this.taskMenuOpen.set(false), 150);
  }

  protected pickTask(t: TaskListItem): void {
    this.pickTaskId(t.id, `${t.number} · ${t.title}`);
  }

  private pickTaskId(id: Guid, label: string): void {
    this.pickedLabel.set({ id, label });
    this.taskText.set('');
    this.taskMenuOpen.set(false);
    this.navigate({ taskId: id, page: null });
  }

  protected clearTask(): void {
    this.pickedLabel.set(null);
    this.navigate({ taskId: null, page: null });
  }

  protected filterTask(id: Guid | null | undefined, number: string | null | undefined): void {
    if (!id) return;
    this.pickTaskId(id, number ?? id);
  }

  protected verifyChain(): void {
    const id = this.taskId();
    if (!id || this.verifying()) return;
    this.verifying.set(true);
    this.api.verifyAudit(id).subscribe({
      next: (r) => {
        this.verifying.set(false);
        this.verify.set(r ?? { ok: false, brokenEntryId: null });
      },
      error: () => this.verifying.set(false),
    });
  }

  protected sourceTone(source: string): string {
    return SOURCE_TONE[source] ?? 'neutral';
  }
}
