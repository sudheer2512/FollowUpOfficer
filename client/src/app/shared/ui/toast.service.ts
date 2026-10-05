import { Injectable, signal } from '@angular/core';

export type ToastKind = 'success' | 'error' | 'info' | 'warning';

export interface ToastAction {
  label: string;
  run: () => void;
}

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
  title?: string;
  action?: ToastAction;
  timeoutMs: number;
}

/** App-wide toasts, rendered by <fu-toast-host> in the root component. */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private seq = 0;
  private readonly _toasts = signal<Toast[]>([]);
  readonly toasts = this._toasts.asReadonly();
  /** Suppress identical error toasts fired in quick succession (e.g. several failed parallel calls). */
  private lastError = { message: '', at: 0 };

  show(kind: ToastKind, message: string, opts: { title?: string; action?: ToastAction; timeoutMs?: number } = {}): number {
    const id = ++this.seq;
    const timeoutMs = opts.timeoutMs ?? (kind === 'error' ? 7000 : 4000);
    this._toasts.update((list) => [...list.slice(-4), { id, kind, message, title: opts.title, action: opts.action, timeoutMs }]);
    if (timeoutMs > 0) setTimeout(() => this.dismiss(id), timeoutMs);
    return id;
  }

  success(message: string, opts?: { title?: string; action?: ToastAction }): number {
    return this.show('success', message, opts);
  }

  info(message: string, opts?: { title?: string; action?: ToastAction; timeoutMs?: number }): number {
    return this.show('info', message, opts);
  }

  warning(message: string, opts?: { title?: string; action?: ToastAction }): number {
    return this.show('warning', message, opts);
  }

  error(message: string, opts?: { title?: string; action?: ToastAction }): number {
    const now = Date.now();
    if (this.lastError.message === message && now - this.lastError.at < 1500) return -1;
    this.lastError = { message, at: now };
    return this.show('error', message, opts);
  }

  dismiss(id: number): void {
    this._toasts.update((list) => list.filter((t) => t.id !== id));
  }
}
