import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MessagesApi } from '../../../core/api/messages.api';
import { LinkStatus, MessageCategory } from '../../../core/models/enums';
import { LinkedMessageDto, TaskDetail } from '../../../core/models/task.models';
import { AgoPipe, ConfidencePipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { DialogRequest } from './task-actions';
import { channelIcon } from './task-detail.util';

const STATUS_ORDER: Record<LinkStatus, number> = { Suggested: 0, Linked: 1, Rejected: 2 };

function categoryTone(c: MessageCategory): string {
  switch (c) {
    case 'Completion':
    case 'Approval':
      return 'good';
    case 'Blocker':
    case 'Escalation':
    case 'Rejection':
      return 'danger';
    case 'Delay':
    case 'DeadlineChange':
      return 'orange';
    case 'Commitment':
    case 'Clarification':
    case 'RequestForHelp':
      return 'warning';
    case 'ProgressUpdate':
      return 'info';
    default:
      return 'neutral';
  }
}

/**
 * Emails and Teams messages linked to the task: how each was matched (method + confidence + explanation), its
 * category, and — for AI suggestions below the auto-link threshold — Confirm / Reject. Messages can be linked manually.
 */
@Component({
  selector: 'fu-communications-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, EmptyStateComponent, HumanizePipe, AgoPipe, DateTimePipe, ConfidencePipe],
  template: `
    <section class="card">
      <header class="card-header">
        <h2>Communications</h2>
        <span class="count-pill">{{ linkedCount() }}</span>
        @if (suggestedCount()) {
          <span class="chip sm tone-accent">{{ suggestedCount() }} to review</span>
        }
        <span class="spacer"></span>
        @if (rejectedCount()) {
          <label class="switch small">
            <input type="checkbox" [checked]="showRejected()" (change)="showRejected.set($any($event.target).checked)" />
            Show rejected ({{ rejectedCount() }})
          </label>
        }
        @if (canLink()) {
          <button type="button" class="btn btn-sm" (click)="open.emit({ kind: 'link-message' })"><fu-icon name="link" [size]="14" /> Link a message</button>
        }
      </header>
      <div class="card-body flush list">
        @for (m of visible(); track m.linkId) {
          <div class="list-row msg" [class.suggested]="m.status === 'Suggested'" [class.rejected]="m.status === 'Rejected'">
            <span class="ch" [attr.title]="m.channel | humanize"><fu-icon [name]="icon(m.channel)" [size]="15" /></span>
            <div class="main">
              <div class="row wrap gap-4">
                <a class="title" routerLink="/communications" [queryParams]="{ message: m.messageId }">{{ m.subject || (m.channel | humanize) + ' message' }}</a>
                @if (m.status === 'Suggested') {
                  <span class="badge tone-accent">Suggested link</span>
                } @else if (m.status === 'Rejected') {
                  <span class="badge">Rejected</span>
                }
              </div>
              <div class="preview small clamp-2">{{ m.preview }}</div>
              <div class="meta">
                <span>{{ m.senderName ?? 'Unknown sender' }}</span>
                <time [attr.datetime]="m.sentAt" [attr.title]="m.sentAt | dateTime">{{ m.sentAt | ago }}</time>
                <span class="chip sm tone-{{ tone(m.category) }}">{{ m.category | humanize }}</span>
                <span class="nowrap" title="How the message was matched to this task">
                  <fu-icon name="link" [size]="11" style="display:inline-flex" /> {{ m.method | humanize }} · {{ m.confidence | confidence }}
                </span>
                @for (s of signalsOf(m); track s) {
                  <span class="chip sm outline">{{ s | humanize }}</span>
                }
              </div>
              @if (m.explanation) {
                <div class="why xsmall">{{ m.explanation }}</div>
              }
            </div>
            <div class="acts">
              @if (m.status === 'Suggested' && canLink()) {
                <button type="button" class="btn btn-xs btn-primary" [disabled]="busyId() === m.linkId" (click)="decide(m, true)"><fu-icon name="check" [size]="12" /> Confirm</button>
                <button type="button" class="btn btn-xs btn-ghost" [disabled]="busyId() === m.linkId" (click)="decide(m, false)">Reject</button>
              }
              @if (m.webUrl) {
                <a class="btn btn-xs btn-ghost" [href]="m.webUrl" target="_blank" rel="noopener noreferrer" [attr.aria-label]="'Open in ' + (m.channel === 'Email' ? 'Outlook' : 'Teams')">
                  <fu-icon name="external" [size]="12" /> {{ m.channel === 'Email' ? 'Outlook' : 'Teams' }}
                </a>
              }
            </div>
          </div>
        } @empty {
          <fu-empty icon="messages" title="No linked messages" message="Replies to reminders and emails or Teams messages that mention this task are linked here automatically." />
        }
      </div>
    </section>
  `,
  styles: `
    .msg { align-items: flex-start; }
    .msg.suggested { background: color-mix(in srgb, var(--accent-soft) 45%, transparent); }
    .msg.rejected { opacity: 0.6; }
    .ch { width: 30px; height: 30px; border-radius: 8px; background: var(--surface-2); border: 1px solid var(--border); display: inline-flex; align-items: center; justify-content: center; color: var(--muted); flex: none; }
    .preview { color: var(--text-2); margin-top: 2px; }
    .why { color: var(--muted); font-style: italic; margin-top: 4px; }
    .acts { display: flex; flex-direction: column; align-items: flex-end; gap: 6px; flex: none; }
    @media (max-width: 640px) { .msg { flex-wrap: wrap; } .acts { flex-direction: row; width: 100%; justify-content: flex-end; } }
  `,
})
export class CommunicationsTabComponent {
  private readonly messages = inject(MessagesApi);
  private readonly toast = inject(ToastService);

  readonly detail = input.required<TaskDetail>();
  /** A link changed (confirm / reject return no task), so the page refreshes. */
  readonly reload = output<void>();
  readonly open = output<DialogRequest>();

  protected readonly showRejected = signal(false);
  protected readonly busyId = signal<string | null>(null);

  private readonly sorted = computed(() =>
    [...this.detail().messages].sort((a, b) => STATUS_ORDER[a.status] - STATUS_ORDER[b.status] || b.sentAt.localeCompare(a.sentAt)),
  );
  protected readonly visible = computed(() => (this.showRejected() ? this.sorted() : this.sorted().filter((m) => m.status !== 'Rejected')));
  protected readonly linkedCount = computed(() => this.detail().messages.filter((m) => m.status === 'Linked').length);
  protected readonly suggestedCount = computed(() => this.detail().messages.filter((m) => m.status === 'Suggested').length);
  protected readonly rejectedCount = computed(() => this.detail().messages.filter((m) => m.status === 'Rejected').length);
  protected readonly canLink = computed(() => {
    const p = this.detail().permissions;
    return p.canManage || p.canUpdate;
  });

  protected icon(channel: string): string {
    return channelIcon(channel);
  }

  protected tone(c: MessageCategory): string {
    return categoryTone(c);
  }

  /** Signals ("deadline", "blocker"…) are not part of LinkedMessageDto yet; render them if the server adds them. */
  protected signalsOf(m: LinkedMessageDto): string[] {
    const signals = (m as LinkedMessageDto & { signals?: string[] | null }).signals;
    return Array.isArray(signals) ? signals : [];
  }

  protected decide(m: LinkedMessageDto, confirm: boolean): void {
    this.busyId.set(m.linkId);
    const command = confirm ? this.messages.confirmLink(m.linkId) : this.messages.rejectLink(m.linkId);
    command.subscribe({
      next: () => {
        this.busyId.set(null);
        this.toast.success(confirm ? 'Message linked to the task' : 'Suggestion rejected');
        this.reload.emit();
      },
      error: () => this.busyId.set(null),
    });
  }
}
