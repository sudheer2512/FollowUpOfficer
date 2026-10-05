import { Injectable, signal } from '@angular/core';
import { Guid } from '../models/common.models';

export interface DelegateLaunchOptions {
  /** Natural-language command to pre-fill (and parse). */
  text?: string;
  /** Start listening on the microphone immediately. */
  voice?: boolean;
  /** Create the task as a subtask of this task. */
  parentTaskId?: Guid | null;
  parentNumber?: string | null;
  /** Open directly on the recurring-definition form. */
  recurring?: boolean;
  /** Pre-select a project. */
  projectId?: Guid | null;
}

/**
 * Opens the global Delegate dialog (top-bar button, shortcut N, mic button, "Create subtask").
 * The dialog component itself is hosted once in the app shell and reads `request`.
 */
@Injectable({ providedIn: 'root' })
export class DelegateLauncher {
  readonly request = signal<DelegateLaunchOptions | null>(null);

  open(opts: DelegateLaunchOptions = {}): void {
    this.request.set({ ...opts });
  }

  close(): void {
    this.request.set(null);
  }
}
