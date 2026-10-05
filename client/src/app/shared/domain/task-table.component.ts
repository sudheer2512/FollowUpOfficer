import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TaskListItem } from '../../core/models/task.models';
import { AgoPipe } from '../pipes/format.pipes';
import { EmptyStateComponent } from '../ui/empty-state.component';
import { IconComponent } from '../ui/icon.component';
import { SkeletonComponent } from '../ui/skeleton.component';
import {
  CriticalityChipComponent,
  DeadlineTextComponent,
  HealthDotComponent,
  PersonComponent,
  PriorityChipComponent,
  ScoreBarComponent,
  StatusBadgeComponent,
} from './indicators';

export type TaskColumn =
  | 'number' | 'title' | 'status' | 'health' | 'attention' | 'responsible' | 'due' | 'criticality' | 'priority' | 'updated'
  | 'progress' | 'delegator' | 'team';

export const DEFAULT_TASK_COLUMNS: TaskColumn[] = [
  'number', 'title', 'status', 'health', 'attention', 'responsible', 'due', 'criticality', 'priority', 'updated',
];
export const COMPACT_TASK_COLUMNS: TaskColumn[] = ['number', 'title', 'status', 'responsible', 'due', 'criticality'];

const SORTABLE: Partial<Record<TaskColumn, string>> = {
  number: 'number',
  attention: 'attention',
  due: 'due',
  priority: 'priority',
  updated: 'updated',
};

/**
 * Task table for TaskListItem lists (task list, project, team, ask, review, reports…).
 * Rows are links (keyboard reachable: Tab + Enter). Optional server-side sort via (sortChange).
 *   <fu-task-table [tasks]="page.items" [columns]="cols" [sort]="filter.sort" (sortChange)="setSort($event)" />
 */
@Component({
  selector: 'fu-task-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    AgoPipe,
    IconComponent,
    EmptyStateComponent,
    SkeletonComponent,
    HealthDotComponent,
    CriticalityChipComponent,
    PriorityChipComponent,
    StatusBadgeComponent,
    ScoreBarComponent,
    DeadlineTextComponent,
    PersonComponent,
  ],
  template: `
    @if (loading() && !tasks().length) {
      <fu-skeleton variant="table" [rows]="6" />
    } @else if (!tasks().length) {
      <fu-empty [icon]="emptyIcon()" [title]="emptyTitle()" [message]="emptyMessage()" [compact]="compact()" />
    } @else {
      <div class="table-wrap">
        <table class="table" [class.compact]="compact()">
          <thead>
            <tr>
              @for (c of columns(); track c) {
                <th
                  [class.sortable]="isSortable(c)"
                  [class.num]="c === 'attention'"
                  [attr.aria-sort]="sort() === sortKey(c) ? 'descending' : null"
                  (click)="isSortable(c) && sortChange.emit(sortKey(c)!)"
                >
                  {{ headers[c] }}
                  @if (sort() === sortKey(c)) {
                    <fu-icon name="arrow-down" [size]="11" style="display:inline-flex;vertical-align:-1px" />
                  }
                </th>
              }
            </tr>
          </thead>
          <tbody>
            @for (t of visible(); track t.id) {
              <tr class="clickable" tabindex="0" [attr.aria-label]="t.number + ' ' + t.title" (click)="open(t, $event)" (keydown.enter)="open(t, $event)">
                @for (c of columns(); track c) {
                  @switch (c) {
                    @case ('number') {
                      <td class="nowrap"><a class="task-number" [routerLink]="['/tasks', t.id]" tabindex="-1">{{ t.number }}</a></td>
                    }
                    @case ('title') {
                      <td class="title-cell">
                        <div class="row gap-4" style="min-width:0">
                          @if (t.visibility !== 'Normal') {
                            <fu-icon name="lock" [size]="13" class="muted" [label]="'Restricted visibility'" />
                          }
                          @if (t.isRecurring) {
                            <fu-icon name="repeat" [size]="13" class="muted" [label]="'Recurring'" />
                          }
                          <span class="ttl" [class.clamp-2]="!compact()" [class.truncate]="compact()">{{ t.title }}</span>
                          @if (t.isBlocked) {
                            <span class="chip sm tone-danger" [attr.title]="t.blockedReason ?? 'Blocked'">Blocked</span>
                          }
                        </div>
                        @if (!compact() && (t.parentNumber || t.tags.length || t.projectName)) {
                          <div class="sub">
                            @if (t.parentNumber) {
                              <span>↳ {{ t.parentNumber }}</span>
                            }
                            @if (t.projectName) {
                              <span>{{ t.projectName }}</span>
                            }
                            @for (tag of t.tags; track tag) {
                              <span class="tag">#{{ tag }}</span>
                            }
                          </div>
                        }
                      </td>
                    }
                    @case ('status') {
                      <td><fu-status [status]="t.status" [text]="t.displayStatus" /></td>
                    }
                    @case ('health') {
                      <td><fu-health [value]="t.health" /></td>
                    }
                    @case ('attention') {
                      <td class="num"><fu-score [value]="t.attentionScore" /></td>
                    }
                    @case ('responsible') {
                      <td class="person-cell"><fu-person [person]="t.primaryAssignee" [name]="t.teamName" /></td>
                    }
                    @case ('delegator') {
                      <td><fu-person [person]="t.delegator" emptyText="—" /></td>
                    }
                    @case ('team') {
                      <td class="muted small">{{ t.teamName ?? t.departmentName ?? '—' }}</td>
                    }
                    @case ('due') {
                      <td><fu-deadline [text]="t.deadlineText" [overdue]="t.isOverdue" [bucket]="t.bucket" /></td>
                    }
                    @case ('criticality') {
                      <td><fu-criticality [value]="t.criticality" [small]="true" /></td>
                    }
                    @case ('priority') {
                      <td><fu-priority [value]="t.priority" /></td>
                    }
                    @case ('progress') {
                      <td>
                        <div class="row"><div class="progress" style="width:60px"><span [style.width.%]="t.progressPercent"></span></div><span class="small muted num">{{ t.progressPercent }}%</span></div>
                      </td>
                    }
                    @case ('updated') {
                      <td class="small muted upd">
                        <div class="nowrap">{{ t.lastActivityAt | ago }}</div>
                        @if (t.lastUpdateSummary && !compact()) {
                          <div class="truncate" style="max-width:220px" [attr.title]="t.lastUpdateSummary">{{ t.lastUpdateSummary }}</div>
                        }
                      </td>
                    }
                  }
                }
              </tr>
            }
          </tbody>
        </table>
      </div>
      @if (limit() && tasks().length > limit()!) {
        <div class="more small muted">+ {{ tasks().length - limit()! }} more</div>
      }
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .title-cell { min-width: 220px; }
    .ttl { font-weight: 500; }
    .sub { display: flex; gap: 8px; flex-wrap: wrap; font-size: 12px; color: var(--muted); margin-top: 2px; }
    .person-cell { min-width: 140px; max-width: 200px; }
    .upd { min-width: 90px; }
    .more { padding: 8px 16px; border-top: 1px solid var(--border); }
  `,
})
export class TaskTableComponent {
  private readonly router = inject(Router);

  readonly tasks = input<TaskListItem[]>([]);
  readonly columns = input<TaskColumn[]>(DEFAULT_TASK_COLUMNS);
  readonly loading = input(false);
  readonly compact = input(false);
  /** Current server sort key (attention | due | updated | number | priority). */
  readonly sort = input<string | null | undefined>(null);
  /** Show at most N rows. */
  readonly limit = input<number | null>(null);
  readonly emptyTitle = input('No tasks');
  readonly emptyMessage = input('');
  readonly emptyIcon = input('check-circle');
  readonly sortChange = output<string>();

  protected readonly visible = computed(() => {
    const l = this.limit();
    return l ? this.tasks().slice(0, l) : this.tasks();
  });

  protected readonly headers: Record<TaskColumn, string> = {
    number: 'Number',
    title: 'Title',
    status: 'Status',
    health: 'Health',
    attention: 'Attention',
    responsible: 'Responsible',
    delegator: 'Delegated by',
    team: 'Team',
    due: 'Due',
    criticality: 'Criticality',
    priority: 'Priority',
    progress: 'Progress',
    updated: 'Last update',
  };

  protected isSortable(c: TaskColumn): boolean {
    return !!SORTABLE[c];
  }

  protected sortKey(c: TaskColumn): string | null {
    return SORTABLE[c] ?? null;
  }

  protected open(t: TaskListItem, e: Event): void {
    const target = e.target as HTMLElement;
    if (target.closest('a') && e.type === 'click') return; // the link handles it
    if (e instanceof MouseEvent && (e.ctrlKey || e.metaKey)) {
      window.open(`/tasks/${t.id}`, '_blank');
      return;
    }
    void this.router.navigate(['/tasks', t.id]);
  }
}
