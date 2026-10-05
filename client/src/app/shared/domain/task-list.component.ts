import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TaskListItem } from '../../core/models/task.models';
import { EmptyStateComponent } from '../ui/empty-state.component';
import { IconComponent } from '../ui/icon.component';
import { CriticalityChipComponent, DeadlineTextComponent, HealthDotComponent, StatusBadgeComponent } from './indicators';

/**
 * Compact task rows for dashboard cards: number · title / responsible · deadline text · criticality.
 *   <fu-task-list [tasks]="section.tasks" [limit]="6" emptyMessage="Nothing overdue" />
 */
@Component({
  selector: 'fu-task-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, EmptyStateComponent, HealthDotComponent, CriticalityChipComponent, DeadlineTextComponent, StatusBadgeComponent],
  template: `
    <div class="list">
      @for (t of visible(); track t.id) {
        <a class="list-row" [routerLink]="['/tasks', t.id]">
          <fu-health [value]="t.health" [showLabel]="false" />
          <div class="main">
            <div class="row gap-4">
              <span class="task-number">{{ t.number }}</span>
              @if (t.isRecurring) {
                <fu-icon name="repeat" [size]="12" class="muted" />
              }
              <span class="title truncate">{{ t.title }}</span>
            </div>
            <div class="meta">
              <span class="truncate">{{ t.primaryAssignee?.name ?? t.teamName ?? 'Unassigned' }}</span>
              @if (showStatus()) {
                <fu-status [status]="t.status" [text]="t.displayStatus" />
              }
              @if (detail() === 'noResponse' && t.noResponseDays > 0) {
                <span class="text-orange">No response for {{ t.noResponseDays }} day{{ t.noResponseDays === 1 ? '' : 's' }}</span>
              }
              @if (detail() === 'commitment' && t.openCommitmentDate) {
                <span>Committed for {{ t.openCommitmentDate }}</span>
              }
              @if (detail() === 'blocked' && t.blockedReason) {
                <span class="text-danger truncate">{{ t.blockedReason }}</span>
              }
              @if (detail() === 'completed' && t.completedAt) {
                <span class="text-good">Completed</span>
              }
            </div>
          </div>
          <div class="side">
            <fu-deadline [text]="t.deadlineText" [overdue]="t.isOverdue" [bucket]="t.bucket" />
            @if (t.criticality !== 'Normal' && t.criticality !== 'Informational') {
              <fu-criticality [value]="t.criticality" [small]="true" />
            }
          </div>
        </a>
      } @empty {
        <fu-empty [icon]="emptyIcon()" [title]="emptyTitle()" [message]="emptyMessage()" [compact]="true" />
      }
    </div>
    @if (limit() && tasks().length > limit()!) {
      <div class="more small muted">+ {{ tasks().length - limit()! }} more</div>
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    .side { display: flex; flex-direction: column; align-items: flex-end; gap: 4px; flex: none; }
    .list-row { padding: 9px 16px; }
    .more { padding: 8px 16px; border-top: 1px solid var(--border); }
  `,
})
export class TaskListComponent {
  readonly tasks = input<TaskListItem[]>([]);
  readonly limit = input<number | null>(null);
  readonly showStatus = input(false);
  /** Extra detail line: 'noResponse' | 'commitment' | 'blocked' | 'completed'. */
  readonly detail = input<string | null>(null);
  readonly emptyTitle = input('');
  readonly emptyMessage = input('Nothing here');
  readonly emptyIcon = input('check-circle');

  protected readonly visible = computed(() => {
    const l = this.limit();
    return l ? this.tasks().slice(0, l) : this.tasks();
  });
}
