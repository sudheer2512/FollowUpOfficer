import { Directive, inject, input, output, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { TasksApi } from '../../../../core/api/tasks.api';
import { Guid } from '../../../../core/models/common.models';
import { TaskDetail } from '../../../../core/models/task.models';
import { ToastService } from '../../../../shared/ui/toast.service';

/**
 * Shared plumbing for the task action dialogs: the task in, the updated task out, a busy flag, and `run()` which posts a
 * command, toasts on success and emits the TaskDetail the API returns (so the page never refetches).
 * HTTP errors are already toasted by the interceptor; the dialog simply stays open for another attempt.
 */
@Directive()
export abstract class TaskDialogBase {
  readonly detail = input.required<TaskDetail>();
  /** The updated task returned by the command. */
  readonly done = output<TaskDetail>();
  readonly closed = output<void>();

  protected readonly api = inject(TasksApi);
  protected readonly toast = inject(ToastService);
  protected readonly busy = signal(false);

  protected taskId(): Guid {
    return this.detail().summary.id;
  }

  protected run(command: Observable<TaskDetail>, success: string): void {
    if (this.busy()) return;
    this.busy.set(true);
    command.subscribe({
      next: (updated) => {
        this.busy.set(false);
        this.toast.success(success);
        this.done.emit(updated);
      },
      error: () => this.busy.set(false),
    });
  }
}
