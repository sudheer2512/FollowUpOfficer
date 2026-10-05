import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DateOnly } from '../../core/models/common.models';
import { ProjectSummary } from '../../core/models/view.models';
import { diffDays, todayDateOnly } from '../../core/util/dates';
import { UiTone } from '../../core/util/tone';

/** The four derived project states the server reports ("On Track", "At Risk", "Off Track", "Completed"). */
export type ProjectStatusKey = 'ontrack' | 'atrisk' | 'offtrack' | 'completed' | 'other';

/** Normalises the server's status text (case, spaces, dashes and underscores are ignored). */
export function projectStatusKey(status: string | null | undefined): ProjectStatusKey {
  const s = (status ?? '').toLowerCase().replace(/[\s_-]+/g, '');
  switch (s) {
    case 'ontrack':
      return 'ontrack';
    case 'atrisk':
      return 'atrisk';
    case 'offtrack':
      return 'offtrack';
    case 'completed':
    case 'complete':
    case 'done':
      return 'completed';
    default:
      return 'other';
  }
}

/** On Track good · At Risk orange · Off Track danger · Completed accent. */
export function projectStatusTone(status: string | null | undefined): UiTone {
  switch (projectStatusKey(status)) {
    case 'ontrack':
      return 'good';
    case 'atrisk':
      return 'orange';
    case 'offtrack':
      return 'danger';
    case 'completed':
      return 'accent';
    default:
      return 'neutral';
  }
}

/** Progress-bar modifier class for a project status. */
export function projectProgressClass(status: string | null | undefined): string {
  switch (projectStatusKey(status)) {
    case 'completed':
    case 'ontrack':
      return 'good';
    case 'atrisk':
      return 'warning';
    case 'offtrack':
      return 'danger';
    default:
      return '';
  }
}

/** True when the target date has passed and the project is not completed. */
export function isTargetMissed(p: Pick<ProjectSummary, 'targetDate' | 'status'>, today: DateOnly = todayDateOnly()): boolean {
  if (!p.targetDate || projectStatusKey(p.status) === 'completed') return false;
  return diffDays(today, p.targetDate) < 0;
}

/** Project status badge. */
@Component({
  selector: 'fu-project-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge tone-{{ tone() }}">{{ status() || 'Unknown' }}</span>`,
  host: { style: 'display:inline-flex' },
})
export class ProjectStatusComponent {
  readonly status = input<string | null | undefined>('');
  protected readonly tone = computed(() => projectStatusTone(this.status()));
}
