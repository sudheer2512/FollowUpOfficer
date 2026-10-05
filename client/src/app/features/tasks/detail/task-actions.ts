/**
 * Quick actions on the task detail page (spec §57) and the dialogs they open. `availableActions` decides — from the
 * server's `permissions` and the task state — which actions the current user sees, in the order that suits their role.
 */
import { AcknowledgementResponse, ApprovalAction } from '../../../core/models/enums';
import { ExtensionDto, TaskDetail } from '../../../core/models/task.models';
import { isAwaitingApproval, isClosed, isReopenable } from './task-detail.util';

/** Reminder variants that all post ReminderRequest to /remind. */
export type ReminderMode = 'reminder' | 'update' | 'email' | 'teams';

/** A templated dialog the page should open. */
export type DialogRequest =
  | { kind: 'reminder'; mode: ReminderMode }
  | { kind: 'comment' }
  | { kind: 'progress' }
  | { kind: 'status' }
  | { kind: 'due-date' }
  | { kind: 'reassign' }
  | { kind: 'escalate' }
  | { kind: 'snooze' }
  | { kind: 'complete' }
  | { kind: 'acknowledge'; response: Exclude<AcknowledgementResponse, 'Accept'> }
  | { kind: 'approval'; action: ApprovalAction }
  | { kind: 'request-extension' }
  | { kind: 'decide-extension'; extension: ExtensionDto; approve: boolean }
  | { kind: 'commitment' }
  | { kind: 'dependency' }
  | { kind: 'link-message' };

export type QuickActionKey =
  | 'remind'
  | 'request-update'
  | 'email'
  | 'teams'
  | 'comment'
  | 'progress'
  | 'status'
  | 'due-date'
  | 'reassign'
  | 'subtask'
  | 'escalate'
  | 'snooze'
  | 'resume'
  | 'complete'
  | 'reopen'
  | 'request-extension'
  | 'commitment';

export interface QuickAction {
  key: QuickActionKey;
  label: string;
  icon: string;
  /** Visual emphasis for the inline button. */
  emphasis?: 'primary';
  /** The dialog to open; actions without one run directly (subtask, resume, reopen). */
  dialog?: DialogRequest;
}

const ACTIONS: Record<QuickActionKey, QuickAction> = {
  remind: { key: 'remind', label: 'Send reminder', icon: 'bell', dialog: { kind: 'reminder', mode: 'reminder' } },
  'request-update': { key: 'request-update', label: 'Request update', icon: 'refresh', dialog: { kind: 'reminder', mode: 'update' } },
  email: { key: 'email', label: 'Send email', icon: 'mail', dialog: { kind: 'reminder', mode: 'email' } },
  teams: { key: 'teams', label: 'Send Teams message', icon: 'message', dialog: { kind: 'reminder', mode: 'teams' } },
  comment: { key: 'comment', label: 'Add comment', icon: 'messages', dialog: { kind: 'comment' } },
  progress: { key: 'progress', label: 'Update progress', icon: 'trending-up', dialog: { kind: 'progress' } },
  status: { key: 'status', label: 'Change status', icon: 'activity', dialog: { kind: 'status' } },
  'due-date': { key: 'due-date', label: 'Change due date', icon: 'calendar', dialog: { kind: 'due-date' } },
  reassign: { key: 'reassign', label: 'Reassign', icon: 'user-plus', dialog: { kind: 'reassign' } },
  subtask: { key: 'subtask', label: 'Create subtask', icon: 'git-branch' },
  escalate: { key: 'escalate', label: 'Escalate', icon: 'zap', dialog: { kind: 'escalate' } },
  snooze: { key: 'snooze', label: 'Snooze follow-up', icon: 'pause', dialog: { kind: 'snooze' } },
  resume: { key: 'resume', label: 'Resume follow-up', icon: 'play' },
  complete: { key: 'complete', label: 'Mark complete', icon: 'check-circle', emphasis: 'primary', dialog: { kind: 'complete' } },
  reopen: { key: 'reopen', label: 'Reopen', icon: 'rotate' },
  'request-extension': { key: 'request-extension', label: 'Request extension', icon: 'calendar-clock', dialog: { kind: 'request-extension' } },
  commitment: { key: 'commitment', label: 'Add commitment', icon: 'flag', dialog: { kind: 'commitment' } },
};

/** Managers chase; assignees report. The first few actions in each order are shown inline. */
const MANAGER_ORDER: QuickActionKey[] = [
  'remind', 'request-update', 'comment', 'due-date', 'reassign', 'escalate', 'snooze', 'resume', 'email', 'teams',
  'progress', 'status', 'complete', 'request-extension', 'commitment', 'subtask', 'reopen',
];
const ASSIGNEE_ORDER: QuickActionKey[] = [
  'progress', 'comment', 'complete', 'status', 'request-extension', 'commitment', 'subtask', 'reopen',
];

/** Which quick actions the current user may take on this task, in display order. */
export function availableActions(d: TaskDetail): QuickAction[] {
  const p = d.permissions;
  const status = d.summary.status;
  const open = !isClosed(status);
  const working = open && !isAwaitingApproval(status);
  const paused = !!d.followUp.pausedUntil;

  const allowed: Record<QuickActionKey, boolean> = {
    remind: p.canManage && open,
    'request-update': p.canManage && open,
    email: p.canManage && open,
    teams: p.canManage && open,
    comment: p.canComment,
    progress: p.canUpdate && working,
    status: p.canUpdate && working,
    'due-date': p.canManage && open,
    reassign: p.canManage && open,
    subtask: (p.canManage || p.canUpdate) && open,
    escalate: p.canManage && open,
    snooze: p.canManage && open && !paused,
    resume: p.canManage && open && paused,
    complete: p.canComplete && working,
    reopen: p.canManage && isReopenable(status),
    'request-extension': p.canUpdate && working,
    commitment: (p.canUpdate || p.canManage) && open,
  };
  const order = p.canManage ? MANAGER_ORDER : ASSIGNEE_ORDER;
  return order.filter((k) => allowed[k]).map((k) => ACTIONS[k]);
}
