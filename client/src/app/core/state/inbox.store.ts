import { Injectable, inject, signal } from '@angular/core';
import { ExecutiveApi } from '../api/executive.api';
import { Guid } from '../models/common.models';
import { InboxItem } from '../models/view.models';

/** In-app notification inbox: unread badge + drawer items. */
@Injectable({ providedIn: 'root' })
export class InboxStore {
  private readonly api = inject(ExecutiveApi);

  readonly unread = signal(0);
  readonly items = signal<InboxItem[]>([]);
  readonly loading = signal(false);
  readonly open = signal(false);

  refreshCount(): void {
    this.api.unreadCount().subscribe({
      next: (r) => this.unread.set(r?.count ?? 0),
      error: () => undefined,
    });
  }

  loadItems(): void {
    this.loading.set(true);
    this.api.inbox().subscribe({
      next: (items) => {
        this.items.set(items ?? []);
        this.loading.set(false);
        this.unread.set((items ?? []).filter((i) => !i.readAt).length);
      },
      error: () => this.loading.set(false),
    });
  }

  markRead(id: Guid): void {
    const item = this.items().find((i) => i.id === id);
    if (!item || item.readAt) return;
    this.items.update((list) => list.map((i) => (i.id === id ? { ...i, readAt: new Date().toISOString() } : i)));
    this.unread.update((n) => Math.max(0, n - 1));
    this.api.markRead(id).subscribe({ error: () => this.refreshCount() });
  }

  markAllRead(): void {
    const now = new Date().toISOString();
    this.items.update((list) => list.map((i) => (i.readAt ? i : { ...i, readAt: now })));
    this.unread.set(0);
    this.api.markAllRead().subscribe({ error: () => this.refreshCount() });
  }

  toggle(): void {
    const next = !this.open();
    this.open.set(next);
    if (next) this.loadItems();
  }

  reset(): void {
    this.unread.set(0);
    this.items.set([]);
    this.open.set(false);
  }
}
