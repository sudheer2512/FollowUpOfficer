import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { IconComponent } from './icon.component';

/**
 * Friendly empty state. Project extra content (e.g. a button) inside.
 *   <fu-empty icon="check-circle" title="Nothing needs you" message="All caught up." />
 */
@Component({
  selector: 'fu-empty',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `<div class="empty" [class.compact]="compact()">
    <div class="empty-icon"><fu-icon [name]="icon()" [size]="20" /></div>
    @if (title()) {
      <div class="empty-title">{{ title() }}</div>
    }
    @if (message()) {
      <div class="small">{{ message() }}</div>
    }
    <ng-content />
  </div>`,
})
export class EmptyStateComponent {
  readonly icon = input<string>('inbox');
  readonly title = input<string>('');
  readonly message = input<string>('');
  readonly compact = input(false);
}
