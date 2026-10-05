import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { Guid } from '../../core/models/common.models';
import { LinkedTaskRef, MessageDto } from '../../core/models/view.models';
import { AgoPipe, ConfidencePipe, DateTimePipe, HumanizePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { LinkSuggestionComponent } from './link-suggestion.component';
import { categoryTone, channelIcon, messageHeadline, messagePreview, senderLabel, suggestedLinks } from './message-ui';

export interface LinkDecision {
  message: MessageDto;
  link: LinkedTaskRef;
  action: 'confirm' | 'reject';
}

/**
 * Low-confidence message → task suggestions awaiting a human decision: one card per message, its suggested links
 * underneath with Confirm / Reject. The parent performs the API calls (it also owns the tab count).
 */
@Component({
  selector: 'fu-review-queue',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LinkSuggestionComponent, EmptyStateComponent, SkeletonComponent, IconComponent, AgoPipe, DateTimePipe, HumanizePipe, ConfidencePipe],
  template: `
    @if (loading() && !messages().length) {
      <fu-skeleton variant="cards" [rows]="3" />
    } @else if (error() && !messages().length) {
      <div class="alert tone-danger" role="alert">
        <fu-icon name="alert-circle" />
        <span class="spacer">{{ error() }}</span>
        <button type="button" class="btn btn-sm" (click)="retry.emit()">Retry</button>
      </div>
    } @else {
      <div class="stack-lg">
        @for (m of messages(); track m.id) {
          <section class="card">
            <header class="card-header rq-head">
              <span class="ch-icon" [attr.title]="m.channel | humanize"><fu-icon [name]="icon(m.channel)" [size]="15" /></span>
              <div class="who">
                <button type="button" class="btn-link subj truncate" (click)="open.emit(m.id)" [attr.aria-label]="'Open message: ' + headline(m)">{{ headline(m) }}</button>
                <div class="xsmall muted">
                  {{ sender(m) }} · {{ m.channel | humanize }} · <span [attr.title]="m.sentAt | dateTime">{{ m.sentAt | ago }}</span>
                </div>
              </div>
              @if (m.category && m.category !== 'Unclassified') {
                <span class="chip sm tone-{{ catTone(m.category) }}">
                  {{ m.category | humanize }}
                  @if (m.categoryConfidence !== null && m.categoryConfidence !== undefined) {
                    · {{ m.categoryConfidence | confidence }}
                  }
                </span>
              }
            </header>
            <div class="card-body stack">
              <p class="prev clamp-2">{{ preview(m) }}</p>
              @if (m.analysisSummary) {
                <div class="summary small"><fu-icon name="sparkles" [size]="14" /> <span>{{ m.analysisSummary }}</span></div>
              }
              <div class="section-title mt-4">Suggested task{{ suggestions(m).length === 1 ? '' : 's' }}</div>
              <div class="sugs">
                @for (l of suggestions(m); track l.linkId) {
                  <fu-link-suggestion
                    [link]="l"
                    [busy]="busyLinks().has(l.linkId)"
                    (confirmed)="decide.emit({ message: m, link: $event, action: 'confirm' })"
                    (rejected)="decide.emit({ message: m, link: $event, action: 'reject' })"
                  />
                }
              </div>
            </div>
          </section>
        } @empty {
          <div class="card">
            <fu-empty icon="check-circle" title="Review queue is clear" message="Messages the AI could not link with confidence appear here for a quick decision." />
          </div>
        }
      </div>
    }
  `,
  styles: `
    :host { display: block; }
    .rq-head { align-items: flex-start; }
    .ch-icon { width: 28px; height: 28px; border-radius: 8px; background: var(--surface-2); border: 1px solid var(--border); display: inline-flex; align-items: center; justify-content: center; color: var(--muted); flex: none; }
    .who { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 1px; }
    .subj { font-weight: 600; font-size: 14px; text-align: left; max-width: 100%; color: var(--text); cursor: pointer; }
    .prev { margin: 0; color: var(--text-2); font-size: 13px; }
    .summary { display: flex; gap: 6px; align-items: flex-start; color: var(--text-2); background: var(--accent-soft); border-radius: var(--radius-sm); padding: 6px 10px; }
    .summary fu-icon { color: var(--accent-text); margin-top: 2px; flex: none; }
    .sugs { display: grid; gap: 8px; }
  `,
})
export class ReviewQueueComponent {
  /** Messages that still have at least one suggested link. */
  readonly messages = input<MessageDto[]>([]);
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  /** Link ids with a decision in flight. */
  readonly busyLinks = input<ReadonlySet<Guid>>(new Set());
  readonly open = output<Guid>();
  readonly decide = output<LinkDecision>();
  readonly retry = output<void>();

  protected readonly icon = channelIcon;
  protected readonly headline = messageHeadline;
  protected readonly preview = messagePreview;
  protected readonly sender = senderLabel;
  protected readonly catTone = categoryTone;
  protected readonly suggestions = suggestedLinks;
}
