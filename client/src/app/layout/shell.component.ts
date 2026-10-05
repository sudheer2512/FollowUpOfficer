import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, OnDestroy, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged, filter, of, switchMap } from 'rxjs';
import { ExecutiveApi } from '../core/api/executive.api';
import { LookupsApi } from '../core/api/lookups.api';
import { AuthService } from '../core/auth/auth.service';
import { SessionState } from '../core/auth/session-state';
import { SearchHit } from '../core/models/view.models';
import { RealtimeService } from '../core/realtime/realtime.service';
import { DelegateLauncher } from '../core/state/delegate-launcher';
import { InboxStore } from '../core/state/inbox.store';
import { LookupsStore } from '../core/state/lookups.store';
import { SystemStore } from '../core/state/system.store';
import { ThemeService } from '../core/state/theme.service';
import { DelegateDialogComponent } from '../features/delegate/delegate-dialog.component';
import { AvatarComponent } from '../shared/domain/indicators';
import { IconComponent } from '../shared/ui/icon.component';
import { ToastService } from '../shared/ui/toast.service';
import { InboxDrawerComponent } from './inbox-drawer.component';
import { buildNav } from './nav';

const NAV_KEY = 'fu.nav.collapsed';

/**
 * Signed-in application frame: collapsible left navigation (a drawer below 900px), top bar with global search
 * (Ctrl/Cmd+K), Delegate (N), voice delegation, inbox bell and user menu. Starts the real-time connection.
 */
@Component({
  selector: 'fu-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, IconComponent, AvatarComponent, InboxDrawerComponent, DelegateDialogComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.css',
  host: {
    '(document:keydown)': 'onKeydown($event)',
    '(document:click)': 'onDocClick($event)',
  },
})
export class ShellComponent implements OnDestroy {
  private readonly router = inject(Router);
  private readonly el = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly auth = inject(AuthService);
  private readonly exec = inject(ExecutiveApi);
  private readonly lookupsApi = inject(LookupsApi);
  private readonly realtime = inject(RealtimeService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly session = inject(SessionState);
  protected readonly inbox = inject(InboxStore);
  protected readonly theme = inject(ThemeService);
  protected readonly system = inject(SystemStore);
  protected readonly launcher = inject(DelegateLauncher);
  private readonly lookups = inject(LookupsStore);

  protected readonly collapsed = signal(this.readCollapsed());
  protected readonly mobileOpen = signal(false);
  protected readonly userMenuOpen = signal(false);
  protected readonly attentionCount = signal<number | null>(null);
  protected readonly connection = this.realtime.state;

  protected readonly nav = computed(() => buildNav(this.session, { emulator: this.system.isEmulator() }));

  // ── Global search ──
  protected readonly searchText = signal('');
  protected readonly searchOpen = signal(false);
  protected readonly searchHits = signal<SearchHit[]>([]);
  protected readonly searchLoading = signal(false);
  protected readonly searchActive = signal(-1);
  private readonly search$ = new Subject<string>();

  constructor() {
    this.lookups.load();
    this.system.load();
    this.inbox.refreshCount();
    this.refreshAttentionCount();
    void this.realtime.start();

    this.realtime
      .on(['inbox.changed', 'notification.queued'], { debounceMs: 600 })
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        this.inbox.refreshCount();
        if (this.inbox.open()) this.inbox.loadItems();
      });

    this.realtime
      .on(['attention.changed'], { debounceMs: 1500 })
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        if (!this.session.isExecutive()) return;
        const before = this.attentionCount();
        this.exec.attention('Open').subscribe({
          next: (items) => {
            this.attentionCount.set(items.length);
            if (before !== null && items.length > before && !this.router.url.startsWith('/attention')) {
              this.toast.info(`${items.length - before} new item${items.length - before === 1 ? '' : 's'} need${items.length - before === 1 ? 's' : ''} your attention.`, {
                action: { label: 'Review', run: () => void this.router.navigate(['/attention']) },
                timeoutMs: 6000,
              });
            }
          },
          error: () => undefined,
        });
      });

    this.search$
      .pipe(
        debounceTime(220),
        distinctUntilChanged(),
        switchMap((q) => {
          if (q.trim().length < 2) {
            this.searchLoading.set(false);
            return of(null);
          }
          this.searchLoading.set(true);
          return this.lookupsApi.search(q.trim());
        }),
        takeUntilDestroyed(),
      )
      .subscribe({
        next: (r) => {
          this.searchLoading.set(false);
          this.searchHits.set(r?.hits.slice(0, 8) ?? []);
          this.searchActive.set(-1);
        },
        error: () => this.searchLoading.set(false),
      });

    this.router.events
      .pipe(
        filter((e) => e instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.mobileOpen.set(false);
        this.userMenuOpen.set(false);
        this.searchOpen.set(false);
      });
  }

  ngOnDestroy(): void {
    void this.realtime.stop();
  }

  private refreshAttentionCount(): void {
    if (!this.session.isExecutive()) return;
    this.exec.attention('Open').subscribe({ next: (items) => this.attentionCount.set(items.length), error: () => undefined });
  }

  protected badge(key: string | undefined): number | null {
    return key === 'attention' ? this.attentionCount() : null;
  }

  protected toggleCollapsed(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    try {
      localStorage.setItem(NAV_KEY, next ? '1' : '0');
    } catch {
      /* ignore */
    }
  }

  private readCollapsed(): boolean {
    try {
      return localStorage.getItem(NAV_KEY) === '1';
    } catch {
      return false;
    }
  }

  // ── Search ──
  protected onSearchInput(v: string): void {
    this.searchText.set(v);
    this.searchOpen.set(true);
    this.search$.next(v);
  }

  protected onSearchKey(e: KeyboardEvent): void {
    const hits = this.searchHits();
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      this.searchActive.set(Math.min(hits.length - 1, this.searchActive() + 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      this.searchActive.set(Math.max(-1, this.searchActive() - 1));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const hit = hits[this.searchActive()];
      if (hit) this.openHit(hit);
      else this.submitSearch();
    } else if (e.key === 'Escape') {
      this.searchOpen.set(false);
      (e.target as HTMLInputElement).blur();
    }
  }

  protected submitSearch(): void {
    const q = this.searchText().trim();
    if (!q) return;
    this.searchOpen.set(false);
    void this.router.navigate(['/search'], { queryParams: { q } });
  }

  protected openHit(hit: SearchHit): void {
    this.searchOpen.set(false);
    this.searchText.set('');
    const route = hit.route || (hit.taskId ? `/tasks/${hit.taskId}` : '/search');
    void this.router.navigateByUrl(route);
  }

  // ── Actions ──
  protected delegate(voice = false): void {
    this.launcher.open({ voice });
  }

  protected toggleInbox(): void {
    this.inbox.toggle();
    this.userMenuOpen.set(false);
  }

  protected signOut(): void {
    this.auth.logout().subscribe(() => {
      void this.realtime.stop();
      this.inbox.reset();
      void this.router.navigate(['/login']);
    });
  }

  protected themeLabel(): string {
    const p = this.theme.preference();
    return p === 'system' ? 'System theme' : p === 'dark' ? 'Dark theme' : 'Light theme';
  }

  // ── Keyboard shortcuts: Ctrl/Cmd+K search, N delegate, Esc closes menus ──
  protected onKeydown(e: KeyboardEvent): void {
    if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
      e.preventDefault();
      this.el.nativeElement.querySelector<HTMLInputElement>('#fu-global-search')?.focus();
      this.searchOpen.set(true);
      return;
    }
    if (e.key === 'Escape') {
      this.userMenuOpen.set(false);
      this.mobileOpen.set(false);
      return;
    }
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if ((e.key === 'n' || e.key === 'N') && !this.isTyping(e) && !this.launcher.request() && !document.querySelector('[aria-modal=true]')) {
      e.preventDefault();
      this.delegate();
    }
  }

  private isTyping(e: KeyboardEvent): boolean {
    const t = e.target as HTMLElement | null;
    if (!t) return false;
    const tag = t.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || t.isContentEditable;
  }

  protected onDocClick(e: MouseEvent): void {
    const target = e.target as HTMLElement;
    if (!target.closest('.user-menu-wrap')) this.userMenuOpen.set(false);
    if (!target.closest('.search-wrap')) this.searchOpen.set(false);
  }
}
