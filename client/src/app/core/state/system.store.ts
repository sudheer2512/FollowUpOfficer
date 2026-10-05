import { Injectable, computed, inject, signal } from '@angular/core';
import { LookupsApi } from '../api/lookups.api';
import { SystemInfo } from '../models/admin.models';

/** GET /api/system/info — organisation name, integration mode (Emulator / Graph…) and AI provider. */
@Injectable({ providedIn: 'root' })
export class SystemStore {
  private readonly api = inject(LookupsApi);
  readonly info = signal<SystemInfo | null>(null);
  readonly mode = computed(() => this.info()?.mode ?? '');
  /** The Microsoft 365 simulator is only meaningful when the integration runs against the emulator. */
  readonly isEmulator = computed(() => this.mode().toLowerCase() === 'emulator');
  readonly aiProvider = computed(() => this.info()?.aiProvider ?? '');
  readonly organisation = computed(() => this.info()?.organisation ?? '');

  load(): void {
    this.api.systemInfo().subscribe({ next: (i) => this.info.set(i), error: () => undefined });
  }
}
