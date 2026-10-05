import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { catchError, debounceTime, distinctUntilChanged, map, of, switchMap, tap } from 'rxjs';
import { TasksApi } from '../../core/api/tasks.api';
import { TaskListItem } from '../../core/models/task.models';
import { StatusBadgeComponent } from '../../shared/domain/indicators';
import { DateInputComponent } from '../../shared/forms/date-input.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { DependencyRow, createDependencyRow } from './delegate-form.model';

let depSeq = 0;

/**
 * Dependency rows for a new task: other tasks it waits for (finish-to-start, found by search) and external
 * dependencies (description, party, expected by). Renders and edits the FormArray it is given.
 */
@Component({
  selector: 'fu-task-dependencies',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, IconComponent, DateInputComponent, StatusBadgeComponent],
  template: `
    <div class="stack">
      @if (rows().length) {
        <ul class="deps">
          @for (row of rows().controls; track row; let i = $index) {
            <li [formGroup]="row">
              @if (row.controls.taskId.value) {
                <fu-icon name="link" [size]="14" />
                <span class="grow truncate">{{ row.controls.label.value }}</span>
                <span class="chip sm outline">Finish to start</span>
              } @else {
                <fu-icon name="globe" [size]="14" />
                <div class="ext grow">
                  <input class="input input-sm" formControlName="externalDescription" placeholder="What are we waiting for?" [attr.aria-label]="'External dependency ' + (i + 1)" />
                  <input class="input input-sm" formControlName="externalParty" placeholder="From whom (vendor, customer…)" aria-label="External party" />
                  <fu-date-input formControlName="expectedBy" ariaLabel="Expected by" />
                </div>
              }
              <button type="button" class="btn btn-ghost btn-icon btn-xs" [attr.aria-label]="'Remove dependency ' + (i + 1)" (click)="remove(i)">
                <fu-icon name="x" [size]="14" />
              </button>
            </li>
          }
        </ul>
      }

      <div class="search-row">
        <div class="input-with-icon search">
          <fu-icon name="search" />
          <input
            class="input input-sm"
            type="search"
            autocomplete="off"
            [id]="searchId"
            placeholder="Waits for another task — search by number or title"
            aria-label="Search tasks this one depends on"
            [attr.aria-controls]="listId"
            [value]="query()"
            (input)="query.set($any($event.target).value)"
            (keydown.escape)="query.set('')"
          />
          @if (query().trim().length >= 2) {
            <div class="menu results" [id]="listId" role="listbox" aria-label="Matching tasks">
              @if (searching() && !results().length) {
                <div class="menu-label">Searching…</div>
              }
              @for (t of results(); track t.id) {
                <button type="button" class="menu-item" role="option" (click)="addTask(t)">
                  <span class="task-number">{{ t.number }}</span>
                  <span class="grow truncate">{{ t.title }}</span>
                  <fu-status [status]="t.status" [text]="t.displayStatus" />
                </button>
              }
              @if (!searching() && !results().length) {
                <div class="menu-label">No open tasks match</div>
              }
            </div>
          }
        </div>
        <button type="button" class="btn btn-sm" (click)="addExternal()"><fu-icon name="plus" /> External dependency</button>
      </div>
    </div>
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .deps { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
    .deps li { display: flex; align-items: center; gap: 8px; padding: 6px 8px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-2); min-width: 0; }
    .deps li > fu-icon { color: var(--muted); }
    .grow { flex: 1; min-width: 0; }
    .ext { display: grid; gap: 6px; grid-template-columns: minmax(0, 2fr) minmax(0, 1.4fr) 150px; }
    @media (max-width: 700px) { .ext { grid-template-columns: minmax(0, 1fr); } }
    .search-row { display: flex; gap: 8px; align-items: flex-start; flex-wrap: wrap; }
    .search { position: relative; flex: 1 1 260px; }
    .results { left: 0; right: 0; top: calc(100% + 4px); max-height: 260px; overflow-y: auto; }
  `,
})
export class TaskDependenciesComponent {
  private readonly api = inject(TasksApi);
  private readonly fb = inject(FormBuilder).nonNullable;

  readonly rows = input.required<FormArray<DependencyRow>>();

  protected readonly searchId = `fu-dep-search-${++depSeq}`;
  protected readonly listId = `fu-dep-list-${depSeq}`;
  protected readonly query = signal('');
  protected readonly results = signal<TaskListItem[]>([]);
  protected readonly searching = signal(false);

  constructor() {
    toObservable(this.query)
      .pipe(
        map((q) => q.trim()),
        debounceTime(300),
        distinctUntilChanged(),
        tap((q) => this.searching.set(q.length >= 2)),
        switchMap((q) =>
          q.length < 2
            ? of([] as TaskListItem[])
            : this.api.query({ text: q, pageSize: 8, open: true }).pipe(
                map((page) => page?.items ?? []),
                catchError(() => of([] as TaskListItem[])),
              ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((items) => {
        const taken = new Set(this.rows().controls.map((r) => r.controls.taskId.value).filter(Boolean));
        this.results.set(items.filter((t) => !taken.has(t.id)));
        this.searching.set(false);
      });
  }

  protected addTask(t: TaskListItem): void {
    const rows = this.rows();
    if (!rows.controls.some((r) => r.controls.taskId.value === t.id)) {
      rows.push(createDependencyRow(this.fb, { taskId: t.id, label: `${t.number} · ${t.title}` }));
      rows.markAsDirty();
    }
    this.query.set('');
    this.results.set([]);
    document.getElementById(this.searchId)?.focus();
  }

  protected addExternal(): void {
    const rows = this.rows();
    rows.push(createDependencyRow(this.fb));
    rows.markAsDirty();
  }

  protected remove(index: number): void {
    const rows = this.rows();
    rows.removeAt(index);
    rows.markAsDirty();
  }
}
