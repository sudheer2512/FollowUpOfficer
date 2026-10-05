import { Signal, computed } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup } from '@angular/forms';
import { map } from 'rxjs';

/** A signal snapshot of a reactive form, so zoneless OnPush templates re-render on every form event. */
export interface FormView<T> {
  value: T;
  dirty: boolean;
  valid: boolean;
}

/**
 * Wraps `form.events` (value, status, pristine, touched, reset) in a signal.
 * Must be called in an injection context (field initialiser or constructor).
 */
export function formView<F extends FormGroup>(form: F): Signal<FormView<ReturnType<F['getRawValue']>>> {
  const version = toSignal(form.events.pipe(map((_, i) => i)), { initialValue: -1 });
  return computed(() => {
    version();
    return { value: form.getRawValue() as ReturnType<F['getRawValue']>, dirty: form.dirty, valid: form.valid };
  });
}

/** Adds or removes `item` in a string-array control (checkbox groups bound to [Flags] lists). */
export function toggleInList(control: FormControl<string[]>, item: string, on: boolean): void {
  const set = new Set(control.value ?? []);
  if (on) set.add(item);
  else set.delete(item);
  control.setValue([...set]);
  control.markAsDirty();
}

/** Keeps the canonical order of `values` when turning a selection back into a flags list. */
export function inOrder<T extends string>(values: readonly T[], selected: readonly string[]): T[] {
  const set = new Set(selected);
  return values.filter((v) => set.has(v));
}

/** Trimmed string or null (optional text fields). */
export function textOrNull(value: string | null | undefined): string | null {
  const t = (value ?? '').trim();
  return t.length ? t : null;
}

/** Reads `checked` from a change event on a checkbox / switch input. */
export function checkedOf(event: Event): boolean {
  return (event.target as HTMLInputElement | null)?.checked ?? false;
}

/** Reads `value` from an input / select / textarea event. */
export function valueOf(event: Event): string {
  return (event.target as HTMLInputElement | null)?.value ?? '';
}
