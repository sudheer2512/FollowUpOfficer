import { ActionItemDto } from '../../core/models/admin.models';
import { MeetingActionStatus } from '../../core/models/enums';
import { UiTone } from '../../core/util/tone';

export interface ActionItemCounts {
  total: number;
  /** Still open and without a task. */
  open: number;
  /** Turned into a task. */
  withTask: number;
}

export function actionItemCounts(items: readonly ActionItemDto[] | null | undefined): ActionItemCounts {
  const list = items ?? [];
  return {
    total: list.length,
    open: list.filter((a) => a.status === 'Open' && !a.taskId).length,
    withTask: list.filter((a) => !!a.taskId || a.status === 'TaskCreated').length,
  };
}

/** Action items that can still become tasks: open and not linked to a task yet. */
export function taskCandidates(items: readonly ActionItemDto[] | null | undefined): ActionItemDto[] {
  return (items ?? []).filter((a) => !a.taskId && (a.status ?? 'Open') === 'Open');
}

export function actionStatusTone(s: MeetingActionStatus | string | null | undefined): UiTone {
  switch (s) {
    case 'TaskCreated':
      return 'accent';
    case 'Done':
      return 'good';
    case 'Cancelled':
      return 'neutral';
    default:
      return 'warning';
  }
}
