import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { NotificationCategory } from '../core/models/enums';
import { InboxItem } from '../core/models/view.models';
import { InboxStore } from '../core/state/inbox.store';
import { AgoPipe, HumanizePipe } from '../shared/pipes/format.pipes';
import { DrawerComponent } from '../shared/ui/drawer.component';
import { EmptyStateComponent } from '../shared/ui/empty-state.component';
import { IconComponent } from '../shared/ui/icon.component';
import { SkeletonComponent } from '../shared/ui/skeleton.component';

const CATEGORY_ICON: Partial<Record<NotificationCategory, string>> = {
  Assignment: 'user-plus',
  AcknowledgementRequest: 'user-check',
  Reminder: 'bell',
  DueSoon: 'clock',
  DueToday: 'clock',
  Overdue: 'alert-triangle',
  Escalation: 'zap',
  CommitmentMissed: 'flag',
  ApprovalRequest: 'check-square',
  ApprovalDecision: 'check-circle',
  ExtensionRequest: 'calendar-clock',
  ExtensionDecision: 'calendar',
  StatusChange: 'activity',
  Comment: 'message',
  CeoAlert: 'alert-circle',
  Digest: 'file',
  WeeklyReview: 'calendar',
};

/** In-app notifications: mark read, read all, click → task. */
@Component({
  selector: 'fu-inbox-drawer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DrawerComponent, IconComponent, EmptyStateComponent, SkeletonComponent, AgoPipe, HumanizePipe],
  template: `
    <fu-drawer title="Notifications" [subtitle]="inbox.unread() ? inbox.unread() + ' unread' : 'All caught up'" width="min(440px, 100vw)" (closed)="inbox.open.set(false)">
      <ng-container drawer-actions>
        <div class="segmented" role="group" aria-label="Filter notifications">
          <button type="button" [class.active]="!unreadOnly()" (click)="unreadOnly.set(false)">All</button>
          <button type="button" [class.active]="unreadOnly()" (click)="unreadOnly.set(true)">Unread</button>
        </div>
        <button type="button" class="btn btn-sm btn-ghost" [disabled]="!inbox.unread()" (click)="inbox.markAllRead()">Read all</button>
      </ng-container>
      @if (inbox.loading() && !inbox.items().length) {
        <fu-skeleton [rows]="6" />
      } @else {
        <div class="items">
          @for (n of visible(); track n.id) {
            <div class="item" [class.unread]="!n.readAt">
              <button type="button" class="open" (click)="open(n)">
                <span class="ic"><fu-icon [name]="icon(n)" [size]="15" /></span>
                <span class="body">
                  <span class="subj">{{ n.subject }}</span>
                  @if (n.body) {
                    <span class="txt clamp-2">{{ n.body }}</span>
                  }
                  <span class="meta">
                    {{ n.category | humanize }}
                    @if (n.taskNumber) {
                      · <span class="task-number">{{ n.taskNumber }}</span>
                    }
                    · {{ n.createdAt | ago }}
                  </span>
                </span>
              </button>
              @if (!n.readAt) {
                <button type="button" class="btn btn-ghost btn-icon btn-xs" aria-label="Mark as read" title="Mark as read" (click)="inbox.markRead(n.id)">
                  <fu-icon name="check" [size]="14" />
                </button>
              }
            </div>
          } @empty {
            <fu-empty icon="bell" [title]="unreadOnly() ? 'No unread notifications' : 'No notifications yet'" message="Reminders, escalations and approvals for you appear here." />
          }
        </div>
      }
    </fu-drawer>
  `,
  styles: `
    .items { margin: -16px; }
    .item { display: flex; align-items: flex-start; gap: 6px; padding: 10px 12px 10px 16px; border-bottom: 1px solid var(--border); }
    .item.unread { background: color-mix(in srgb, var(--accent-soft) 55%, transparent); }
    .open { flex: 1; min-width: 0; display: flex; gap: 10px; text-align: left; background: none; border: 0; padding: 0; cursor: pointer; color: inherit; }
    .ic { width: 28px; height: 28px; border-radius: 8px; background: var(--surface-2); border: 1px solid var(--border); display: flex; align-items: center; justify-content: center; color: var(--muted); flex: none; }
    .unread .ic { color: var(--accent-text); }
    .body { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    .subj { font-weight: 500; }
    .unread .subj { font-weight: 600; }
    .txt { font-size: 12.5px; color: var(--text-2); }
    .meta { font-size: 11.5px; color: var(--muted); }
  `,
})
export class InboxDrawerComponent {
  protected readonly inbox = inject(InboxStore);
  private readonly router = inject(Router);
  protected readonly unreadOnly = signal(false);
  protected readonly visible = computed(() => (this.unreadOnly() ? this.inbox.items().filter((i) => !i.readAt) : this.inbox.items()));

  protected icon(n: InboxItem): string {
    return CATEGORY_ICON[n.category] ?? 'bell';
  }

  protected open(n: InboxItem): void {
    this.inbox.markRead(n.id);
    if (n.taskId) {
      this.inbox.open.set(false);
      void this.router.navigate(['/tasks', n.taskId]);
    }
  }
}
