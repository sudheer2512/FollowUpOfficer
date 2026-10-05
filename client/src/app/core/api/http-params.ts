import { HttpParams } from '@angular/common/http';

/** Builds HttpParams from an object, skipping null / undefined / empty-string values. Arrays become comma lists. */
export function toParams(obj: Record<string, unknown> | null | undefined): HttpParams {
  let params = new HttpParams();
  if (!obj) return params;
  for (const [k, v] of Object.entries(obj)) {
    if (v === null || v === undefined || v === '') continue;
    if (Array.isArray(v)) {
      if (v.length) params = params.set(k, v.join(','));
    } else {
      params = params.set(k, String(v));
    }
  }
  return params;
}

export function enc(id: string): string {
  return encodeURIComponent(id);
}
