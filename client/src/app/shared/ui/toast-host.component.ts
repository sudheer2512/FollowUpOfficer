import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { IconComponent } from './icon.component';
import { Toast, ToastService } from './toast.service';

/** Renders ToastService toasts (bottom-right; polite live region). Mounted once in the root component. */
@Component({
  selector: 'fu-toast-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `<div class="stack-toasts" aria-live="polite" aria-relevant="additions">
    @for (t of toasts.toasts(); track t.id) {
      <div class="toast" [class]="'toast k-' + t.kind" [attr.role]="t.kind === 'error' ? 'alert' : 'status'">
        <fu-icon [name]="icon(t)" [size]="18" class="ic" />
        <div class="msg">
          @if (t.title) {
            <div class="ttl">{{ t.title }}</div>
          }
          <div>{{ t.message }}</div>
        </div>
        @if (t.action) {
          <button type="button" class="btn btn-sm btn-ghost act" (click)="t.action.run(); toasts.dismiss(t.id)">{{ t.action.label }}</button>
        }
        <button type="button" class="btn btn-ghost btn-icon btn-xs" aria-label="Dismiss notification" (click)="toasts.dismiss(t.id)">
          <fu-icon name="x" [size]="14" />
        </button>
      </div>
    }
  </div>`,
  styles: `
    .stack-toasts { position: fixed; right: 16px; bottom: 16px; z-index: 100; display: flex; flex-direction: column; gap: 8px; width: min(380px, calc(100vw - 32px)); }
    .toast { display: flex; align-items: flex-start; gap: 10px; padding: 11px 12px; background: var(--surface); border: 1px solid var(--border); border-left-width: 3px; border-radius: var(--radius-sm); box-shadow: var(--shadow-lg); font-size: 13.5px; animation: in .16s ease-out; }
    .toast.k-success { border-left-color: var(--good); } .toast.k-success .ic { color: var(--good); }
    .toast.k-error { border-left-color: var(--danger); } .toast.k-error .ic { color: var(--danger); }
    .toast.k-warning { border-left-color: var(--warning); } .toast.k-warning .ic { color: var(--warning); }
    .toast.k-info { border-left-color: var(--accent); } .toast.k-info .ic { color: var(--accent); }
    .msg { flex: 1; min-width: 0; padding-top: 1px; }
    .ttl { font-weight: 600; margin-bottom: 2px; }
    .act { margin-top: -3px; }
    @keyframes in { from { opacity: 0; transform: translateY(8px); } }
  `,
})
export class ToastHostComponent {
  protected readonly toasts = inject(ToastService);

  protected icon(t: Toast): string {
    return t.kind === 'success' ? 'check-circle' : t.kind === 'error' ? 'alert-circle' : t.kind === 'warning' ? 'alert-triangle' : 'bell';
  }
}
