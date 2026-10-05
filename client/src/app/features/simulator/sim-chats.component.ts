import { ChangeDetectionStrategy, Component, computed, effect, inject, input, linkedSignal, signal, untracked } from '@angular/core';
import { loader } from '../../core/util/loader';
import { humanize } from '../../core/util/humanize';
import { AvatarComponent } from '../../shared/domain/indicators';
import { AgoPipe, DateTimePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ModalComponent } from '../../shared/ui/modal.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { eventValue, injectAnnounceSent, userLabel } from './sim-feedback';
import { SimulatorApi } from './simulator.api';
import { SimChat, SimChatMessage, SimUser } from './simulator.models';

const MAX_AVATARS = 4;

/** Teams chats tab: chat list, message pane and a composer that posts as any member; plus "New 1:1 chat". */
@Component({
  selector: 'fu-sim-chats',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AvatarComponent, AgoPipe, DateTimePipe, EmptyStateComponent, IconComponent, ModalComponent, SkeletonComponent],
  templateUrl: './sim-chats.component.html',
  styleUrl: './sim-pane.css',
})
export class SimChatsComponent {
  private readonly api = inject(SimulatorApi);
  private readonly announceSent = injectAnnounceSent();

  readonly users = input<SimUser[]>([]);

  protected readonly maxAvatars = MAX_AVATARS;
  protected readonly chats = loader<SimChat[]>();
  protected readonly selectedId = signal<string | null>(null);
  protected readonly messages = loader<SimChatMessage[]>();
  protected readonly text = signal('');
  protected readonly posting = signal(false);

  /** New 1:1 chat dialog state (user ids). */
  protected readonly newChat = signal<{ userA: string; userB: string } | null>(null);
  protected readonly creating = signal(false);

  private readonly usersById = computed(() => new Map(this.users().map((u) => [u.id, u])));

  protected readonly chatList = computed(() =>
    (this.chats.value() ?? []).map((c) => ({ chat: c, title: this.titleOf(c), names: this.memberNames(c) })),
  );
  protected readonly selected = computed(() => this.chatList().find((c) => c.chat.id === this.selectedId()) ?? null);

  protected readonly thread = computed(() =>
    [...(this.messages.value() ?? [])].sort((a, b) => (a.createdDateTime ?? '').localeCompare(b.createdDateTime ?? '')),
  );

  /** "Post as": the chat's members when known, otherwise anyone in the tenant. */
  protected readonly posters = computed(() => {
    const chat = this.selected()?.chat;
    const members = (chat?.members ?? [])
      .map((m) => (m.userId ? this.usersById().get(m.userId) : undefined))
      .filter((u): u is SimUser => !!u);
    return members.length ? members : this.users();
  });
  protected readonly poster = linkedSignal<SimUser[], string>({
    source: this.posters,
    computation: (list, previous) => (previous && list.some((u) => u.id === previous.value) ? previous.value : (list[0]?.id ?? '')),
  });

  constructor() {
    this.reloadChats();
    effect(() => {
      const id = this.selectedId();
      untracked(() => {
        this.text.set('');
        if (id) this.messages.load(this.api.chatMessages(id));
        else this.messages.set([]);
      });
    });
  }

  protected reloadChats(selectId?: string | null): void {
    this.chats.load(this.api.chats(), { silent: !!this.chats.value() });
    if (selectId) this.selectedId.set(selectId);
  }

  protected reloadMessages(): void {
    const id = this.selectedId();
    if (id) this.messages.load(this.api.chatMessages(id), { silent: true });
  }

  protected memberNames(c: SimChat): string[] {
    return c.members.map((m) => m.displayName ?? (m.userId ? this.usersById().get(m.userId)?.displayName : null) ?? m.userId ?? 'Unknown');
  }

  protected titleOf(c: SimChat): string {
    return c.topic || this.memberNames(c).join(', ') || c.id;
  }

  protected chatTypeLabel(c: SimChat): string {
    return c.chatType ? humanize(c.chatType) : 'Chat';
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

  /** Ctrl/Cmd + Enter posts. */
  protected onComposerKey(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      this.post();
    }
  }

  protected post(): void {
    const id = this.selectedId();
    const text = this.text().trim();
    const from = this.poster();
    if (!id || !text || !from || this.posting()) return;
    this.posting.set(true);
    this.api.postChatMessage(id, { fromUserId: from, text }).subscribe({
      next: () => {
        this.posting.set(false);
        this.text.set('');
        this.announceSent();
        this.reloadMessages();
      },
      error: () => this.posting.set(false),
    });
  }

  protected openNewChat(): void {
    const [a, b] = this.users();
    this.newChat.set({ userA: a?.id ?? '', userB: b?.id ?? '' });
  }

  protected setNewChat(field: 'userA' | 'userB', event: Event): void {
    const value = eventValue(event);
    this.newChat.update((d) => (d ? { ...d, [field]: value } : d));
  }

  protected createChat(): void {
    const d = this.newChat();
    if (!d || !d.userA || !d.userB || d.userA === d.userB || this.creating()) return;
    this.creating.set(true);
    this.api.ensureOneOnOne(d).subscribe({
      next: (chatId) => {
        this.creating.set(false);
        this.newChat.set(null);
        this.reloadChats(chatId);
      },
      error: () => this.creating.set(false),
    });
  }
}
