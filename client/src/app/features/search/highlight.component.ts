import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { highlightSegments } from './search-text';

/**
 * Renders text with the query terms wrapped in <mark>. Segments are bound as text (never innerHTML), so server
 * strings cannot inject markup.
 *   <fu-highlight [text]="hit.snippet" [terms]="terms()" />
 */
@Component({
  selector: 'fu-highlight',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `@for (s of segments(); track $index) {@if (s.match) {<mark>{{ s.text }}</mark>} @else {<span>{{ s.text }}</span>}}`,
  styles: `mark { background: color-mix(in srgb, var(--warning) 28%, transparent); color: inherit; border-radius: 2px; padding: 0 1px; }`,
})
export class HighlightComponent {
  readonly text = input<string | null | undefined>('');
  readonly terms = input<readonly string[]>([]);
  protected readonly segments = computed(() => highlightSegments(this.text(), this.terms()));
}
