import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { ToastService } from '../../shared/ui/toast.service';
import { SessionState } from '../auth/session-state';
import { SILENT_ERRORS, SKIP_AUTH_REDIRECT, problemMessage } from './problem';

/**
 * Cross-cutting API behaviour:
 *  - sends the auth cookie (withCredentials) and the CSRF guard header `X-Requested-With: FollowUp`
 *    (required on every non-GET request; harmless on GETs);
 *  - on 401 clears the session and redirects to /login?returnUrl=…;
 *  - maps problem+json errors to a friendly toast unless the request opted out with SILENT_ERRORS.
 */
export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const isApi = req.url.startsWith('/api') || req.url.startsWith('api/');
  if (!isApi) return next(req);

  const router = inject(Router);
  const toast = inject(ToastService);
  const session = inject(SessionState);

  const outgoing = req.clone({
    withCredentials: true,
    setHeaders: { 'X-Requested-With': 'FollowUp' },
  });

  return next(outgoing).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse) {
        if (err.status === 401) {
          if (!req.context.get(SKIP_AUTH_REDIRECT)) {
            const wasSignedIn = session.me() !== null;
            session.clear();
            const current = router.url;
            if (!current.startsWith('/login')) {
              if (wasSignedIn) toast.info('Your session has ended. Please sign in again.');
              void router.navigate(['/login'], { queryParams: current && current !== '/' ? { returnUrl: current } : {} });
            }
          }
        } else if (!req.context.get(SILENT_ERRORS)) {
          toast.error(problemMessage(err));
        }
      }
      return throwError(() => err);
    }),
  );
};
