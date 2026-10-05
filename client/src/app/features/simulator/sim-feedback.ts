import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { ToastService } from '../../shared/ui/toast.service';
import { SimUser } from './simulator.models';

/**
 * Returns a function that confirms a simulated post and offers a jump to the integration monitor,
 * where the next sync picks the message up. Call in an injection context (field initialiser).
 */
export function injectAnnounceSent(): () => void {
  const toast = inject(ToastService);
  const router = inject(Router);
  return () =>
    toast.success('Sent — the next sync will import it', {
      action: { label: 'Monitor', run: () => void router.navigateByUrl('/admin/monitor') },
    });
}

/** "Asha Rao · Finance Manager" for user pickers. */
export function userLabel(u: SimUser): string {
  return u.jobTitle ? `${u.displayName} · ${u.jobTitle}` : u.displayName;
}

/** Reads `value` from an input / select / textarea event. */
export function eventValue(event: Event): string {
  return (event.target as HTMLInputElement | null)?.value ?? '';
}

/** Reads `checked` from a checkbox / switch change event. */
export function eventChecked(event: Event): boolean {
  return (event.target as HTMLInputElement | null)?.checked ?? false;
}
