import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, finalize, map, of, shareReplay, tap } from 'rxjs';
import { LookupsApi } from '../api/lookups.api';
import { Lookups, PersonLookup, TeamLookup } from '../models/admin.models';
import { Guid, NamedRef } from '../models/common.models';

const EMPTY: Lookups = {
  people: [],
  teams: [],
  departments: [],
  projects: [],
  categories: [],
  escalationPolicies: [],
  tags: [],
  templates: [],
};

/**
 * Cached GET /api/lookups for pickers and selects. `ensure()` loads once; `refresh()` reloads
 * (call after admin edits). Read through the signals in templates.
 */
@Injectable({ providedIn: 'root' })
export class LookupsStore {
  private readonly api = inject(LookupsApi);
  private readonly _data = signal<Lookups>(EMPTY);
  private readonly _loaded = signal(false);
  private inflight$?: Observable<Lookups>;

  readonly data = this._data.asReadonly();
  readonly loaded = this._loaded.asReadonly();
  readonly people = computed(() => this._data().people);
  readonly teams = computed(() => this._data().teams);
  readonly departments = computed(() => this._data().departments);
  readonly projects = computed(() => this._data().projects);
  readonly categories = computed(() => this._data().categories);
  readonly escalationPolicies = computed(() => this._data().escalationPolicies);
  readonly tags = computed(() => this._data().tags);
  readonly templates = computed(() => this._data().templates);

  private readonly peopleById = computed(() => new Map(this._data().people.map((p) => [p.id, p])));
  private readonly teamsById = computed(() => new Map(this._data().teams.map((t) => [t.id, t])));

  /** Loads once (subsequent calls return the cached value). */
  ensure(): Observable<Lookups> {
    if (this._loaded()) return of(this._data());
    return this.fetch();
  }

  refresh(): Observable<Lookups> {
    this.inflight$ = undefined;
    return this.fetch();
  }

  private fetch(): Observable<Lookups> {
    this.inflight$ ??= this.api.lookups().pipe(
      map((l) => ({ ...EMPTY, ...l })),
      tap((l) => {
        this._data.set(l);
        this._loaded.set(true);
      }),
      finalize(() => (this.inflight$ = undefined)),
      shareReplay(1),
    );
    return this.inflight$;
  }

  /** Fire-and-forget load for components that only read the signals. */
  load(): void {
    this.ensure().subscribe({ error: () => undefined });
  }

  person(id: Guid | null | undefined): PersonLookup | undefined {
    return id ? this.peopleById().get(id) : undefined;
  }

  personName(id: Guid | null | undefined): string {
    return this.person(id)?.name ?? '';
  }

  team(id: Guid | null | undefined): TeamLookup | undefined {
    return id ? this.teamsById().get(id) : undefined;
  }

  teamName(id: Guid | null | undefined): string {
    return this.team(id)?.name ?? '';
  }

  nameIn(list: NamedRef[], id: Guid | null | undefined): string {
    return id ? (list.find((x) => x.id === id)?.name ?? '') : '';
  }
}
