/**
 * Minimal async-state helper for signal-based components.
 *
 *   readonly data = loader<CeoWorkbench>();
 *   reload(silent = false) { this.data.load(this.api.workbench(), { silent }); }
 *
 * Template: @if (data.loading() && !data.value()) { skeleton } @else if (data.error()) { … } @else { data.value() }
 *
 * Must be created in an injection context (a field initialiser or constructor); the running request is cancelled
 * when the component is destroyed or when a newer load starts.
 */
import { DestroyRef, Signal, inject, signal } from '@angular/core';
import { Observable, Subscription } from 'rxjs';
import { problemMessage } from '../http/problem';

export interface Loader<T> {
  readonly value: Signal<T | undefined>;
  readonly loading: Signal<boolean>;
  readonly error: Signal<string | null>;
  /** Starts a load. `silent` keeps the current value and spinner hidden (used for real-time refreshes). */
  load(source: Observable<T>, opts?: { silent?: boolean }): void;
  set(value: T | undefined): void;
  update(fn: (value: T | undefined) => T | undefined): void;
}

export function loader<T>(initial?: T): Loader<T> {
  const destroyRef = inject(DestroyRef);
  const value = signal<T | undefined>(initial);
  const loading = signal(false);
  const error = signal<string | null>(null);
  let sub: Subscription | undefined;
  destroyRef.onDestroy(() => sub?.unsubscribe());

  return {
    value: value.asReadonly(),
    loading: loading.asReadonly(),
    error: error.asReadonly(),
    load(source: Observable<T>, opts?: { silent?: boolean }) {
      sub?.unsubscribe();
      if (!opts?.silent) loading.set(true);
      error.set(null);
      sub = source.subscribe({
        next: (v) => {
          value.set(v);
          loading.set(false);
        },
        error: (e: unknown) => {
          loading.set(false);
          if (!opts?.silent) error.set(problemMessage(e));
        },
      });
    },
    set(v: T | undefined) {
      value.set(v);
    },
    update(fn: (v: T | undefined) => T | undefined) {
      value.update(fn);
    },
  };
}
