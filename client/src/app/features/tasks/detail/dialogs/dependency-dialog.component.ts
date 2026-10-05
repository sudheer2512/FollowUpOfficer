import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { catchError, debounceTime, distinctUntilChanged, map, of, switchMap, tap } from 'rxjs';
import { DependencyType } from '../../../../core/models/enums';
import { DependencyInput, TaskListItem } from '../../../../core/models/task.models';
import { DateInputComponent } from '../../../../shared/forms/date-input.component';
import { DeadlineTextComponent, StatusBadgeComponent } from '../../../../shared/domain/indicators';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ModalComponent } from '../../../../shared/ui/modal.component';
import { TaskDialogBase } from './task-dialog-base';

type Mode = 'task' | 'external';

/** Add dependency → POST /api/tasks/{id}/dependencies: blocked by another task, or by an external party. */
@Component({
  selector: 'fu-dependency-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, ModalComponent, DateInputComponent, IconComponent, StatusBadgeComponent, DeadlineTextComponent],
  template: `
    <fu-modal title="Add dependency" [subtitle]="detail().summary.number + ' is blocked by…'" size="lg" (closed)="closed.emit()">
      <div class="stack">
        <div class="segmented" role="group" aria-label="Dependency kind">
          <button type="button" [class.active]="mode() === 'task'" [attr.aria-pressed]="mode() === 'task'" (click)="mode.set('task')">Another task</button>
          <button type="button" [class.active]="mode() === 'external'" [attr.aria-pressed]="mode() === 'external'" (click)="mode.set('external')">External party</button>
        </div>

        <form id="dep-form" class="stack" [formGroup]="form" (ngSubmit)="submit()">
          @if (mode() === 'task') {
            <div class="field">
              <label for="dep-search">Find the blocking task</label>
              <div class="input-with-icon">
                <fu-icon name="search" [size]="15" />
                <input
                  id="dep-search"
                  class="input"
                  type="search"
                  autocomplete="off"
                  placeholder="Task number or title"
                  [value]="query()"
                  (input)="query.set($any($event.target).value)"
                />
              </div>
            </div>
            <div class="results card" role="listbox" aria-label="Matching tasks">
              @if (searching()) {
                <div class="small muted pad">Searching…</div>
              } @else {
                @for (t of candidates(); track t.id) {
                  <button
                    type="button"
                    role="option"
                    class="list-row"
                    [class.sel]="selected()?.id === t.id"
                    [attr.aria-selected]="selected()?.id === t.id"
                    (click)="selected.set(t)"
                  >
                    <fu-icon [name]="selected()?.id === t.id ? 'check-circle' : 'link'" [size]="15" [class.text-accent]="selected()?.id === t.id" />
                    <span class="main">
                      <span class="row gap-4"><span class="task-number">{{ t.number }}</span><span class="title truncate">{{ t.title }}</span></span>
                      <span class="meta">
                        <fu-status [status]="t.status" [text]="t.displayStatus" />
                        <span>{{ t.primaryAssignee?.name ?? t.teamName ?? 'Unassigned' }}</span>
                        <fu-deadline [text]="t.deadlineText" [overdue]="t.isOverdue" [bucket]="t.bucket" />
                      </span>
                    </span>
                  </button>
                } @empty {
                  <div class="small muted pad">{{ emptyHint() }}</div>
                }
              }
            </div>
            <div class="field">
              <label for="dep-type">Relationship</label>
              <select id="dep-type" class="select" formControlName="type">
                <option value="FinishToStart">Finish to start — this task can start when it finishes</option>
                <option value="StartToStart">Start to start — this task can start when it starts</option>
              </select>
            </div>
          } @else {
            <div class="form-grid">
              <div class="field full">
                <label for="dep-desc" class="req">What are we waiting for?</label>
                <input id="dep-desc" class="input" formControlName="externalDescription" maxlength="300" placeholder="e.g. Signed purchase order from the customer" />
              </div>
              <div class="field">
                <label for="dep-party">Party</label>
                <input id="dep-party" class="input" formControlName="externalParty" maxlength="150" placeholder="e.g. Acme Logistics" />
              </div>
              <div class="field">
                <label for="dep-expected">Expected by</label>
                <fu-date-input inputId="dep-expected" formControlName="expectedBy" />
              </div>
            </div>
          }
        </form>
      </div>
      <ng-container modal-footer>
        <button type="button" class="btn" (click)="closed.emit()">Cancel</button>
        <button type="submit" form="dep-form" class="btn btn-primary" [disabled]="busy() || !valid()">Add dependency</button>
      </ng-container>
    </fu-modal>
  `,
  styles: `
    .results { max-height: 280px; overflow-y: auto; box-shadow: none; }
    .results .list-row.sel { background: var(--accent-soft); }
    .pad { padding: 12px 16px; }
  `,
})
export class DependencyDialogComponent extends TaskDialogBase {
  protected readonly mode = signal<Mode>('task');
  protected readonly query = signal('');
  protected readonly selected = signal<TaskListItem | null>(null);
  protected readonly searching = signal(false);

  private readonly results = toSignal(
    toObservable(this.query).pipe(
      debounceTime(300),
      map((q) => q.trim()),
      distinctUntilChanged(),
      tap((q) => this.searching.set(q.length >= 2)),
      switchMap((q) =>
        q.length < 2
          ? of<TaskListItem[]>([])
          : this.api.query({ text: q, pageSize: 8 }).pipe(
              map((page) => page?.items ?? []),
              catchError(() => of<TaskListItem[]>([])),
            ),
      ),
      tap(() => this.searching.set(false)),
    ),
    { initialValue: [] as TaskListItem[] },
  );

  /** Search hits minus this task and tasks it already depends on. */
  protected readonly candidates = computed(() => {
    const self = this.detail().summary.id;
    const linked = new Set(this.detail().dependencies.map((d) => d.taskId).filter((id): id is string => !!id));
    return this.results().filter((t) => t.id !== self && !linked.has(t.id));
  });

  protected readonly form = inject(FormBuilder).nonNullable.group({
    type: ['FinishToStart' as DependencyType],
    externalDescription: [''],
    externalParty: [''],
    expectedBy: [''],
  });

  protected readonly emptyHint = computed(() =>
    this.query().trim().length >= 2 ? 'No matching tasks.' : 'Type at least two characters of a task number or title.',
  );

  protected valid(): boolean {
    return this.mode() === 'task' ? !!this.selected() : this.form.controls.externalDescription.value.trim().length > 0;
  }

  protected submit(): void {
    if (!this.valid()) return;
    const v = this.form.getRawValue();
    const target = this.selected();
    const req: DependencyInput =
      this.mode() === 'task' && target
        ? { taskId: target.id, type: v.type }
        : {
            type: 'External',
            externalDescription: v.externalDescription.trim(),
            externalParty: v.externalParty.trim() || null,
            expectedBy: v.expectedBy || null,
          };
    this.run(this.api.addDependency(this.taskId(), req), target && this.mode() === 'task' ? `Now blocked by ${target.number}` : 'External dependency added');
  }
}
