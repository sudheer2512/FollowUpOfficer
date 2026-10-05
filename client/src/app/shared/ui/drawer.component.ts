import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, afterNextRender, inject, input, output } from '@angular/core';
import { IconComponent } from './icon.component';

/**
 * Right-hand slide-over panel. Render conditionally; Escape / scrim click emit (closed).
 *   @if (selected()) { <fu-drawer [title]="selected()!.subject" (closed)="selected.set(null)"> … </fu-drawer> }
 */
@Component({
  selector: 'fu-drawer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `<div class="scrim" (click)="closed.emit()"></div>
    <aside class="panel" role="dialog" aria-modal="true" [attr.aria-label]="title()" [style.width]="width()" tabindex="-1" (keydown.escape)="closed.emit()">
      <header class="hd">
        <div class="titles">
          <h2 class="truncate">{{ title() }}</h2>
          @if (subtitle()) {
            <div class="sub">{{ subtitle() }}</div>
          }
        </div>
        <ng-content select="[drawer-actions]" />
        <button type="button" class="btn btn-ghost btn-icon btn-sm" aria-label="Close panel" (click)="closed.emit()">
          <fu-icon name="x" />
        </button>
      </header>
      <div class="bd"><ng-content /></div>
      <ng-content select="[drawer-footer]" />
    </aside>`,
  styles: `
    .scrim { position: fixed; inset: 0; background: var(--scrim); z-index: 70; animation: fade .12s ease-out; }
    .panel { position: fixed; top: 0; right: 0; bottom: 0; max-width: 100vw; background: var(--surface); border-left: 1px solid var(--border); box-shadow: var(--shadow-lg); z-index: 71; display: flex; flex-direction: column; animation: slide .16s ease-out; outline: none; }
    .hd { display: flex; align-items: center; gap: 10px; padding: 14px 16px; border-bottom: 1px solid var(--border); }
    .titles { flex: 1; min-width: 0; }
    .hd h2 { font-size: 15px; }
    .sub { color: var(--muted); font-size: 12.5px; margin-top: 2px; }
    .bd { flex: 1 1 auto; overflow-y: auto; padding: 16px; }
    @keyframes fade { from { opacity: 0; } }
    @keyframes slide { from { transform: translateX(24px); opacity: .6; } }
  `,
})
export class DrawerComponent implements OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly previouslyFocused = (typeof document !== 'undefined' ? document.activeElement : null) as HTMLElement | null;

  readonly title = input('');
  readonly subtitle = input('');
  readonly width = input('min(560px, 100vw)');
  readonly closed = output<void>();

  constructor() {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('.panel')?.focus());
  }

  ngOnDestroy(): void {
    this.previouslyFocused?.focus?.();
  }
}
