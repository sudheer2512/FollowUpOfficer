import { Injectable, signal } from '@angular/core';

export interface ConfirmOptions {
  title?: string;
  message: string;
  confirmText?: string;
  cancelText?: string;
  /** Red confirm button for destructive actions. */
  danger?: boolean;
}

export interface PromptOptions extends ConfirmOptions {
  label?: string;
  placeholder?: string;
  value?: string;
  /** Multi-line textarea instead of an input. */
  multiline?: boolean;
  /** Input type for single-line prompts: text, date, datetime-local, number. */
  inputType?: 'text' | 'date' | 'datetime-local' | 'number';
  required?: boolean;
}

export interface DialogRequest {
  kind: 'confirm' | 'prompt';
  options: PromptOptions;
  resolve: (value: string | boolean | null) => void;
}

/**
 * Imperative confirm / prompt dialogs, rendered by <fu-dialog-host> in the root component.
 *   if (await this.dialog.confirm({ message: 'End this series?', danger: true })) { … }
 *   const note = await this.dialog.prompt({ title: 'Reopen task', label: 'Reason', multiline: true, required: true });
 */
@Injectable({ providedIn: 'root' })
export class DialogService {
  readonly current = signal<DialogRequest | null>(null);

  confirm(options: ConfirmOptions | string): Promise<boolean> {
    const opts: PromptOptions = typeof options === 'string' ? { message: options } : options;
    return new Promise<boolean>((resolve) => {
      this.current.set({ kind: 'confirm', options: opts, resolve: (v) => resolve(v === true) });
    });
  }

  /** Resolves with the entered text, or null when cancelled. */
  prompt(options: PromptOptions): Promise<string | null> {
    return new Promise<string | null>((resolve) => {
      this.current.set({ kind: 'prompt', options, resolve: (v) => resolve(typeof v === 'string' ? v : null) });
    });
  }

  /** Called by the host. */
  settle(value: string | boolean | null): void {
    const req = this.current();
    this.current.set(null);
    req?.resolve(value);
  }
}
