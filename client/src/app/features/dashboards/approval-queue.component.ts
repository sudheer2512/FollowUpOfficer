import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Guid } from '../../core/models/common.models';
import { ApprovalQueueItem } from '../../core/models/view.models';
import { AgoPipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { IconName } from '../../shared/ui/icons';
import { QuickActions } from '../attention/quick-actions.service';

/** What the inline buttons decide: an extension request, a completion approval, or nothing (open the task). */
type DecisionKind = 'extension' | 'completion' | 'other';

interface QueueRow {
  item: ApprovalQueueItem;
  key: string;
  taskId: Guid | null;
  number: string;
  title: string;
  decision: DecisionKind;
  icon: IconName;
}

/**
 * Waiting-for-my-approval queue (workbench / my dashboard): completion approvals get Approve / Reopen,
 * extension requests get Approve / Reject (with a note), other kinds link to the task.
 *   <fu-approval-queue [items]="section.approvals" [limit]="6" (changed)="reload(true)" />
 */
@Component({
  selector: 'fu-approval-queue',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, EmptyStateComponent, AgoPipe, HumanizePipe],
  template: `
    <div class="list">
      @for (r of rows(); track r.key) {
        <div class="list-row q-row" [class.busy]="busy().has(r.key)">
          <span class="ic"><fu-icon [name]="r.icon" [size]="15" /></span>
          <div class="main">
            <div class="row gap-4">
              @if (r.taskId) {
                <a class="task-number" [routerLink]="['/tasks', r.taskId]" tabindex="-1">{{ r.number }}</a>
                <a class="title truncate" [routerLink]="['/tasks', r.taskId]">{{ r.title }}</a>
              } @else {
                <span class="title truncate">{{ r.title }}</span>
              }
            </div>
            <div class="meta">
              <span class="chip sm outline">{{ r.item.kind | humanize }}</span>
              @if (r.item.detail) {
                <span class="truncate detail" [attr.title]="r.item.detail">{{ r.item.detail }}</span>
              }
              <span class="nowrap">requested {{ r.item.requestedAt | ago }}</span>
            </div>
          </div>
          <div class="acts" role="group" [attr.aria-label]="'Decide on ' + r.title">
            @switch (r.decision) {
              @case ('extension') {
                <button type="button" class="btn btn-xs btn-good" [disabled]="busy().has(r.key)" (click)="decideExtension(r, true)">
                  <fu-icon name="check" [size]="13" /> Approve
                </button>
                <button type="button" class="btn btn-xs btn-danger" [disabled]="busy().has(r.key)" (click)="decideExtension(r, false)">
                  <fu-icon name="x" [size]="13" /> Reject
                </button>
              }
              @case ('completion') {
                <button type="button" class="btn btn-xs btn-good" [disabled]="busy().has(r.key) || !r.taskId" (click)="approve(r)">
                  <fu-icon name="check" [size]="13" /> Approve
                </button>
                <button type="button" class="btn btn-xs" [disabled]="busy().has(r.key) || !r.taskId" (click)="reopen(r)">
                  <fu-icon name="rotate" [size]="13" /> Reopen
                </button>
              }
              @default {
                @if (r.taskId) {
                  <a class="btn btn-xs" [routerLink]="['/tasks', r.taskId]">Open task</a>
                }
              }
            }
          </div>
        </div>
      } @empty {
        <fu-empty icon="check-circle" title="Nothing waiting for your approval" message="Completions and extension requests you need to decide appear here." [compact]="true" />
      }
    </div>
    @if (hiddenCount() > 0) {
      <div class="more small muted">+ {{ hiddenCount() }} more</div>
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .q-row { align-items: flex-start; padding: 10px 16px; }
    .q-row.busy { opacity: 0.6; }
    .ic { width: 28px; height: 28px; border-radius: 8px; background: var(--accent-soft); color: var(--accent-text); display: flex; align-items: center; justify-content: center; flex: none; }
    .main { flex: 1 1 auto; min-width: 0; }
    .detail { max-width: 320px; }
    .acts { display: flex; gap: 4px; flex: none; flex-wrap: wrap; justify-content: flex-end; }
    .more { padding: 8px 16px; border-top: 1px solid var(--border); }
    @media (max-width: 760px) { .q-row { flex-wrap: wrap; } .acts { width: 100%; justify-content: flex-start; padding-left: 40px; } }
  `,
})
export class ApprovalQueueComponent {
  private readonly quick = inject(QuickActions);

  readonly items = input<ApprovalQueueItem[]>([]);
  readonly limit = input<number | null>(null);
  /** Emitted after a decision changed server state. */
  readonly changed = output<void>();

  protected readonly busy = signal<ReadonlySet<string>>(new Set());

  private readonly all = computed(() => this.items() ?? []);

  protected readonly rows = computed<QueueRow[]>(() => {
    const limit = this.limit();
    const visible = limit ? this.all().slice(0, limit) : this.all();
    return visible.map((item) => {
      const decision = decisionKind(item.kind);
      return {
        item,
        key: `${item.kind}:${item.id}`,
        taskId: item.task?.id ?? null,
        number: item.task?.number ?? '',
        title: item.title || item.task?.title || 'Approval request',
        decision,
        icon: decision === 'extension' ? 'calendar-clock' : decision === 'completion' ? 'check-square' : 'user-check',
      };
    });
  });

  protected readonly hiddenCount = computed(() => Math.max(0, this.all().length - this.rows().length));

  protected approve(r: QueueRow): void {
    const taskId = r.taskId;
    if (taskId) void this.track(r, () => this.quick.approveCompletion(taskId));
  }

  protected reopen(r: QueueRow): void {
    const taskId = r.taskId;
    if (taskId) void this.track(r, () => this.quick.reopenCompletion(taskId));
  }

  /** For extension kinds the queue item id is the extension request id. */
  protected decideExtension(r: QueueRow, approve: boolean): void {
    void this.track(r, () => this.quick.decideExtension(r.item.id, approve));
  }

  /** Runs one decision with the row's buttons disabled (including while its prompt is open). */
  private async track(r: QueueRow, action: () => Promise<boolean>): Promise<void> {
    if (this.busy().has(r.key)) return;
    this.setBusy(r.key, true);
    try {
      if (await action()) this.changed.emit();
    } finally {
      this.setBusy(r.key, false);
    }
  }

  private setBusy(key: string, on: boolean): void {
    this.busy.update((set) => {
      const next = new Set(set);
      if (on) next.add(key);
      else next.delete(key);
      return next;
    });
  }
}

/** "Extension…" → extension; "Acknowledgement…" → other; anything else (Completion, Approval…) → completion. */
function decisionKind(kind: string | null | undefined): DecisionKind {
  const k = (kind ?? '').toLowerCase();
  if (k.includes('extension')) return 'extension';
  if (k.includes('acknowledg')) return 'other';
  return 'completion';
}
