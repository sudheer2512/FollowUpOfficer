import { Signal, computed, effect, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Observable, map } from 'rxjs';

/** The part of a reactive-forms control these helpers need. */
interface ValueSource<V> {
  readonly valueChanges: Observable<unknown>;
  getRawValue(): V;
}

/**
 * The raw value of a form as a signal, for zoneless OnPush templates: programmatic patches re-render too.
 * Must be called in an injection context (field initialiser or constructor).
 */
export function rawValueSignal<V>(control: ValueSource<V>): Signal<V> {
  return toSignal(control.valueChanges.pipe(map(() => control.getRawValue())), { initialValue: control.getRawValue() });
}

/**
 * Same as rawValueSignal for a form that arrives through a signal input. The value is available synchronously on
 * first render and follows the input if a different form is passed. Must be called in an injection context.
 */
export function rawValueOfInput<V>(source: () => ValueSource<V>): Signal<V> {
  const version = signal(0);
  effect((onCleanup) => {
    const sub = source().valueChanges.subscribe(() => version.update((n) => n + 1));
    onCleanup(() => sub.unsubscribe());
  });
  return computed(() => {
    version();
    return source().getRawValue();
  });
}
