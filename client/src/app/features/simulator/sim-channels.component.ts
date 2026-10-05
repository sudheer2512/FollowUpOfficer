import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { loader } from '../../core/util/loader';
import { AvatarComponent } from '../../shared/domain/indicators';
import { AgoPipe, DateTimePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { eventValue, injectAnnounceSent, userLabel } from './sim-feedback';
import { SimulatorApi } from './simulator.api';
import { SimChannel, SimChatMessage, SimUser } from './simulator.models';

interface TeamGroup {
  teamId: string;
  teamName: string;
  channels: SimChannel[];
}

interface Thread {
  root: SimChatMessage;
  replies: SimChatMessage[];
}

function keyOf(c: SimChannel): string {
  return `${c.teamId}|${c.channelId}`;
}

function byTime(a: SimChatMessage, b: SimChatMessage): number {
  return (a.createdDateTime ?? '').localeCompare(b.createdDateTime ?? '');
}

/** Channels tab: teams and channels, threaded channel messages with replies, post or reply as any user. */
@Component({
  selector: 'fu-sim-channels',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AvatarComponent, AgoPipe, DateTimePipe, EmptyStateComponent, IconComponent, SkeletonComponent],
  templateUrl: './sim-channels.component.html',
  styleUrl: './sim-pane.css',
})
export class SimChannelsComponent {
  private readonly api = inject(SimulatorApi);
  private readonly document = inject(DOCUMENT);
  private readonly announceSent = injectAnnounceSent();

  readonly users = input<SimUser[]>([]);

  protected readonly channels = loader<SimChannel[]>();
  protected readonly selectedKey = signal<string | null>(null);
  protected readonly messages = loader<SimChatMessage[]>();
  protected readonly replyTo = signal<SimChatMessage | null>(null);
  protected readonly text = signal('');
  protected readonly posting = signal(false);

  private readonly usersById = computed(() => new Map(this.users().map((u) => [u.id, u])));

  protected readonly groups = computed<TeamGroup[]>(() => {
    const map = new Map<string, TeamGroup>();
    for (const c of this.channels.value() ?? []) {
      const g = map.get(c.teamId) ?? { teamId: c.teamId, teamName: c.teamName ?? c.teamId, channels: [] };
      g.channels.push(c);
      map.set(c.teamId, g);
    }
    return [...map.values()].sort((a, b) => a.teamName.localeCompare(b.teamName));
  });

  protected readonly selected = computed(() => (this.channels.value() ?? []).find((c) => keyOf(c) === this.selectedKey()) ?? null);

  /** Root posts with their replies nested underneath (replies whose parent is missing become roots). */
  protected readonly threads = computed<Thread[]>(() => {
    const all = this.messages.value() ?? [];
    const ids = new Set(all.map((m) => m.id));
    const replies = new Map<string, SimChatMessage[]>();
    const roots: SimChatMessage[] = [];
    for (const m of all) {
      if (m.replyToId && ids.has(m.replyToId) && m.replyToId !== m.id) {
        const list = replies.get(m.replyToId) ?? [];
        list.push(m);
        replies.set(m.replyToId, list);
      } else {
        roots.push(m);
      }
    }
    return roots.sort(byTime).map((root) => ({ root, replies: (replies.get(root.id) ?? []).sort(byTime) }));
  });

  protected readonly poster = linkedSignal<SimUser[], string>({
    source: this.users,
    computation: (list, previous) => (previous && list.some((u) => u.id === previous.value) ? previous.value : (list[0]?.id ?? '')),
  });

  constructor() {
    this.reloadChannels();
    // Keyed on the selection only, so a silent channel-list refresh does not wipe the composer.
    effect(() => {
      const key = this.selectedKey();
      untracked(() => {
        this.replyTo.set(null);
        this.text.set('');
        const channel = this.selected();
        if (key && channel) this.messages.load(this.api.channelMessages(channel.teamId, channel.channelId));
        else this.messages.set([]);
      });
    });
  }

  protected reloadChannels(): void {
    this.channels.load(this.api.channels(), { silent: !!this.channels.value() });
  }

  protected reloadMessages(): void {
    const c = this.selected();
    if (c) this.messages.load(this.api.channelMessages(c.teamId, c.channelId), { silent: true });
  }

  protected key(c: SimChannel): string {
    return keyOf(c);
  }

  protected senderOf(m: SimChatMessage): string {
    return m.fromName ?? (m.fromUserId ? this.usersById().get(m.fromUserId)?.displayName : null) ?? 'Unknown sender';
  }

  protected label(u: SimUser): string {
    return userLabel(u);
  }

  protected value(event: Event): string {
    return eventValue(event);
  }

  protected startReply(m: SimChatMessage): void {
    this.replyTo.set(m);
    setTimeout(() => this.document.getElementById('sim-channel-text')?.focus());
  }

  /** Ctrl/Cmd + Enter posts. */
  protected onComposerKey(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      this.post();
    }
  }

  protected post(): void {
    const c = this.selected();
    const text = this.text().trim();
    const from = this.poster();
    if (!c || !text || !from || this.posting()) return;
    this.posting.set(true);
    this.api.postChannelMessage(c.teamId, c.channelId, { fromUserId: from, text, replyToId: this.replyTo()?.id ?? null }).subscribe({
      next: () => {
        this.posting.set(false);
        this.text.set('');
        this.replyTo.set(null);
        this.announceSent();
        this.reloadMessages();
      },
      error: () => this.posting.set(false),
    });
  }
}
