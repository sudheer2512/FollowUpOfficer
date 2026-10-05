import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { TasksApi } from '../../../../core/api/tasks.api';
import { DependencyDto, TaskDetail } from '../../../../core/models/task.models';
import { todayDateOnly } from '../../../../core/util/dates';
import { StatusBadgeComponent } from '../../../../shared/domain/indicators';
import { DateOnlyPipe, HumanizePipe } from '../../../../shared/pipes/format.pipes';
import { DialogService } from '../../../../shared/ui/dialog.service';
import { IconComponent } from '../../../../shared/ui/icon.component';
import { ToastService } from '../../../../shared/ui/toast.service';
import { DialogRequest } from '../task-actions';

/** "Blocked by TASK-123", external dependencies (resolve / remove), and the tasks this one blocks. */
@Component({
  selector: 'fu-dependencies-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, StatusBadgeComponent, DateOnlyPipe, HumanizePipe],
  template: `
    <section class="card">
      <header class="card-header">
        <h2>Dependencies</h2>
        @if (openCount()) {
          <span class="count-pill danger" [attr.title]="openCount() + ' unresolved'">{{ openCount() }}</span>
        }
        <span class="spacer"></span>
        @if (canEdit()) {
          <button type="button" class="btn btn-xs btn-ghost" (click)="open.emit({ kind: 'dependency' })"><fu-icon name="plus" [size]="13" /> Add</button>
        }
      </header>
      <div class="card-body flush">
        @if (!blockedBy().length && !blocks().length) {
          <div class="muted small pad">Nothing is blocking this task.</div>
        }
        <div class="list">
          @for (dep of blockedBy(); track dep.id) {
            <div class="list-row" [class.resolved]="dep.isResolved">
              <fu-icon [name]="dep.taskId ? 'link' : 'globe'" [size]="15" class="muted" />
              <div class="main">
                @if (dep.taskId) {
                  <div class="title">
                    <span class="muted small">Blocked by</span>
                    <a [routerLink]="['/tasks', dep.taskId]"><span class="task-number">{{ dep.number }}</span></a>
                    {{ dep.title }}
                  </div>
                  <div class="meta">
                    @if (dep.status) {
                      <fu-status [status]="dep.status" />
                    }
                    <span>{{ dep.type | humanize }}</span>
                  </div>
                } @else {
                  <div class="title">{{ dep.externalDescription || 'External dependency' }}</div>
                  <div class="meta">
                    @if (dep.externalParty) {
                      <span>{{ dep.externalParty }}</span>
                    }
                    @if (dep.expectedBy) {
                      <span [class.text-danger]="isLate(dep)">expected {{ dep.expectedBy | dateOnly: 'weekday' }}</span>
                    }
                  </div>
                }
              </div>
              @if (dep.isResolved) {
                <span class="chip sm tone-good">Resolved</span>
              } @else if (dep.isBlocking) {
                <span class="chip sm tone-danger">Blocking</span>
              }
              @if (canEdit() && !dep.taskId && !dep.isResolved) {
                <button type="button" class="btn btn-xs" [disabled]="busyId() === dep.id" (click)="resolve(dep)">Resolve</button>
              }
              @if (canEdit()) {
                <button type="button" class="btn btn-ghost btn-icon btn-xs" aria-label="Remove dependency" [disabled]="busyId() === dep.id" (click)="remove(dep)">
                  <fu-icon name="trash" [size]="13" />
                </button>
              }
            </div>
          }
          @if (blocks().length) {
            <div class="list-row sub-head"><span class="section-title" style="margin:0">This task blocks</span></div>
            @for (dep of blocks(); track dep.id) {
              <a class="list-row" [routerLink]="['/tasks', dep.taskId]">
                <fu-icon name="arrow-right" [size]="15" class="muted" />
                <div class="main">
                  <div class="title"><span class="task-number">{{ dep.number }}</span> {{ dep.title }}</div>
                </div>
                @if (dep.status) {
                  <fu-status [status]="dep.status" />
                }
              </a>
            }
          }
        </div>
      </div>
    </section>
  `,
  styles: `
    .pad { padding: 14px 16px; }
    .resolved .title { color: var(--muted); text-decoration: line-through; text-decoration-color: var(--subtle); }
    .sub-head { background: var(--surface-2); padding-top: 8px; padding-bottom: 6px; }
  `,
})
export class DependenciesCardComponent {
  private readonly api = inject(TasksApi);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);

  readonly detail = input.required<TaskDetail>();
  readonly updated = output<TaskDetail>();
  readonly open = output<DialogRequest>();

  protected readonly busyId = signal<string | null>(null);
  protected readonly blockedBy = computed(() => this.detail().dependencies.filter((d) => d.direction !== 'Blocks'));
  protected readonly blocks = computed(() => this.detail().dependencies.filter((d) => d.direction === 'Blocks' && !!d.taskId));
  protected readonly openCount = computed(() => this.blockedBy().filter((d) => !d.isResolved).length);
  protected readonly canEdit = computed(() => {
    const p = this.detail().permissions;
    return p.canManage || p.canUpdate;
  });
  private readonly today = todayDateOnly();

  protected isLate(dep: DependencyDto): boolean {
    return !dep.isResolved && !!dep.expectedBy && dep.expectedBy < this.today;
  }

  protected resolve(dep: DependencyDto): void {
    this.mutate(dep, this.api.resolveDependency(this.detail().summary.id, dep.id), 'Dependency resolved');
  }

  protected async remove(dep: DependencyDto): Promise<void> {
    const what = dep.taskId ? `the dependency on ${dep.number}` : `“${dep.externalDescription ?? 'external dependency'}”`;
    const ok = await this.dialog.confirm({ title: 'Remove dependency', message: `Remove ${what}?`, confirmText: 'Remove', danger: true });
    if (ok) this.mutate(dep, this.api.removeDependency(this.detail().summary.id, dep.id), 'Dependency removed');
  }

  private mutate(dep: DependencyDto, command: Observable<TaskDetail>, success: string): void {
    this.busyId.set(dep.id);
    command.subscribe({
      next: (d) => {
        this.busyId.set(null);
        this.toast.success(success);
        this.updated.emit(d);
      },
      error: () => this.busyId.set(null),
    });
  }
}
