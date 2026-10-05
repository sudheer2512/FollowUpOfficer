import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { AttentionItemDto } from '../../core/models/view.models';

/**
 * STUB — public API is fixed; the executive-views implementation replaces the body.
 * Ranked attention items with inline actions (open, remind, escalate, approve/reopen, extension decision,
 * AI suggestion accept/dismiss, resolve, dismiss, snooze).
 *   <fu-attention-list [items]="items" [compact]="true" [limit]="6" (changed)="reload(true)" />
 */
@Component({
  selector: 'fu-attention-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `@for (a of items(); track a.id) {
    <div class="list-row">{{ a.title }}</div>
  }`,
})
export class AttentionListComponent {
  readonly items = input<AttentionItemDto[]>([]);
  /** Fewer details and a single row of actions (dashboard cards). */
  readonly compact = input(false);
  readonly limit = input<number | null>(null);
  /** Emitted after an action changed server state, so the host can reload. */
  readonly changed = output<void>();
}
