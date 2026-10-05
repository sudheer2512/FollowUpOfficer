import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionState } from './session-state';

/** Requires a signed-in user; otherwise redirects to /login?returnUrl=… */
export const authGuard: CanActivateFn = (_route, state) => {
  const session = inject(SessionState);
  if (session.isSignedIn()) return true;
  return inject(Router).createUrlTree(['/login'], { queryParams: state.url && state.url !== '/' ? { returnUrl: state.url } : {} });
};

/** Signed-in users are sent away from /login. */
export const guestGuard: CanActivateFn = () => {
  const session = inject(SessionState);
  if (!session.isSignedIn()) return true;
  return inject(Router).createUrlTree([session.isExecutive() ? '/' : '/me']);
};

/** Executive-only pages (workbench, attention, reports, review). Others land on /me. */
export const executiveGuard: CanActivateFn = () => {
  const session = inject(SessionState);
  return session.isExecutive() ? true : inject(Router).createUrlTree(['/me']);
};

/** Administrator-only pages. */
export const adminGuard: CanActivateFn = () => {
  const session = inject(SessionState);
  return session.isAdministrator() ? true : inject(Router).createUrlTree(['/me']);
};

/** Integration monitor, audit log and simulator: executives or administrators. */
export const monitorGuard: CanActivateFn = () => {
  const session = inject(SessionState);
  return session.canMonitor() ? true : inject(Router).createUrlTree(['/me']);
};

/** Managers, team leaders and executives (team dashboard). Everyone else goes to /me. */
export const managerGuard: CanActivateFn = () => {
  const session = inject(SessionState);
  return session.isManager() || session.isAdministrator() ? true : inject(Router).createUrlTree(['/me']);
};
