import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, debounceTime, map, of, switchMap, tap } from 'rxjs';
import { MessagesApi } from '../../../../core/api/messages.api';
import { MessageDto } from '../../../../core/models/view.models';
import { AgoPipe, HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { channelIcon } from '../task-detail.util';
import { TaskDialogBase } from './task-dialog-base';

/** Link a message manually: search GET /api/messages, pick one → POST /api/tasks/{id}/links. */
@Component({
  selector: 'fu-link-message-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ModalComponent, IconComponent, AgoPipe, HumanizePipe],
  template: `
    <fu-modal title="Link a message" [subtitle]="'Attach an email or Teams message to ' + detail().summary.number" size="lg" (closed)="closed.emit()">
      <div class="stack">
        <div class="field">
          <label for="lm-search">Search messages</label>
          <div class="input-with-icon">
            <fu-icon name="search" [size]="15" />
            <input
              id="lm-search"
              class="input"
              type="search"
              autocomplete="off"
              placeholder="Subject, sender or text — leave empty for recent messages"
              [value]="query()"
              (input)="query.set($any($event.target).value)"
            />
          </div>
        </div>
        <div class="results card" role="listbox" aria-label="Messages">
          @if (searching() && !results().length) {
            <div class="small muted pad">Searching…</div>
          }
          @for (m of results(); track m.id) {
            <button
              type="button"
              role="option"
              class="list-row"
              [class.sel]="selected()?.id === m.id"
              [attr.aria-selected]="selected()?.id === m.id"
              [disabled]="alreadyLinked(m)"
              (click)="selected.set(m)"
            >
              <span class="ch"><fu-icon [name]="icon(m.channel)" [size]="15" /></span>
              <span class="main">
                <span class="title truncate">{{ m.subject || m.body }}</span>
                <span class="meta">
                  <span>{{ m.senderName ?? m.senderAddress ?? 'Unknown sender' }}</span>
                  <span>{{ m.channel | humanize }}</span>
                  <span>{{ m.sentAt | ago }}</span>
                  @if (alreadyLinked(m)) {
                    <span class="chip sm tone-good">Already linked</span>
                  } @else if (m.links.length) {
                    <span class="chip sm">Linked to {{ m.links[0].number }}{{ m.links.length > 1 ? ' +' + (m.links.length - 1) : '' }}</span>
                  }
                </span>
              </span>
              @if (selected()?.id === m.id) {
                <fu-icon name="check-circle" [size]="16" class="text-accent" />
              }
            </button>
          } @empty {
            @if (!searching()) {
              <div class="small muted pad">No messages found.</div>
            }
          }
        </div>
      </div>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="button" class="btn btn-primary" [disabled]="busy() || !selected()" (click)="submit()">Link message</button>
      </ng-container>
    </fu-modal>
  `,
  styles: `
    .results { max-height: 340px; overflow-y: auto; box-shadow: none; }
    .results .list-row.sel { background: var(--accent-soft); }
    .results .list-row:disabled { opacity: 0.6; cursor: default; }
    .ch { width: 28px; height: 28px; border-radius: 8px; background: var(--surface-2); border: 1px solid var(--border); display: inline-flex; align-items: center; justify-content: center; color: var(--muted); flex: none; }
    .pad { padding: 12px 16px; }
  `,
})
export class LinkMessageDialogComponent extends TaskDialogBase {
  private readonly messages = inject(MessagesApi);

  protected readonly query = signal('');
  protected readonly selected = signal<MessageDto | null>(null);
  protected readonly searching = signal(true);

  /** Recent messages when the box is empty; otherwise a debounced text search. */
  protected readonly results = toSignal(
    toObservable(this.query).pipe(
      debounceTime(300),
      map((q) => q.trim()),
      tap(() => this.searching.set(true)),
      switchMap((text) =>
        this.messages.list({ text: text || null, pageSize: 10 }).pipe(
          map((page) => (page?.items ?? []).map((m) => ({ ...m, links: m.links ?? [] }))),
          catchError(() => of<MessageDto[]>([])),
        ),
      ),
      tap(() => this.searching.set(false)),
    ),
    { initialValue: [] as MessageDto[] },
  );

  private readonly linkedIds = computed(() => new Set(this.detail().messages.map((m) => m.messageId)));

  protected alreadyLinked(m: MessageDto): boolean {
    return this.linkedIds().has(m.id) || m.links.some((l) => l.taskId === this.detail().summary.id && l.status !== 'Rejected');
  }

  protected icon(channel: string): string {
    return channelIcon(channel);
  }

  protected submit(): void {
    const m = this.selected();
    if (!m) return;
    this.run(this.api.linkMessage(this.taskId(), { messageId: m.id }), 'Message linked to the task');
  }
}
