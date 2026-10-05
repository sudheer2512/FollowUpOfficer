import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, output, signal, untracked } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { debounceTime, distinctUntilChanged } from 'rxjs';
import { MessagesApi } from '../../core/api/messages.api';
import { DateOnly, Guid, PagedResult } from '../../core/models/common.models';
import {
  COMMUNICATION_CHANNEL_VALUES,
  CommunicationChannel,
  MESSAGE_PROCESSING_STATUS_VALUES,
  MessageProcessingStatus,
} from '../../core/models/enums';
import { MessageDto, MessageFilter } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { loader } from '../../core/util/loader';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { PaginatorComponent } from '../../shared/ui/paginator.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ToastService } from '../../shared/ui/toast.service';
import { categoryTone, channelIcon, messageHeadline, messagePreview, processingTone, senderLabel, visibleLinks } from './message-ui';

const PAGE_SIZE = 50;

/**
 * Paged message table. `mode="all"` shows the full filter bar; `mode="failed"` lists messages whose processing failed,
 * with a Reprocess action per row. Rows open the message drawer through (open).
 */
@Component({
  selector: 'fu-message-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, EmptyStateComponent, SkeletonComponent, PaginatorComponent, AgoPipe, DateTimePipe, HumanizePipe],
  templateUrl: './message-list.component.html',
  styleUrl: './message-list.component.css',
})
export class MessageListComponent {
  private readonly api = inject(MessagesApi);
  private readonly toast = inject(ToastService);

  readonly mode = input<'all' | 'failed'>('all');
  readonly open = output<Guid>();

  protected readonly channels = COMMUNICATION_CHANNEL_VALUES;
  protected readonly statuses = MESSAGE_PROCESSING_STATUS_VALUES;
  protected readonly icon = channelIcon;
  protected readonly headline = messageHeadline;
  protected readonly preview = messagePreview;
  protected readonly sender = senderLabel;
  protected readonly links = visibleLinks;
  protected readonly catTone = categoryTone;
  protected readonly procTone = processingTone;
  protected readonly data = loader<PagedResult<MessageDto>>();
  protected readonly reprocessing = signal<ReadonlySet<Guid>>(new Set());

  // ── Filters ──
  protected readonly textInput = signal('');
  private readonly text = toSignal(toObservable(this.textInput).pipe(debounceTime(300), distinctUntilChanged()), { initialValue: '' });
  protected readonly channel = signal<CommunicationChannel | ''>('');
  protected readonly status = signal<MessageProcessingStatus | ''>('');
  protected readonly unlinked = signal(false);
  protected readonly from = signal<DateOnly>('');
  protected readonly to = signal<DateOnly>('');

  private readonly criteria = computed<MessageFilter>(() => {
    const text = this.text().trim() || null;
    if (this.mode() === 'failed') return { text, status: 'Failed' };
    return {
      text,
      channel: this.channel() || null,
      status: this.status() || null,
      unlinked: this.unlinked() || null,
      from: this.from() || null,
      to: this.to() || null,
    };
  });
  /** Back to page 1 whenever the criteria change. */
  protected readonly page = linkedSignal({ source: this.criteria, computation: () => 1 });
  private readonly request = computed<MessageFilter>(() => ({ ...this.criteria(), page: this.page(), pageSize: PAGE_SIZE }));

  protected readonly items = computed(() => this.data.value()?.items ?? []);
  protected readonly total = computed(() => this.data.value()?.total ?? 0);
  protected readonly hasFilters = computed(
    () => !!(this.textInput().trim() || this.channel() || this.status() || this.unlinked() || this.from() || this.to()),
  );
  protected readonly pageSize = PAGE_SIZE;

  constructor() {
    effect(() => {
      const req = this.request();
      untracked(() => this.data.load(this.api.list(req)));
    });
    refreshOn(['communication.imported'], () => this.reload(true));
  }

  /** Re-fetches the current page (silently keeps the rows on screen). */
  reload(silent = false): void {
    this.data.load(this.api.list(this.request()), { silent });
  }

  protected clearFilters(): void {
    this.textInput.set('');
    this.channel.set('');
    this.status.set('');
    this.unlinked.set(false);
    this.from.set('');
    this.to.set('');
  }

  protected openRow(m: MessageDto, e: Event): void {
    if (e.type === 'click' && (e.target as HTMLElement).closest('button')) return;
    this.open.emit(m.id);
  }

  protected reprocess(m: MessageDto, e: Event): void {
    e.stopPropagation();
    if (this.reprocessing().has(m.id)) return;
    this.reprocessing.update((s) => new Set(s).add(m.id));
    const done = () =>
      this.reprocessing.update((s) => {
        const next = new Set(s);
        next.delete(m.id);
        return next;
      });
    this.api.reprocess(m.id).subscribe({
      next: () => {
        done();
        this.toast.success('Queued for reprocessing');
        this.reload(true);
      },
      error: done,
    });
  }
}
