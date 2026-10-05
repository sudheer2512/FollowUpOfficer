import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MessagesApi } from '../../core/api/messages.api';
import { Guid } from '../../core/models/common.models';
import { LinkStatus } from '../../core/models/enums';
import { LinkedTaskRef, MessageDto, MessageParty } from '../../core/models/view.models';
import { humanize } from '../../core/util/humanize';
import { loader } from '../../core/util/loader';
import { PersonComponent, StatusBadgeComponent } from '../../shared/domain/indicators';
import { ConfidencePipe, DateOnlyPipe, DateTimePipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { DrawerComponent } from '../../shared/ui/drawer.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ToastService } from '../../shared/ui/toast.service';
import { LinkSuggestionComponent } from './link-suggestion.component';
import { categoryTone, channelIcon, messageHeadline, partyLabel, processingTone, senderLabel } from './message-ui';

interface RecipientGroup {
  kind: string;
  parties: MessageParty[];
}

/**
 * Full message: body, sender, recipients, analysis, extracted facts and linked tasks (Confirm / Reject suggestions,
 * Reprocess). Emits (changed) after any decision so the page can refresh its lists.
 */
@Component({
  selector: 'fu-message-drawer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DrawerComponent,
    LinkSuggestionComponent,
    PersonComponent,
    StatusBadgeComponent,
    EmptyStateComponent,
    SkeletonComponent,
    IconComponent,
    HumanizePipe,
    DateTimePipe,
    DateOnlyPipe,
    ConfidencePipe,
  ],
  templateUrl: './message-drawer.component.html',
  styles: `
    .stack-lg > section { min-width: 0; }
    .kv { grid-template-columns: minmax(96px, 26%) 1fr; }
    .parties { display: flex; flex-direction: column; gap: 2px; word-break: break-word; }
    .body { background: var(--surface-2); border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 12px 14px; font-size: 13.5px; max-height: 420px; overflow-y: auto; }
    .summary { display: flex; gap: 6px; align-items: flex-start; color: var(--text-2); }
    .summary fu-icon { color: var(--accent-text); margin-top: 2px; flex: none; }
    .links { display: grid; gap: 8px; }
    .mono { word-break: break-all; }
  `,
})
export class MessageDrawerComponent {
  private readonly api = inject(MessagesApi);
  private readonly toast = inject(ToastService);

  readonly messageId = input.required<Guid>();
  readonly closed = output<void>();
  /** A link was confirmed / rejected or the message was queued for reprocessing. */
  readonly changed = output<void>();

  protected readonly data = loader<MessageDto>();
  protected readonly busyLinks = signal<ReadonlySet<Guid>>(new Set());
  protected readonly reprocessing = signal(false);

  protected readonly message = computed(() => this.data.value() ?? null);
  protected readonly title = computed(() => {
    const m = this.message();
    return m ? messageHeadline(m) : this.data.loading() ? 'Loading message…' : 'Message';
  });
  protected readonly subtitle = computed(() => {
    const m = this.message();
    return m ? `${senderLabel(m)} · ${humanize(m.channel)}` : '';
  });

  protected readonly recipientGroups = computed<RecipientGroup[]>(() => {
    const groups = new Map<string, MessageParty[]>();
    for (const p of this.message()?.recipients ?? []) {
      const kind = (p.kind ?? '').trim() || 'Recipients';
      groups.set(kind, [...(groups.get(kind) ?? []), p]);
    }
    return [...groups].map(([kind, parties]) => ({ kind, parties }));
  });

  /** The extracted facts, or null when nothing useful was extracted ("No facts extracted"). */
  protected readonly visibleFacts = computed(() => {
    const f = this.message()?.facts;
    if (!f) return null;
    const any =
      f.suggestedStatus ||
      f.blocker ||
      f.commitmentDate ||
      f.commitmentText ||
      f.proposedDueDate ||
      (f.progressPercent !== null && f.progressPercent !== undefined) ||
      f.urgent ||
      (f.signals ?? []).length;
    return any ? f : null;
  });
  protected readonly factSignals = computed(() => this.visibleFacts()?.signals ?? []);
  protected readonly links = computed(() => this.message()?.links ?? []);

  protected readonly channelIcon = channelIcon;
  protected readonly catTone = categoryTone;
  protected readonly procTone = processingTone;
  protected readonly party = partyLabel;

  constructor() {
    effect(() => {
      const id = this.messageId();
      untracked(() => this.data.load(this.api.get(id)));
    });
  }

  protected reload(silent = false): void {
    this.data.load(this.api.get(this.messageId()), { silent });
  }

  protected decide(link: LinkedTaskRef, action: 'confirm' | 'reject'): void {
    if (this.busyLinks().has(link.linkId)) return;
    this.setBusy(link.linkId, true);
    const call = action === 'confirm' ? this.api.confirmLink(link.linkId) : this.api.rejectLink(link.linkId);
    call.subscribe({
      next: () => {
        this.setBusy(link.linkId, false);
        const status: LinkStatus = action === 'confirm' ? 'Linked' : 'Rejected';
        this.data.update((m) => (m ? { ...m, links: (m.links ?? []).map((l) => (l.linkId === link.linkId ? { ...l, status } : l)) } : m));
        this.toast.success(action === 'confirm' ? `Linked to ${link.number}` : 'Suggestion rejected');
        this.changed.emit();
      },
      error: () => this.setBusy(link.linkId, false),
    });
  }

  protected reprocess(): void {
    const m = this.message();
    if (!m || this.reprocessing()) return;
    this.reprocessing.set(true);
    this.api.reprocess(m.id).subscribe({
      next: () => {
        this.reprocessing.set(false);
        this.toast.success('Queued for reprocessing');
        this.changed.emit();
        this.reload(true);
      },
      error: () => this.reprocessing.set(false),
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
