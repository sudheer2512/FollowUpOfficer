import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, afterNextRender, inject, input, output } from '@angular/core';
import { IconComponent } from './icon.component';

let modalSeq = 0;

/**
 * Templated modal dialog. Render it conditionally and close by handling (closed):
 *
 *   @if (showReassign()) {
 *     <fu-modal title="Reassign" (closed)="showReassign.set(false)">
 *       …form…
 *       <ng-container modal-footer>
 *         <button class="btn" (click)="showReassign.set(false)">Cancel</button>
 *         <button class="btn btn-primary" (click)="save()">Reassign</button>
 *       </ng-container>
 *     </fu-modal>
 *   }
 *
 * Escape and a scrim click emit (closed). Focus moves into the dialog on open and is restored on close.
 */
@Component({
  selector: 'fu-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `<div class="scrim" (click)="onScrim($event)">
    <div
      class="dialog"
      [class]="'dialog size-' + size()"
      role="dialog"
      aria-modal="true"
      [attr.aria-labelledby]="titleId"
      tabindex="-1"
      (keydown)="onKey($event)"
    >
      <header class="hd">
        <div class="titles">
          <h2 [id]="titleId">{{ title() }}</h2>
          @if (subtitle()) {
            <div class="sub">{{ subtitle() }}</div>
          }
        </div>
        <button type="button" class="btn btn-ghost btn-icon btn-sm" aria-label="Close dialog" (click)="closed.emit()">
          <fu-icon name="x" />
        </button>
      </header>
      <div class="bd"><ng-content /></div>
      <footer class="ft"><ng-content select="[modal-footer]" /></footer>
    </div>
  </div>`,
  styles: `
    .scrim { position: fixed; inset: 0; background: var(--scrim); z-index: 80; display: flex; align-items: flex-start; justify-content: center; padding: 6vh 16px 16px; overflow-y: auto; animation: fade .12s ease-out; }
    .dialog { background: var(--surface); border: 1px solid var(--border); border-radius: 12px; box-shadow: var(--shadow-lg); width: 100%; max-width: 520px; display: flex; flex-direction: column; max-height: 88vh; animation: rise .14s ease-out; outline: none; }
    .dialog.size-sm { max-width: 420px; }
    .dialog.size-lg { max-width: 760px; }
    .dialog.size-xl { max-width: 1040px; }
    .hd { display: flex; align-items: flex-start; gap: 12px; padding: 16px 18px 12px; border-bottom: 1px solid var(--border); }
    .titles { flex: 1; min-width: 0; }
    .hd h2 { font-size: 16px; }
    .sub { color: var(--muted); font-size: 12.5px; margin-top: 2px; }
    .bd { padding: 16px 18px; overflow-y: auto; flex: 1 1 auto; }
    .ft { display: flex; justify-content: flex-end; gap: 8px; padding: 12px 18px; border-top: 1px solid var(--border); flex-wrap: wrap; }
    .ft:empty { display: none; }
    @keyframes fade { from { opacity: 0; } }
    @keyframes rise { from { opacity: 0; transform: translateY(8px) scale(.99); } }
    @media (max-width: 600px) { .scrim { padding: 0; align-items: stretch; } .dialog { max-width: none !important; max-height: none; border-radius: 0; } }
  `,
})
export class ModalComponent implements OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly previouslyFocused = (typeof document !== 'undefined' ? document.activeElement : null) as HTMLElement | null;

  readonly title = input('');
  readonly subtitle = input('');
  readonly size = input<'sm' | 'md' | 'lg' | 'xl'>('md');
  /** Close when the scrim is clicked (disable for long forms). */
  readonly dismissOnScrim = input(true);
  readonly closed = output<void>();

  protected readonly titleId = `fu-modal-title-${++modalSeq}`;

  constructor() {
    afterNextRender(() => {
      const root = this.host.nativeElement.querySelector<HTMLElement>('.dialog');
      const first = root?.querySelector<HTMLElement>(
        '[autofocus], .bd input:not([type=hidden]):not([disabled]), .bd select, .bd textarea, .bd button',
      );
      (first ?? root)?.focus();
    });
  }

  ngOnDestroy(): void {
    this.previouslyFocused?.focus?.();
  }

  protected onScrim(e: MouseEvent): void {
    if (e.target === e.currentTarget && this.dismissOnScrim()) this.closed.emit();
  }

  protected onKey(e: KeyboardEvent): void {
    if (e.key === 'Escape') {
      e.stopPropagation();
      this.closed.emit();
      return;
    }
    if (e.key !== 'Tab') return;
    const root = e.currentTarget as HTMLElement;
    const focusables = Array.from(
      root.querySelectorAll<HTMLElement>('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"])'),
    ).filter((el) => el.offsetParent !== null);
    if (!focusables.length) return;
    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }
}
