import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TaskDetail } from '../../../core/models/task.models';
import { DropdownComponent } from '../../../shared/ui/dropdown.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { QuickAction, availableActions } from './task-actions';

/** Buttons shown inline on wide screens; on narrow screens only the first two stay inline. */
const INLINE_COUNT = 5;
const INLINE_COUNT_NARROW = 2;

/**
 * Quick-action bar (spec §57). Only actions the server's `permissions` allow are shown; the rest of the list lives in
 * a "More" menu. Emits the chosen action — the page opens its dialog or runs it.
 */
@Component({
  selector: 'fu-quick-actions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, DropdownComponent],
  template: `
    @if (actions().length) {
      <div class="bar" role="toolbar" aria-label="Quick actions">
        @for (a of inline(); track a.key; let i = $index) {
          <button
            type="button"
            class="btn btn-sm"
            [class.btn-primary]="a.emphasis === 'primary'"
            [class.wide-only]="i >= narrowCount"
            [disabled]="busy()"
            (click)="action.emit(a)"
          >
            <fu-icon [name]="a.icon" [size]="15" /> {{ a.label }}
          </button>
        }
        @if (actions().length > narrowCount) {
          <fu-dropdown label="More" icon="more" align="right" buttonClass="btn btn-sm" [class.narrow-only]="!overflow().length">
            @for (a of inline(); track a.key; let i = $index) {
              @if (i >= narrowCount) {
                <button type="button" role="menuitem" class="menu-item narrow-only" [disabled]="busy()" (click)="action.emit(a)">
                  <fu-icon [name]="a.icon" [size]="15" /> {{ a.label }}
                </button>
              }
            }
            @for (a of overflow(); track a.key) {
              <button type="button" role="menuitem" class="menu-item" [disabled]="busy()" (click)="action.emit(a)">
                <fu-icon [name]="a.icon" [size]="15" /> {{ a.label }}
              </button>
            }
          </fu-dropdown>
        }
      </div>
    }
  `,
  styles: `
    :host { display: block; margin-bottom: 16px; }
    .bar { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; padding: 8px; background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); }
    .narrow-only { display: none; }
    @media (max-width: 900px) {
      .wide-only { display: none; }
      .narrow-only { display: flex; }
      fu-dropdown.narrow-only { display: inline-flex; }
    }
  `,
})
export class QuickActionsComponent {
  readonly detail = input.required<TaskDetail>();
  readonly busy = input(false);
  readonly action = output<QuickAction>();

  protected readonly narrowCount = INLINE_COUNT_NARROW;
  protected readonly actions = computed(() => availableActions(this.detail()));
  protected readonly inline = computed(() => this.actions().slice(0, INLINE_COUNT));
  protected readonly overflow = computed(() => this.actions().slice(INLINE_COUNT));
}
