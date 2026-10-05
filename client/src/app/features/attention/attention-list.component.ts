import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TaskListItem } from '../../core/models/task.models';
import { AttentionItemDto } from '../../core/models/view.models';
import { formatDateTime } from '../../core/util/dates';
import { UiTone } from '../../core/util/tone';
import { DeadlineTextComponent, ScoreBarComponent } from '../../shared/domain/indicators';
import { AgoPipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { DropdownComponent } from '../../shared/ui/dropdown.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { IconName } from '../../shared/ui/icons';
import {
  AttentionAction,
  AttentionActionKey,
  attentionActions,
  attentionTypeIcon,
  attentionTypeTone,
  splitActions,
} from './attention-actions';
import { EscalateDialogComponent } from './escalate-dialog.component';
import { QuickActions } from './quick-actions.service';

/** Buttons shown inline per row in compact mode; the rest go to the overflow menu. */
const COMPACT_INLINE_ACTIONS = 3;

interface AttentionRow {
  item: AttentionItemDto;
  icon: IconName;
  tone: UiTone;
  status: { text: string; tone: UiTone } | null;
  actions: AttentionAction[];
  primary: AttentionAction[];
  overflow: AttentionAction[];
}

/**
 * Ranked attention items with inline actions (open, remind, escalate, approve/reopen, extension decision,
 * AI suggestion accept/dismiss, resolve, dismiss, snooze). Rows keep the input order — hosts sort.
 *   <fu-attention-list [items]="items" [compact]="true" [limit]="6" (changed)="reload(true)" />
 */
@Component({
  selector: 'fu-attention-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    IconComponent,
    DropdownComponent,
    EmptyStateComponent,
    ScoreBarComponent,
    DeadlineTextComponent,
    EscalateDialogComponent,
    AgoPipe,
    HumanizePipe,
  ],
  templateUrl: './attention-list.component.html',
  styleUrl: './attention-list.component.css',
})
export class AttentionListComponent {
  private readonly router = inject(Router);
  private readonly quick = inject(QuickActions);

  readonly items = input<AttentionItemDto[]>([]);
  /** Fewer details and a single row of actions (dashboard cards). */
  readonly compact = input(false);
  readonly limit = input<number | null>(null);
  /** Emitted after an action changed server state, so the host can reload. */
  readonly changed = output<void>();

  /** Ids of items whose action is running (their buttons are disabled). */
  private readonly busyIds = signal<ReadonlySet<string>>(new Set());
  /** Task being escalated (opens the escalate dialog). */
  protected readonly escalating = signal<TaskListItem | null>(null);

  private readonly all = computed(() => this.items() ?? []);

  protected readonly rows = computed<AttentionRow[]>(() => {
    const limit = this.limit();
    const visible = limit ? this.all().slice(0, limit) : this.all();
    return visible.map((item) => {
      const actions = attentionActions(item);
      const { primary, overflow } = splitActions(actions, COMPACT_INLINE_ACTIONS);
      return {
        item,
        icon: attentionTypeIcon(item.type),
        tone: attentionTypeTone(item.type),
        status: statusChip(item),
        actions,
        primary,
        overflow,
      };
    });
  });

  protected readonly hiddenCount = computed(() => Math.max(0, this.all().length - this.rows().length));

  protected isBusy(id: string): boolean {
    return this.busyIds().has(id);
  }

  protected async run(item: AttentionItemDto, key: AttentionActionKey): Promise<void> {
    if (this.isBusy(item.id)) return;
    const task = item.task ?? null;
    if (key === 'open') {
      if (task) void this.router.navigate(['/tasks', task.id]);
      return;
    }
    if (key === 'escalate') {
      if (task) this.escalating.set(task);
      return;
    }
    this.setBusy(item.id, true);
    try {
      if (await this.perform(item, key)) this.changed.emit();
    } finally {
      this.setBusy(item.id, false);
    }
  }

  protected onEscalated(): void {
    this.changed.emit();
  }

  private perform(item: AttentionItemDto, key: AttentionActionKey): Promise<boolean> {
    const taskId = item.task?.id;
    switch (key) {
      case 'remind':
        return taskId ? this.quick.remind(taskId) : Promise.resolve(false);
      case 'approve':
        return taskId ? this.quick.approveCompletion(taskId) : Promise.resolve(false);
      case 'reopenTask':
        return taskId ? this.quick.reopenCompletion(taskId) : Promise.resolve(false);
      case 'approveExtension':
      case 'rejectExtension':
        return item.extensionRequestId
          ? this.quick.decideExtension(item.extensionRequestId, key === 'approveExtension')
          : Promise.resolve(false);
      case 'acceptSuggestion':
        return item.suggestionId ? this.quick.acceptSuggestion(item.suggestionId) : Promise.resolve(false);
      case 'dismissSuggestion':
        return item.suggestionId ? this.quick.dismissSuggestion(item.suggestionId) : Promise.resolve(false);
      case 'snooze':
        return this.quick.snoozeAttention(item.id);
      case 'resolve':
      case 'dismiss':
      case 'reopen':
        return this.quick.decideAttention(item.id, key);
      default:
        return Promise.resolve(false);
    }
  }

  private setBusy(id: string, on: boolean): void {
    this.busyIds.update((set) => {
      const next = new Set(set);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }
}

/** Status chip for items that are not simply open ("Snoozed until 7 Oct, 09:00", "Resolved", "Dismissed"). */
function statusChip(item: AttentionItemDto): { text: string; tone: UiTone } | null {
  switch (item.status) {
    case 'Snoozed':
      return {
        text: item.snoozedUntil ? `Snoozed until ${formatDateTime(item.snoozedUntil, { short: true })}` : 'Snoozed',
        tone: 'info',
      };
    case 'Resolved':
      return { text: 'Resolved', tone: 'good' };
    case 'Dismissed':
      return { text: 'Dismissed', tone: 'neutral' };
    default:
      return null;
  }
}
