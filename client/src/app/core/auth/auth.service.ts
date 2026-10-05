import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of, shareReplay, tap } from 'rxjs';
import { AuthConfig, LoginRequest, MeDto } from '../models/admin.models';
import { SILENT_ERRORS, SKIP_AUTH_REDIRECT } from '../http/problem';
import { SessionState } from './session-state';

/** Cookie-based sign-in against /api/auth/*. The session itself lives in {@link SessionState}. */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly session = inject(SessionState);

  /** True once the initial "who am I" probe has completed (signed in or not). */
  readonly ready = signal(false);

  private config$?: Observable<AuthConfig>;

  /** Called once at start-up: restores an existing cookie session without redirecting or toasting. */
  async restore(): Promise<void> {
    try {
      const me = await firstValueFrom(
        this.http.get<MeDto>('/api/auth/me', {
          context: new HttpContext().set(SKIP_AUTH_REDIRECT, true).set(SILENT_ERRORS, true),
        }),
      );
      this.session.set(me);
    } catch {
      this.session.clear();
    } finally {
      this.ready.set(true);
    }
  }

  config(): Observable<AuthConfig> {
    this.config$ ??= this.http
      .get<AuthConfig>('/api/auth/config', { context: new HttpContext().set(SILENT_ERRORS, true) })
      .pipe(
        catchError(() => of<AuthConfig>({ entraEnabled: false, demoMode: false, demoUsers: [] })),
        shareReplay(1),
      );
    return this.config$;
  }

  login(req: LoginRequest): Observable<MeDto> {
    return this.http
      .post<MeDto>('/api/auth/login', req, {
        context: new HttpContext().set(SKIP_AUTH_REDIRECT, true).set(SILENT_ERRORS, true),
      })
      .pipe(tap((me) => this.session.set(me)));
  }

  /** Re-reads /api/auth/me (after role changes). */
  refreshMe(): Observable<MeDto | null> {
    return this.http.get<MeDto>('/api/auth/me', { context: new HttpContext().set(SILENT_ERRORS, true) }).pipe(
      tap((me) => this.session.set(me)),
      catchError(() => of(null)),
    );
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', {}, { context: new HttpContext().set(SILENT_ERRORS, true) }).pipe(
      catchError(() => of(undefined)),
      map(() => undefined),
      tap(() => this.session.clear()),
    );
  }
}
