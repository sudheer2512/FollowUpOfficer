import { ChangeDetectionStrategy, Component, DestroyRef, Signal, computed, effect, inject, linkedSignal, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { Subject, debounceTime, map } from 'rxjs';
import { TasksApi } from '../../core/api/tasks.api';
import { PagedResult } from '../../core/models/common.models';
import { TaskFilter, TaskListItem, TaskScope, TaskSort } from '../../core/models/task.models';
import { refreshOn } from '../../core/realtime/realtime.service';
import { DelegateLauncher } from '../../core/state/delegate-launcher';
import { LookupsStore } from '../../core/state/lookups.store';
import { loader } from '../../core/util/loader';
import { DEFAULT_PAGE_SIZE, QueryInput, filterToQuery, queryToFilter } from '../../core/util/task-filter-query';
import { TaskTableComponent } from '../../shared/domain/task-table.component';
import { DrawerComponent } from '../../shared/ui/drawer.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { PaginatorComponent } from '../../shared/ui/paginator.component';
import { SkeletonComponent } from '../../shared/ui/skeleton.component';
import { FilterChip, FilterNameResolver, clearCriteria, describeFilter, removeChip } from './list/filter-chips';
import { FilterPanelComponent } from './list/filter-panel.component';
import { SavedViewsComponent } from './list/saved-views.component';

const FILTER_PANEL_PREF = 'fu.tasks.filterPanel';

const SCOPES: { value: TaskScope; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'mine', label: 'Mine' },
  { value: 'delegated', label: 'Delegated by me' },
  { value: 'following', label: 'Following' },
  { value: 'team', label: 'My team' },
];

const SORTS: { value: TaskSort; label: string }[] = [
  { value: 'attention', label: 'Attention' },
  { value: 'due', label: 'Due date' },
  { value: 'updated', label: 'Last update' },
  { value: 'number', label: 'Number' },
  { value: 'priority', label: 'Priority' },
];

/** Open / closed / all quick toggle → TaskFilter.open (true / false / absent). */
const OPEN_STATES: { value: boolean | null; label: string }[] = [
  { value: true, label: 'Open' },
  { value: false, label: 'Closed' },
  { value: null, label: 'All' },
];

/** Equality for filters by URL meaning, so unrelated query-param churn never triggers a reload. */
function sameQuery(a: TaskFilter, b: TaskFilter): boolean {
  return JSON.stringify(filterToQuery(a)) === JSON.stringify(filterToQuery(b));
}

/** Guards against an unfinished backend: missing arrays and paging fields. */
function normalisePage(page: PagedResult<TaskListItem> | null, filter: TaskFilter): PagedResult<TaskListItem> {
  const items = (page?.items ?? []).map((t) => ({ ...t, tags: t.tags ?? [] }));
  return {
    items,
    total: page?.total ?? items.length,
    page: page?.page || filter.page || 1,
    pageSize: page?.pageSize || filter.pageSize || DEFAULT_PAGE_SIZE,
  };
}

/** A signal that follows a CSS media query for the lifetime of the calling component. */
function mediaQuery(query: string): Signal<boolean> {
  const mql = typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia(query) : null;
  const matches = signal(mql?.matches ?? false);
  if (mql) {
    const onChange = (e: MediaQueryListEvent) => matches.set(e.matches);
    mql.addEventListener('change', onChange);
    inject(DestroyRef).onDestroy(() => mql.removeEventListener('change', onChange));
  }
  return matches.asReadonly();
}

function readPanelPreference(): boolean | null {
  try {
    const v = localStorage.getItem(FILTER_PANEL_PREF);
    return v === null ? null : v === '1';
  } catch {
    return null;
  }
}

/**
 * /tasks — the task list. The URL query string is the single source of truth: every filter change navigates, and the
 * list reloads from the parsed TaskFilter, so drill-downs from tiles and shared links land on the same view.
 */
@Component({
  selector: 'fu-task-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    IconComponent,
    SkeletonComponent,
    EmptyStateComponent,
    PaginatorComponent,
    DrawerComponent,
    TaskTableComponent,
    FilterPanelComponent,
    SavedViewsComponent,
  ],
  templateUrl: './task-list.page.html',
  styleUrl: './task-list.page.css',
})
export class TaskListPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(TasksApi);
  private readonly lookups = inject(LookupsStore);
  protected readonly delegate = inject(DelegateLauncher);

  protected readonly scopes = SCOPES;
  protected readonly sorts = SORTS;
  protected readonly openStates = OPEN_STATES;

  private readonly queryParams = toSignal(this.route.queryParams, { initialValue: this.route.snapshot.queryParams });

  /** The active filter, parsed from the URL. */
  protected readonly filter = computed(() => queryToFilter(this.queryParams() as QueryInput), { equal: sameQuery });
  protected readonly data = loader<PagedResult<TaskListItem>>();

  protected readonly scope = computed(() => (this.filter().scope as TaskScope | null | undefined) ?? 'all');
  protected readonly openState = computed(() => (typeof this.filter().open === 'boolean' ? this.filter().open! : null));
  protected readonly sort = computed(() => (this.filter().sort as TaskSort | null | undefined) ?? 'attention');

  private readonly names: FilterNameResolver = {
    person: (id) => this.lookups.personName(id),
    team: (id) => this.lookups.teamName(id),
    department: (id) => this.lookups.nameIn(this.lookups.departments(), id),
    project: (id) => this.lookups.nameIn(this.lookups.projects(), id),
  };
  /** Removable chips for the active criteria (re-labelled once lookups arrive). */
  protected readonly chips = computed(() => {
    this.lookups.data();
    return describeFilter(this.filter(), this.names);
  });

  /** The search box mirrors the URL text but can run ahead of it while the user types. */
  protected readonly searchText = linkedSignal(() => this.filter().text ?? '');
  private readonly search$ = new Subject<string>();

  protected readonly narrow = mediaQuery('(max-width: 1000px)');
  /** Side column on wide screens (remembered per browser); a drawer on narrow screens, closed by default. */
  protected readonly showFilters = signal(this.narrow() ? false : (readPanelPreference() ?? true));

  constructor() {
    this.lookups.load();

    effect(() => {
      const f = this.filter();
      untracked(() => this.load(f, false));
    });

    this.search$.pipe(debounceTime(300), takeUntilDestroyed()).subscribe((text) => this.applySearch(text));

    refreshOn(['task.changed', 'task.created'], () => this.load(this.filter(), true));
  }

  protected reload(): void {
    this.load(this.filter(), false);
  }

  private load(filter: TaskFilter, silent: boolean): void {
    this.data.load(this.api.query(filter).pipe(map((page) => normalisePage(page, filter))), { silent });
  }

  /** Applies a change to the criteria (page restarts at 1 unless told otherwise). */
  protected update(patch: Partial<TaskFilter>, opts: { resetPage?: boolean; replace?: boolean } = {}): void {
    const next: TaskFilter = { ...this.filter(), ...patch };
    if (opts.resetPage !== false) next.page = 1;
    this.navigate(next, opts.replace ?? false);
  }

  protected navigate(filter: TaskFilter, replace = false): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: filterToQuery(filter), replaceUrl: replace });
  }

  protected setScope(scope: TaskScope): void {
    this.update({ scope: scope === 'all' ? undefined : scope });
  }

  protected setOpen(open: boolean | null): void {
    this.update({ open: open ?? undefined });
  }

  protected setSort(sort: string): void {
    this.update({ sort: sort === 'attention' ? undefined : sort });
  }

  protected goToPage(page: number): void {
    this.update({ page }, { resetPage: false });
  }

  protected onSearchInput(text: string): void {
    this.searchText.set(text);
    this.search$.next(text);
  }

  /** Enter applies the search immediately instead of waiting for the debounce. */
  protected applySearch(text: string = this.searchText()): void {
    const next = text.trim();
    if (next === (this.filter().text ?? '')) return;
    this.update({ text: next || undefined }, { replace: true });
  }

  protected clearSearch(): void {
    this.searchText.set('');
    this.applySearch('');
  }

  protected removeChip(chip: FilterChip): void {
    this.navigate(removeChip(this.filter(), chip));
  }

  protected clearAll(): void {
    this.navigate(clearCriteria(this.filter()));
  }

  protected toggleFilters(): void {
    const next = !this.showFilters();
    this.showFilters.set(next);
    if (this.narrow()) return;
    try {
      localStorage.setItem(FILTER_PANEL_PREF, next ? '1' : '0');
    } catch {
      /* storage unavailable: the preference simply is not remembered */
    }
  }

  protected chipKey(chip: FilterChip): string {
    return `${chip.key}:${chip.value ?? ''}`;
  }
}
