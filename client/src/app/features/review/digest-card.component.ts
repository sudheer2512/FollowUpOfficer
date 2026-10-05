import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ExecutiveApi } from '../../core/api/executive.api';
import { Drilldown } from '../../core/models/common.models';
import { AttentionItemDto, DigestDto } from '../../core/models/view.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { addDays, todayDateOnly } from '../../core/util/dates';
import { loader } from '../../core/util/loader';
import { UiTone } from '../../core/util/tone';
import { ScoreBarComponent } from '../../shared/domain/indicators';
import { DateTimePipe } from '../../shared/pipes/format.pipes';
import { DialogService } from '../../shared/ui/dialog.service';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { ToastService } from '../../shared/ui/toast.service';
import { queuedMessage } from './review-format';
import { SummaryPointsComponent } from './summary-points.component';

interface DigestStat {
  key: string;
  label: string;
  value: number;
  /** Tone applied only when the value is non-zero (a zero never needs alarm colours). */
  tone: UiTone;
  link: Drilldown;
}

const TOP_ATTENTION = 5;

/**
 * Daily Digest preview: what the CEO's morning email will say right now (counts, summary, top attention items),
 * with "Send digest now" for executives. Refreshes silently when tasks or attention items change.
 */
@Component({
  selector: 'fu-digest-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, SkeletonComponent, EmptyStateComponent, ScoreBarComponent, SummaryPointsComponent, DateTimePipe],
  template: `
    <section class="card">
      <header class="card-header">
        <fu-icon name="mail" class="muted" />
        <div class="titles">
          <h2>Daily digest</h2>
          @if (digest.value(); as d) {
            <div class="xsmall muted">Preview as of {{ d.asOf | dateTime: 'short' }}</div>
          }
        </div>
        <span class="spacer"></span>
        @if (canSend()) {
          <button type="button" class="btn btn-sm" [disabled]="sending() || !digest.value()" (click)="send()">
            <fu-icon name="send" [size]="14" /> {{ sending() ? 'Sending…' : 'Send digest now' }}
          </button>
        }
      </header>

      @if (digest.loading() && !digest.value()) {
        <div class="card-body"><fu-skeleton variant="lines" [rows]="6" /></div>
      } @else if (digest.error()) {
        <div class="card-body">
          <div class="alert tone-danger" role="alert">
            <fu-icon name="alert-circle" />
            <span class="spacer">{{ digest.error() }}</span>
            <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
          </div>
        </div>
      } @else if (digest.value(); as d) {
        <div class="stats">
          @for (s of stats(); track s.key) {
            <a class="stat" [routerLink]="s.link.route" [queryParams]="s.link.query" [attr.aria-label]="s.label + ': ' + s.value">
              <span [class]="s.value ? 'v num text-' + s.tone : 'v num zero'">{{ s.value }}</span>
              <span class="l">{{ s.label }}</span>
            </a>
          }
        </div>

        @if (d.summary; as sum) {
          <div class="card-body summary">
            @if (sum.headline) {
              <p class="headline">{{ sum.headline }}</p>
            }
            <fu-summary-points [points]="sum.points" [limit]="5" [compact]="true" />
          </div>
        }

        <div class="top">
          <div class="section-title">Top attention items</div>
          @for (a of topAttention(); track a.id) {
            <a class="list-row" [routerLink]="a.task ? ['/tasks', a.task.id] : ['/attention']">
              <span class="main">
                <span class="title truncate">{{ a.title }}</span>
                <span class="meta">{{ a.typeLabel }}@if (a.task) { · <span class="task-number">{{ a.task.number }}</span>}</span>
              </span>
              <fu-score [value]="a.score" />
            </a>
          } @empty {
            <fu-empty icon="check-circle" title="Nothing needs attention" [compact]="true" />
          }
        </div>
      }
    </section>
  `,
  styles: `
    :host { display: block; }
    .titles { display: flex; flex-direction: column; min-width: 0; }
    .stats { display: grid; grid-template-columns: repeat(auto-fill, minmax(96px, 1fr)); border-bottom: 1px solid var(--border); }
    .stat { display: flex; flex-direction: column; gap: 2px; padding: 10px 14px; color: inherit; border-right: 1px solid var(--border); border-bottom: 1px solid var(--border); margin: 0 -1px -1px 0; }
    .stat:hover { background: var(--surface-hover); text-decoration: none; }
    .stat:focus-visible { outline: none; box-shadow: inset 0 0 0 2px var(--accent); }
    .v { font-size: 20px; font-weight: 600; line-height: 1.15; color: var(--text); }
    .v.zero { color: var(--subtle); }
    .l { font-size: 11.5px; color: var(--muted); }
    .summary { border-bottom: 1px solid var(--border); }
    .headline { font-weight: 600; margin-bottom: 8px; }
    .top { padding-top: 12px; }
    .top .section-title { padding: 0 16px; }
    .top .list-row { padding: 8px 16px; }
    .top .meta { margin-top: 0; }
  `,
})
export class DigestCardComponent {
  private readonly api = inject(ExecutiveApi);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);

  /** Show "Send digest now" (executives). */
  readonly canSend = input(false);

  protected readonly digest = loader<DigestDto>();
  protected readonly sending = signal(false);

  protected readonly stats = computed<DigestStat[]>(() => {
    const d = this.digest.value();
    if (!d) return [];
    const yesterday = addDays(todayDateOnly(), -1);
    const tasks = (query: Record<string, string>): Drilldown => ({ route: '/tasks', query });
    return [
      { key: 'open', label: 'Open', value: d.open ?? 0, tone: 'neutral', link: tasks({ open: 'true' }) },
      { key: 'overdue', label: 'Overdue', value: d.overdue ?? 0, tone: 'danger', link: tasks({ overdue: 'true' }) },
      { key: 'critical', label: 'Critical', value: d.critical ?? 0, tone: 'orange', link: tasks({ open: 'true', minCriticality: 'Critical' }) },
      { key: 'attention', label: 'Need attention', value: d.needAttention ?? 0, tone: 'warning', link: { route: '/attention', query: {} } },
      { key: 'today', label: 'Due today', value: d.dueToday ?? 0, tone: 'warning', link: tasks({ bucket: 'DueToday' }) },
      { key: 'missed', label: 'Missed commitments', value: d.missedCommitments ?? 0, tone: 'danger', link: { route: '/commitments', query: { tab: 'missed' } } },
      { key: 'done', label: 'Completed yesterday', value: d.completedYesterday ?? 0, tone: 'good', link: tasks({ completedFrom: yesterday, completedTo: yesterday }) },
    ];
  });

  protected readonly topAttention = computed<AttentionItemDto[]>(() => (this.digest.value()?.topAttention ?? []).slice(0, TOP_ATTENTION));

  constructor() {
    this.reload();
    refreshOn(['attention.changed', 'task.changed'], () => this.reload(true));
  }

  protected reload(silent = false): void {
    this.digest.load(this.api.digestPreview(), { silent });
  }

  protected async send(): Promise<void> {
    const ok = await this.dialog.confirm({
      title: 'Send the daily digest now?',
      message: 'The digest goes to its subscribers immediately, in addition to the scheduled morning run.',
      confirmText: 'Send digest',
    });
    if (!ok) return;
    this.sending.set(true);
    this.api.sendDigest().subscribe({
      next: (r) => {
        this.sending.set(false);
        this.toast.success(queuedMessage(r));
      },
      error: () => this.sending.set(false),
    });
  }
}
