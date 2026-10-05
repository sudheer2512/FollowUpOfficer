import { ChangeDetectionStrategy, Component, ElementRef, inject, input, signal } from '@angular/core';
import { IconComponent } from './icon.component';

/**
 * Button + popup menu. Project `.menu-item` buttons / links; any click inside closes the menu.
 *   <fu-dropdown label="More" icon="more" align="right">
 *     <button class="menu-item" (click)="pause()"><fu-icon name="pause" /> Pause</button>
 *   </fu-dropdown>
 */
@Component({
  selector: 'fu-dropdown',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `<button
      type="button"
      [class]="buttonClass()"
      [attr.aria-label]="ariaLabel() || label() || null"
      aria-haspopup="menu"
      [attr.aria-expanded]="open()"
      (click)="toggle()"
    >
      @if (icon()) {
        <fu-icon [name]="icon()" />
      }
      @if (label()) {
        <span>{{ label() }}</span>
      }
      @if (caret()) {
        <fu-icon name="chevron-down" [size]="14" />
      }
    </button>
    @if (open()) {
      <div class="menu" role="menu" [style.right]="align() === 'right' ? '0' : null" [style.left]="align() === 'left' ? '0' : null" (click)="open.set(false)" (keydown.escape)="open.set(false)">
        <ng-content />
      </div>
    }`,
  styles: `:host { position: relative; display: inline-flex; } .menu { top: calc(100% + 4px); }`,
  host: { '(document:click)': 'onDocClick($event)' },
})
export class DropdownComponent {
  private readonly el = inject<ElementRef<HTMLElement>>(ElementRef);
  readonly label = input('');
  readonly icon = input('');
  readonly ariaLabel = input('');
  readonly align = input<'left' | 'right'>('right');
  readonly caret = input(true);
  readonly buttonClass = input('btn');
  protected readonly open = signal(false);

  protected toggle(): void {
    this.open.update((v) => !v);
  }

  protected onDocClick(e: MouseEvent): void {
    if (this.open() && !this.el.nativeElement.contains(e.target as Node)) this.open.set(false);
  }
}
