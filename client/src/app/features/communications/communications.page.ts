import { ChangeDetectionStrategy, Component, computed, inject, input, signal, viewChild } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MessagesApi } from '../../core/api/messages.api';
import { Guid } from '../../core/models/common.models';
import { MessageDto } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { loader } from '../../core/util/loader';
import { TabItem, TabsComponent } from '../../shared/ui/tabs.component';
import { ToastService } from '../../shared/ui/toast.service';
import { MessageDrawerComponent } from './message-drawer.component';
import { MessageListComponent } from './message-list.component';
import { suggestedLinks, withoutSuggestion } from './message-ui';
import { LinkDecision, ReviewQueueComponent } from './review-queue.component';

type CommsTab = 'all' | 'review' | 'failed';
const TABS: CommsTab[] = ['all', 'review', 'failed'];

/**
 * /communications — imported email and Teams messages.
 * Tabs (?tab=all|review|failed): all messages with filters, the low-confidence review queue, and failed messages.
 * ?message=<id> opens the message drawer (kept in the URL so other pages can link to a message).
 */
@Component({
  selector: 'fu-communications-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TabsComponent, MessageListComponent, ReviewQueueComponent, MessageDrawerComponent],
  template: `
    <div class="page">
      <div class="page-header">
        <div class="titles">
          <h1>Communications</h1>
          <div class="subtitle">Email and Teams replies, analysed and linked to delegated tasks automatically.</div>
        </div>
      </div>

      <fu-tabs class="mb-16" [tabs]="tabs()" [active]="activeTab()" (activeChange)="selectTab($event)" ariaLabel="Message views" />

      @switch (activeTab()) {
        @case ('review') {
          <fu-review-queue
            [messages]="reviewMessages()"
            [loading]="review.loading()"
            [error]="review.error()"
            [busyLinks]="busyLinks()"
            (open)="openMessage($event)"
            (decide)="decide($event)"
            (retry)="reloadReview()"
          />
        }
        @case ('failed') {
          <fu-message-list mode="failed" (open)="openMessage($event)" />
        }
        @default {
          <fu-message-list mode="all" (open)="openMessage($event)" />
        }
      }
    </div>

    @if (message(); as id) {
      <fu-message-drawer [messageId]="id" (closed)="closeMessage()" (changed)="onDrawerChange()" />
    }
  `,
})
export class CommunicationsPage {
  private readonly api = inject(MessagesApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);

  /** Query parameter ?tab=. */
  readonly tab = input<string | undefined>(undefined);
  /** Query parameter ?message= (opens the drawer). */
  readonly message = input<Guid | undefined>(undefined);

  private readonly list = viewChild(MessageListComponent);

  protected readonly review = loader<MessageDto[]>();
  protected readonly busyLinks = signal<ReadonlySet<Guid>>(new Set());

  protected readonly activeTab = computed<CommsTab>(() => {
    const t = (this.tab() ?? '').toLowerCase() as CommsTab;
    return TABS.includes(t) ? t : 'all';
  });
  protected readonly reviewMessages = computed(() => (this.review.value() ?? []).filter((m) => suggestedLinks(m).length > 0));
  protected readonly tabs = computed<TabItem[]>(() => [
    { key: 'all', label: 'All messages' },
    {
      key: 'review',
      label: 'Review queue',
      count: this.review.value() ? this.reviewMessages().length : null,
      tone: this.reviewMessages().length ? 'warning' : '',
    },
    { key: 'failed', label: 'Failed' },
  ]);

  constructor() {
    this.reloadReview();
    refreshOn(['communication.imported'], () => this.reloadReview(true));
  }

  protected reloadReview(silent = false): void {
    this.review.load(this.api.reviewQueue(), { silent });
  }

  protected selectTab(key: string): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: key === 'all' ? null : key },
      queryParamsHandling: 'merge',
    });
  }

  protected openMessage(id: Guid): void {
    this.setMessageParam(id);
  }

  protected closeMessage(): void {
    this.setMessageParam(null);
  }

  protected onDrawerChange(): void {
    this.reloadReview(true);
    this.list()?.reload(true);
  }

  protected decide(d: LinkDecision): void {
    const { link, action } = d;
    if (this.busyLinks().has(link.linkId)) return;
    this.setBusy(link.linkId, true);
    const call = action === 'confirm' ? this.api.confirmLink(link.linkId) : this.api.rejectLink(link.linkId);
    call.subscribe({
      next: () => {
        this.setBusy(link.linkId, false);
        this.review.update((list) => withoutSuggestion(list ?? [], link.linkId));
        this.toast.success(action === 'confirm' ? `Linked to ${link.number}` : 'Suggestion rejected');
      },
      error: () => this.setBusy(link.linkId, false),
    });
  }

  private setMessageParam(id: Guid | null): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { message: id },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  private setBusy(linkId: Guid, on: boolean): void {
    this.busyLinks.update((s) => {
      const next = new Set(s);
      if (on) next.add(linkId);
      else next.delete(linkId);
      return next;
    });
  }
}
