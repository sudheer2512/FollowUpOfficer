import { Injectable, computed, signal } from '@angular/core';
import { MeDto } from '../models/admin.models';

/**
 * The signed-in user as signals. Kept free of HttpClient so the HTTP interceptor can clear it
 * without a circular dependency (AuthService → HttpClient → interceptor → AuthService).
 */
@Injectable({ providedIn: 'root' })
export class SessionState {
  private readonly _me = signal<MeDto | null>(null);
  readonly me = this._me.asReadonly();

  readonly isSignedIn = computed(() => this._me() !== null);
  readonly isExecutive = computed(() => this._me()?.isExecutive ?? false);
  readonly isAdministrator = computed(() => this._me()?.isAdministrator ?? false);
  readonly isManager = computed(() => {
    const me = this._me();
    return !!me && (me.isManager || me.isExecutive || me.ledTeamIds.length > 0);
  });
  /** Executives and administrators may open the integration monitor and audit log. */
  readonly canMonitor = computed(() => this.isExecutive() || this.isAdministrator());
  readonly employeeId = computed(() => this._me()?.employeeId ?? null);
  readonly firstName = computed(() => (this._me()?.name ?? '').split(' ')[0] ?? '');

  set(me: MeDto | null): void {
    this._me.set(me);
  }

  clear(): void {
    this._me.set(null);
  }
}
