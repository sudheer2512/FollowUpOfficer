import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MessagesApi } from '../../../core/api/messages.api';
import { Guid, PagedResult } from '../../../core/models/common.models';
import { MessageDto } from '../../../core/models/view.models';
import { loader } from '../../../core/util/loader';
import { AgoPipe, DateTimePipe, HumanizePipe } from '../../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { SkeletonComponent } from '../../../shared/ui/skeleton.component';
import { ToastService } from '../../../shared/ui/toast.service';

const PAGE_SIZE = 20;

/** Imported emails / Teams messages whose processing failed, with a one-click reprocess. */
@Component({
  selector: 'fu-failed-messages',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, AgoPipe, DateTimePipe, HumanizePipe, EmptyStateComponent, IconComponent, SkeletonComponent],
  template: `
    <section class="card" aria-labelledby="mon-failed-title">
      <header class="card-header">
        <fu-icon name="mail" class="muted" />
        <h2 id="mon-failed-title">Failed messages</h2>
        <span class="count-pill" [class.danger]="total() > 0">{{ total() }}</span>
        <span class="spacer"></span>
        <button type="button" class="btn btn-ghost btn-icon btn-sm" aria-label="Refresh failed messages" (click)="reload()">
          <fu-icon name="refresh" />
        </button>
      </header>
      <div class="card-body flush">
        @if (data.loading() && !data.value()) {
          <fu-skeleton variant="table" [rows]="3" />
        } @else if (data.error()) {
          <div class="card-body">
            <div class="alert tone-danger" role="alert">
              <fu-icon name="alert-circle" /> <span class="spacer">{{ data.error() }}</span>
              <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
            </div>
          </div>
        } @else if (!rows().length) {
          <fu-empty icon="check-circle" title="No failed messages" message="Every imported email and Teams message was processed." [compact]="true" />
        } @else {
          <div class="list">
            @for (m of rows(); track m.id) {
              <div class="list-row">
                <fu-icon [name]="m.channel === 'Email' ? 'mail' : m.channel === 'TeamsChannel' ? 'hash' : 'message'" class="muted" />
                <div class="main">
                  <div class="title truncate" [attr.title]="m.subject || m.body">{{ m.subject || m.body || '(no subject)' }}</div>
                  <div class="meta">
                    <span>{{ m.senderName || m.senderAddress || 'Unknown sender' }}</span>
                    <span>{{ m.channel | humanize }}</span>
                    <span [attr.title]="m.sentAt | dateTime">{{ m.sentAt | ago }}</span>
                    @if (m.analysisSummary) {
                      <span class="text-danger truncate" [attr.title]="m.analysisSummary">{{ m.analysisSummary }}</span>
                    }
                  </div>
                </div>
                <a class="btn btn-ghost btn-xs" [routerLink]="['/communications']" [queryParams]="{ message: m.id }">Open</a>
                <button type="button" class="btn btn-xs" [disabled]="busy() === m.id" (click)="reprocess(m.id)">
                  <fu-icon name="rotate" [size]="12" /> Reprocess
                </button>
              </div>
            }
          </div>
          @if (total() > rows().length) {
            <div class="card-footer small muted">
              Showing the latest {{ rows().length }} of {{ total() }}.
              <a [routerLink]="['/communications']" [queryParams]="{ tab: 'failed' }">See all in Communications</a>
            </div>
          }
        }
      </div>
    </section>
  `,
})
export class FailedMessagesComponent {
  private readonly api = inject(MessagesApi);
  private readonly toast = inject(ToastService);

  protected readonly data = loader<PagedResult<MessageDto>>();
  protected readonly rows = computed(() => this.data.value()?.items ?? []);
  protected readonly total = computed(() => this.data.value()?.total ?? this.rows().length);
  protected readonly busy = signal<Guid | null>(null);

  constructor() {
    this.reload();
  }

  reload(silent = false): void {
    this.data.load(this.api.list({ status: 'Failed', pageSize: PAGE_SIZE }), { silent });
  }

  protected reprocess(id: Guid): void {
    this.busy.set(id);
    this.api.reprocess(id).subscribe({
      next: () => {
        this.busy.set(null);
        this.toast.success('Queued for reprocessing');
        this.reload(true);
      },
      error: () => this.busy.set(null),
    });
  }
}
