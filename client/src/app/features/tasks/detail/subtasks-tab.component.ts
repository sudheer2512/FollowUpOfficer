import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TasksApi } from '../../../core/api/tasks.api';
import { SubtaskNode, TaskDetail } from '../../../core/models/task.models';
import { DateInputComponent } from '../../../shared/forms/date-input.component';
import { PersonPickerComponent } from '../../../shared/forms/person-picker.component';
import { EmptyStateComponent } from '../../../shared/ui/empty-state.component';
import { IconComponent } from '../../../shared/ui/icon.component';
import { ToastService } from '../../../shared/ui/toast.service';
import { SubtaskNodeComponent } from './subtasks/subtask-node.component';
import { countSubtasks, isClosed } from './task-detail.util';

function countOpen(nodes: SubtaskNode[]): number {
  return nodes.reduce((n, node) => n + (isClosed(node.status) ? 0 : 1) + countOpen(node.children ?? []), 0);
}

/** Subtasks as a tree, with an inline quick-add (title, assignee, due date) and a link to the full delegate dialog. */
@Component({
  selector: 'fu-subtasks-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, IconComponent, EmptyStateComponent, PersonPickerComponent, DateInputComponent, SubtaskNodeComponent],
  template: `
    @let d = detail();
    <section class="card">
      <header class="card-header">
        <h2>Subtasks</h2>
        <span class="count-pill">{{ total() }}</span>
        @if (total()) {
          <span class="small muted">{{ open() }} open</span>
        }
        <span class="spacer"></span>
        @if (canAdd()) {
          <button type="button" class="btn btn-sm" (click)="openDelegate.emit()"><fu-icon name="plus" [size]="14" /> New subtask…</button>
        }
      </header>

      @if (canAdd()) {
        <form class="quick-add" [formGroup]="form" (ngSubmit)="add()" aria-label="Quick add subtask">
          <div class="field grow">
            <label for="qa-title" class="sr-only">Subtask title</label>
            <input id="qa-title" class="input" formControlName="title" maxlength="200" placeholder="Add a subtask — e.g. Collect vendor quotations" autocomplete="off" />
          </div>
          <div class="field who">
            <label for="qa-assignee" class="sr-only">Assignee</label>
            <fu-person-picker inputId="qa-assignee" formControlName="primaryAssigneeId" placeholder="Assignee" />
          </div>
          <div class="field due">
            <label for="qa-due" class="sr-only">Due date</label>
            <fu-date-input inputId="qa-due" formControlName="dueDate" ariaLabel="Due date" />
          </div>
          <button type="submit" class="btn btn-primary" [disabled]="adding() || form.invalid"><fu-icon name="plus" [size]="14" /> Add</button>
        </form>
      }

      <div class="card-body flush tree" role="tree" aria-label="Subtasks">
        @for (n of d.subtasks; track n.id) {
          <fu-subtask-node [node]="n" [level]="0" />
        } @empty {
          <fu-empty icon="git-branch" title="No subtasks" message="Break larger work into subtasks; each one gets its own owner, deadline and follow-up." [compact]="true" />
        }
      </div>
    </section>
  `,
  styles: `
    .quick-add { display: flex; gap: 8px; align-items: flex-start; padding: 12px 16px; border-bottom: 1px solid var(--border); background: var(--surface-2); flex-wrap: wrap; }
    .quick-add .grow { flex: 1 1 260px; }
    .quick-add .who { flex: 0 1 220px; }
    .quick-add .due { flex: 0 1 160px; }
    .tree { overflow-x: auto; }
  `,
})
export class SubtasksTabComponent {
  private readonly api = inject(TasksApi);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  readonly detail = input.required<TaskDetail>();
  /** A subtask was created inline; the page refreshes the task. */
  readonly reload = output<void>();
  /** Open the full delegate dialog for a subtask. */
  readonly openDelegate = output<void>();

  protected readonly adding = signal(false);
  protected readonly total = computed(() => countSubtasks(this.detail().subtasks));
  protected readonly open = computed(() => countOpen(this.detail().subtasks));
  protected readonly canAdd = computed(() => {
    const d = this.detail();
    return (d.permissions.canManage || d.permissions.canUpdate) && !isClosed(d.summary.status);
  });

  protected readonly form = this.fb.group({
    title: this.fb.nonNullable.control('', [Validators.required, Validators.maxLength(200), Validators.pattern(/\S/)]),
    primaryAssigneeId: this.fb.control<string | null>(null),
    dueDate: this.fb.control<string | null>(null),
  });

  protected add(): void {
    if (this.form.invalid || this.adding()) return;
    const { title, primaryAssigneeId, dueDate } = this.form.getRawValue();
    this.adding.set(true);
    this.api.createSubtask(this.detail().summary.id, { title: title.trim(), primaryAssigneeId, dueDate }).subscribe({
      next: (created) => {
        this.adding.set(false);
        const number = created?.summary?.number;
        this.toast.success(number ? `Subtask ${number} created` : 'Subtask created');
        this.form.reset({ title: '', primaryAssigneeId, dueDate });
        this.reload.emit();
      },
      error: () => this.adding.set(false),
    });
  }
}
