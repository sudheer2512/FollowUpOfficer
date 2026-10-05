import { HttpContextToken, HttpErrorResponse } from '@angular/common/http';
import { ProblemDetails } from '../models/common.models';

/** Set on a request to suppress the global error toast (the caller shows its own message). */
export const SILENT_ERRORS = new HttpContextToken<boolean>(() => false);
/** Set on a request so a 401 does not redirect to /login (e.g. the initial "who am I" probe). */
export const SKIP_AUTH_REDIRECT = new HttpContextToken<boolean>(() => false);

/** Extracts the problem+json body (if any) from an HTTP error. */
export function problemOf(err: unknown): ProblemDetails | null {
  if (err instanceof HttpErrorResponse) {
    const body = err.error;
    if (body && typeof body === 'object') return body as ProblemDetails;
    if (typeof body === 'string' && body.trim().startsWith('{')) {
      try {
        return JSON.parse(body) as ProblemDetails;
      } catch {
        return null;
      }
    }
  }
  return null;
}

/** A friendly, user-facing sentence for any error (problem+json detail/title first, then status defaults). */
export function problemMessage(err: unknown): string {
  const p = problemOf(err);
  if (p?.detail) return p.detail;
  if (p?.errors) {
    const firstErr = Object.values(p.errors).flat()[0];
    if (firstErr) return firstErr;
  }
  if (err instanceof HttpErrorResponse) {
    if (p?.title && err.status !== 500) return p.title;
    switch (err.status) {
      case 0:
        return 'The server could not be reached. Check your connection and try again.';
      case 400:
        return 'Some of the information is not valid.';
      case 401:
        return 'Your session has ended. Please sign in again.';
      case 403:
        return 'You are not allowed to do that.';
      case 404:
        return 'That item no longer exists or is not visible to you.';
      case 409:
        return 'That change conflicts with the current state. Refresh and try again.';
      case 429:
        return 'Too many requests. Please wait a moment.';
      default:
        return err.status >= 500 ? 'Something went wrong on the server.' : `Request failed (${err.status}).`;
    }
  }
  if (err instanceof Error) return err.message;
  return 'Something went wrong.';
}

/** The stable problem `code` (e.g. "task.invalid_transition") when present. */
export function problemCode(err: unknown): string | null {
  return problemOf(err)?.code ?? null;
}
