import { WorkStatus } from '../../../core/models/enums';
import { TaskDetail, TaskPermissions } from '../../../core/models/task.models';
import { availableActions } from './task-actions';
import { normaliseDetail } from './task-detail.util';

function task(status: WorkStatus, permissions: Partial<TaskPermissions>, pausedUntil: string | null = null): TaskDetail {
  return normaliseDetail({
    summary: { status },
    followUp: { pausedUntil },
    permissions,
  } as unknown as TaskDetail);
}

const keys = (d: TaskDetail) => availableActions(d).map((a) => a.key);

describe('task quick actions', () => {
  it('managers chase first and can snooze, escalate and reassign', () => {
    const k = keys(task('InProgress', { canManage: true, canComment: true }));
    expect(k.slice(0, 3)).toEqual(['remind', 'request-update', 'comment']);
    expect(k).toContain('escalate');
    expect(k).toContain('snooze');
    expect(k).not.toContain('resume');
    expect(k).not.toContain('reopen');
    expect(k).not.toContain('complete');
  });

  it('offers resume instead of snooze while follow-up is paused', () => {
    const k = keys(task('InProgress', { canManage: true }, '2026-10-20'));
    expect(k).toContain('resume');
    expect(k).not.toContain('snooze');
  });

  it('assignees report progress first and can complete', () => {
    const k = keys(task('InProgress', { canUpdate: true, canComplete: true, canComment: true }));
    expect(k.slice(0, 3)).toEqual(['progress', 'comment', 'complete']);
    expect(k).not.toContain('remind');
  });

  it('hides work actions once completion awaits approval, but lets managers reopen', () => {
    const k = keys(task('CompletedByAssignee', { canManage: true, canUpdate: true, canComplete: true }));
    expect(k).not.toContain('complete');
    expect(k).not.toContain('progress');
    expect(k).toContain('reopen');
  });

  it('closed tasks only allow comments and reopening', () => {
    expect(keys(task('Completed', { canManage: true, canComment: true }))).toEqual(['comment', 'reopen']);
  });

  it('shows nothing without permissions', () => {
    expect(keys(task('InProgress', {}))).toEqual([]);
  });
});
