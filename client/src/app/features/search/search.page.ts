import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink, UrlTree } from '@angular/router';
import { of } from 'rxjs';
import { LookupsApi } from '../../core/api/lookups.api';
import { SearchHit, SearchResult } from '../../core/models/view.models';
import { loader } from '../../core/util/loader';
import { AgoPipe, DateTimePipe } from '../../shared/pipes/format.pipes';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { HighlightComponent } from './highlight.component';
import { compareKinds, kindMeta, searchTerms } from './search-text';

interface KindCount {
  kind: string;
  label: string;
  count: number;
}

interface HitRow {
  hit: SearchHit;
  /** Parsed in-app route (keeps any query string intact); null when the hit has no safe route. */
  link: UrlTree | null;
}

interface HitGroup {
  kind: string;
  icon: string;
  label: string;
  /** Matches reported by the server for this kind (may exceed the rows returned). */
  total: number;
  rows: HitRow[];
}

const NO_RESULT: SearchResult = { query: '', hits: [], counts: {} };

/**
 * Global search results (/search?q=): kind filter chips with counts, results grouped by kind, query terms
 * highlighted in titles and snippets. Every hit row is a link to its route.
 */
@Component({
  selector: 'fu-search-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, NgTemplateOutlet, IconComponent, EmptyStateComponent, SkeletonComponent, HighlightComponent, AgoPipe, DateTimePipe],
  template: `
    <div class="page page-narrow">
      <div class="page-header">
        <div class="titles">
          <h1>Search</h1>
          <div class="subtitle">Tasks, people, teams, emails, Teams messages, commitments, projects, tags and attachments</div>
        </div>
      </div>

      <form class="search-bar" role="search" (submit)="submit($event)">
        <div class="input-with-icon grow">
          <fu-icon name="search" />
          <input
            id="search-page-q"
            class="input"
            type="search"
            autocomplete="off"
            aria-label="Search"
            placeholder="Search by task number, title, person, message text…"
            [value]="draft()"
            (input)="draft.set($any($event.target).value)"
          />
        </div>
        <button type="submit" class="btn btn-primary">Search</button>
      </form>

      @if (!query()) {
        <fu-empty icon="search" title="Search everything" message="Search tasks, people, emails, Teams messages, commitments, projects, tags and attachments" />
      } @else if (data.loading() && !data.value()) {
        <section class="card mt-16"><fu-skeleton variant="table" [rows]="6" /></section>
      } @else if (data.error()) {
        <div class="alert tone-danger mt-16" role="alert">
          <fu-icon name="alert-circle" />
          <span class="spacer">{{ data.error() }}</span>
          <button type="button" class="btn btn-sm" (click)="reload()">Retry</button>
        </div>
      } @else if (!hits().length) {
        <fu-empty icon="search" [title]="'No results for “' + query() + '”'" message="Try fewer words, a task number such as T-1042, or a person’s name." />
      } @else {
        <div class="kinds" role="group" aria-label="Filter results by type">
          <button type="button" class="chip selectable" [class.on]="!kind()" [attr.aria-pressed]="!kind()" (click)="kind.set(null)">All ({{ total() }})</button>
          @for (k of kinds(); track k.kind) {
            <button type="button" class="chip selectable" [class.on]="kind() === k.kind" [attr.aria-pressed]="kind() === k.kind" (click)="kind.set(k.kind)">
              {{ k.label }} ({{ k.count }})
            </button>
          }
        </div>

        @for (g of groups(); track g.kind) {
          <section class="card mt-16" [attr.aria-label]="g.label">
            <header class="card-header">
              <fu-icon [name]="g.icon" class="muted" />
              <h2>{{ g.label }}</h2>
              <span class="count-pill">{{ g.total }}</span>
              @if (g.total > g.rows.length) {
                <span class="spacer"></span>
                <span class="xsmall muted">Showing the top {{ g.rows.length }}</span>
              }
            </header>
            <div class="list">
              @for (r of g.rows; track r.hit.kind + r.hit.id) {
                @if (r.link) {
                  <a class="list-row" [routerLink]="r.link">
                    <ng-container [ngTemplateOutlet]="rowBody" [ngTemplateOutletContext]="{ $implicit: r.hit, icon: g.icon }" />
                  </a>
                } @else {
                  <div class="list-row">
                    <ng-container [ngTemplateOutlet]="rowBody" [ngTemplateOutletContext]="{ $implicit: r.hit, icon: g.icon }" />
                  </div>
                }
              }
            </div>
          </section>
        }
      }
    </div>

    <ng-template #rowBody let-hit let-icon="icon">
      <span class="hit-icon"><fu-icon [name]="icon" [size]="15" /></span>
      <span class="main">
        <span class="title row gap-4">
          @if (hit.taskNumber) {
            <span class="task-number">{{ hit.taskNumber }}</span>
          }
          <span class="truncate"><fu-highlight [text]="hit.title" [terms]="terms()" /></span>
        </span>
        @if (hit.snippet) {
          <span class="snippet clamp-2"><fu-highlight [text]="hit.snippet" [terms]="terms()" /></span>
        }
      </span>
      @if (hit.at) {
        <span class="xsmall muted nowrap" [attr.title]="hit.at | dateTime">{{ hit.at | ago }}</span>
      }
    </ng-template>
  `,
  styles: `
    .search-bar { display: flex; gap: 8px; }
    .search-bar .grow { flex: 1 1 auto; }
    .search-bar .input { height: 40px; font-size: 14.5px; }
    .kinds { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 16px; }
    .hit-icon { width: 28px; height: 28px; border-radius: 8px; background: var(--surface-2); border: 1px solid var(--border); display: flex; align-items: center; justify-content: center; color: var(--muted); flex: none; }
    .snippet { display: block; font-size: 12.5px; color: var(--text-2); margin-top: 2px; }
    .list-row .title { display: flex; }
  `,
})
export class SearchPage {
  private readonly api = inject(LookupsApi);
  private readonly router = inject(Router);

  /** `?q=` (bound by withComponentInputBinding). */
  readonly q = input<string | undefined>();

  protected readonly data = loader<SearchResult>();
  protected readonly draft = signal('');
  /** Selected kind filter; null = all. */
  protected readonly kind = signal<string | null>(null);

  protected readonly query = computed(() => (this.q() ?? '').trim());
  protected readonly terms = computed(() => searchTerms(this.query()));
  protected readonly hits = computed(() => this.data.value()?.hits ?? []);

  /** Kind counts from the server's `counts`, completed by counting hits for kinds it did not report. */
  protected readonly kinds = computed<KindCount[]>(() => {
    const counts = new Map<string, number>();
    for (const [k, n] of Object.entries(this.data.value()?.counts ?? {})) {
      if (typeof n === 'number' && n > 0) counts.set(k, n);
    }
    const hitCounts = new Map<string, number>();
    for (const h of this.hits()) hitCounts.set(h.kind, (hitCounts.get(h.kind) ?? 0) + 1);
    for (const [k, n] of hitCounts) if (!counts.has(k)) counts.set(k, n);
    return [...counts.keys()].sort(compareKinds).map((k) => ({ kind: k, label: kindMeta(k).label, count: counts.get(k) ?? 0 }));
  });

  protected readonly total = computed(() => this.kinds().reduce((sum, k) => sum + k.count, 0));

  protected readonly groups = computed<HitGroup[]>(() => {
    const selected = this.kind();
    const byKind = new Map<string, HitRow[]>();
    for (const hit of this.hits()) {
      if (selected && hit.kind !== selected) continue;
      const rows = byKind.get(hit.kind) ?? [];
      rows.push({ hit, link: this.linkFor(hit) });
      byKind.set(hit.kind, rows);
    }
    const totals = new Map(this.kinds().map((k) => [k.kind, k.count]));
    return [...byKind.keys()].sort(compareKinds).map((kind) => {
      const meta = kindMeta(kind);
      const rows = byKind.get(kind) ?? [];
      return { kind, icon: meta.icon, label: meta.plural, total: Math.max(totals.get(kind) ?? 0, rows.length), rows };
    });
  });

  constructor() {
    // Search whenever ?q= changes (also when the shell's global search navigates here).
    effect(() => {
      const q = this.query();
      untracked(() => {
        this.draft.set(q);
        this.kind.set(null);
        this.data.set(undefined);
        // Loading an empty result also cancels a request still in flight for the previous query.
        this.data.load(q ? this.api.search(q) : of(NO_RESULT));
      });
    });
  }

  protected submit(e: Event): void {
    e.preventDefault();
    const q = this.draft().trim();
    if (q === this.query()) {
      this.reload();
      return;
    }
    void this.router.navigate([], { queryParams: { q: q || null }, queryParamsHandling: 'merge' });
  }

  protected reload(): void {
    const q = this.query();
    if (q) this.data.load(this.api.search(q));
  }

  /** In-app routes only; a missing route falls back to the hit's task. */
  private linkFor(hit: SearchHit): UrlTree | null {
    const route = hit.route || (hit.taskId ? `/tasks/${hit.taskId}` : '');
    if (!route.startsWith('/') || route.startsWith('//')) return null;
    try {
      return this.router.parseUrl(route);
    } catch {
      return null;
    }
  }
}
