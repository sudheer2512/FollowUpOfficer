import { DOCUMENT } from '@angular/common';
import { Injectable, computed, effect, inject, signal } from '@angular/core';

export type ThemePreference = 'system' | 'light' | 'dark';

const STORAGE_KEY = 'fu.theme';

/**
 * Light / dark theme. "system" follows prefers-color-scheme; an explicit choice is stored in localStorage
 * and applied as <html data-theme="light|dark">, which the CSS tokens key off.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly doc = inject(DOCUMENT);
  private readonly media = typeof matchMedia === 'function' ? matchMedia('(prefers-color-scheme: dark)') : null;
  private readonly systemDark = signal(this.media?.matches ?? false);

  readonly preference = signal<ThemePreference>(this.read());
  readonly effective = computed<'light' | 'dark'>(() => {
    const p = this.preference();
    return p === 'system' ? (this.systemDark() ? 'dark' : 'light') : p;
  });

  constructor() {
    this.media?.addEventListener?.('change', (e) => this.systemDark.set(e.matches));
    effect(() => {
      const p = this.preference();
      const root = this.doc.documentElement;
      if (p === 'system') root.removeAttribute('data-theme');
      else root.setAttribute('data-theme', p);
      root.style.colorScheme = this.effective();
      try {
        localStorage.setItem(STORAGE_KEY, p);
      } catch {
        /* storage unavailable */
      }
    });
  }

  set(p: ThemePreference): void {
    this.preference.set(p);
  }

  /** Cycles light → dark → system. */
  toggle(): void {
    const order: ThemePreference[] = ['light', 'dark', 'system'];
    const i = order.indexOf(this.preference());
    this.preference.set(order[(i + 1) % order.length]);
  }

  private read(): ThemePreference {
    try {
      const v = localStorage.getItem(STORAGE_KEY);
      return v === 'light' || v === 'dark' || v === 'system' ? v : 'system';
    } catch {
      return 'system';
    }
  }
}
