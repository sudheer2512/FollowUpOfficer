/**
 * Pure helpers for CEO attention items: type → icon / tone, and the quick actions offered for an item.
 *
 * The server may suggest actions in `item.actions` (free-form keys such as "remind", "approveExtension",
 * "Accept"); they are matched case-insensitively, with close variants accepted. When it sends none, the
 * type-specific actions are inferred from `item.type`. Lifecycle actions (open task, resolve, snooze,
 * dismiss, reopen) are always offered when the item's status allows them.
 */
import { AttentionType } from '../../core/models/enums';
import { AttentionItemDto } from '../../core/models/view.models';
import { UiTone } from '../../core/util/tone';
import { IconName } from '../../shared/ui/icons';

export type AttentionActionKey =
  | 'open'
  | 'remind'
  | 'escalate'
  /** Confirm a completion awaiting approval. */
  | 'approve'
  /** Send a completion back to the assignee (approval action "Reopen"). */
  | 'reopenTask'
  | 'approveExtension'
  | 'rejectExtension'
  | 'acceptSuggestion'
  | 'dismissSuggestion'
  | 'resolve'
  | 'snooze'
  | 'dismiss'
  /** Put a resolved / dismissed / snoozed attention item back on the open list. */
  | 'reopen';

/** Button emphasis: `good` / `danger` for decisions, `quiet` for housekeeping. */
export type ActionStyle = 'good' | 'danger' | 'default' | 'quiet';

export interface AttentionAction {
  key: AttentionActionKey;
  label: string;
  icon: IconName;
  style: ActionStyle;
}

export const ATTENTION_TYPE_ICON: Record<AttentionType, IconName> = {
  CriticalOverdue: 'alert-triangle',
  ExtensionRequest: 'calendar-clock',
  RepeatedNonResponse: 'bell',
  Blocked: 'ban',
  ApprovalRequired: 'check-square',
  CommitmentMissed: 'flag',
  CriticalMessage: 'mail',
  DepartmentConflict: 'users',
  CustomerEscalation: 'alert-circle',
  AcknowledgementPending: 'user-check',
  AcknowledgementDeclined: 'x-circle',
  AiSuggestion: 'sparkles',
  EscalatedToCeo: 'zap',
  RecurringMissed: 'repeat',
};

export function attentionTypeIcon(type: AttentionType | string | null | undefined): IconName {
  return ATTENTION_TYPE_ICON[type as AttentionType] ?? 'alert-circle';
}

/**
 * Hard exceptions are red, silence / drift is orange, and decisions waiting on the executive use the accent —
 * so the icon column reads "chase" vs "decide" at a glance.
 */
export function attentionTypeTone(type: AttentionType | string | null | undefined): UiTone {
  switch (type) {
    case 'CriticalOverdue':
    case 'EscalatedToCeo':
    case 'CustomerEscalation':
    case 'CommitmentMissed':
    case 'Blocked':
    case 'AcknowledgementDeclined':
      return 'danger';
    case 'RepeatedNonResponse':
    case 'RecurringMissed':
    case 'DepartmentConflict':
    case 'CriticalMessage':
      return 'orange';
    case 'ApprovalRequired':
    case 'ExtensionRequest':
    case 'AiSuggestion':
    case 'AcknowledgementPending':
      return 'accent';
    default:
      return 'neutral';
  }
}

/** Types where the natural next step is to chase the responsible person (remind / escalate). */
const FOLLOW_UP_TYPES: ReadonlySet<string> = new Set<AttentionType>([
  'CriticalOverdue',
  'RepeatedNonResponse',
  'Blocked',
  'CommitmentMissed',
  'AcknowledgementPending',
  'EscalatedToCeo',
  'CustomerEscalation',
  'DepartmentConflict',
  'RecurringMissed',
  'CriticalMessage',
]);

const ACTION_META: Record<AttentionActionKey, Omit<AttentionAction, 'key'>> = {
  open: { label: 'Open task', icon: 'arrow-right', style: 'default' },
  remind: { label: 'Send reminder', icon: 'bell', style: 'default' },
  escalate: { label: 'Escalate', icon: 'zap', style: 'default' },
  approve: { label: 'Approve', icon: 'check', style: 'good' },
  reopenTask: { label: 'Reopen task', icon: 'rotate', style: 'default' },
  approveExtension: { label: 'Approve extension', icon: 'check', style: 'good' },
  rejectExtension: { label: 'Reject extension', icon: 'x', style: 'danger' },
  acceptSuggestion: { label: 'Accept suggestion', icon: 'sparkles', style: 'good' },
  dismissSuggestion: { label: 'Dismiss suggestion', icon: 'x', style: 'default' },
  resolve: { label: 'Resolve', icon: 'check-circle', style: 'quiet' },
  snooze: { label: 'Snooze', icon: 'clock', style: 'quiet' },
  dismiss: { label: 'Dismiss', icon: 'eye-off', style: 'quiet' },
  reopen: { label: 'Reopen', icon: 'rotate', style: 'quiet' },
};

/** Display order: decisions first, then chasing, then navigation, then housekeeping. */
const ACTION_ORDER: readonly AttentionActionKey[] = [
  'approve',
  'approveExtension',
  'acceptSuggestion',
  'remind',
  'escalate',
  'reopenTask',
  'rejectExtension',
  'dismissSuggestion',
  'open',
  'resolve',
  'snooze',
  'dismiss',
  'reopen',
];

const LIFECYCLE_ACTIONS: readonly AttentionActionKey[] = ['open', 'resolve', 'snooze', 'dismiss', 'reopen'];

type ItemShape = Pick<AttentionItemDto, 'type' | 'status'>;

/** Maps a server action key (any case or separators, close variants) to a UI action, or null when unknown. */
export function normalizeActionKey(raw: string | null | undefined, item: ItemShape): AttentionActionKey | null {
  const key = (raw ?? '').toLowerCase().replace(/[^a-z]/g, '');
  const isExtension = item.type === 'ExtensionRequest';
  const isSuggestion = item.type === 'AiSuggestion';
  switch (key) {
    case 'open':
    case 'opentask':
    case 'view':
    case 'viewtask':
      return 'open';
    case 'remind':
    case 'reminder':
    case 'sendreminder':
    case 'requestupdate':
    case 'nudge':
    case 'followup':
      return 'remind';
    case 'escalate':
    case 'escalation':
      return 'escalate';
    // Generic "yes" — meaning depends on what the item asks for.
    case 'approve':
    case 'confirm':
    case 'accept':
    case 'apply':
      return isExtension ? 'approveExtension' : isSuggestion ? 'acceptSuggestion' : 'approve';
    // Generic "no".
    case 'reject':
    case 'decline':
    case 'deny':
      return isExtension ? 'rejectExtension' : isSuggestion ? 'dismissSuggestion' : 'reopenTask';
    case 'approvecompletion':
    case 'confirmcompletion':
      return 'approve';
    case 'reopentask':
    case 'rejectcompletion':
    case 'requestmorework':
    case 'sendback':
      return 'reopenTask';
    case 'reopen':
      // On an open approval item "reopen" sends the work back; on a closed item it reopens the item itself.
      if (item.status !== 'Open') return 'reopen';
      return item.type === 'ApprovalRequired' ? 'reopenTask' : null;
    case 'approveextension':
    case 'extensionapprove':
    case 'grantextension':
    case 'acceptextension':
      return 'approveExtension';
    case 'rejectextension':
    case 'extensionreject':
    case 'declineextension':
    case 'denyextension':
      return 'rejectExtension';
    case 'acceptsuggestion':
    case 'applysuggestion':
      return 'acceptSuggestion';
    case 'dismisssuggestion':
    case 'rejectsuggestion':
    case 'ignoresuggestion':
      return 'dismissSuggestion';
    case 'resolve':
    case 'resolved':
    case 'done':
    case 'markresolved':
    case 'markdone':
      return 'resolve';
    case 'snooze':
    case 'snoozeuntil':
    case 'later':
    case 'remindlater':
      return 'snooze';
    case 'dismiss':
    case 'ignore':
    case 'hide':
      return 'dismiss';
    case 'wake':
    case 'unsnooze':
    case 'reopenitem':
      return 'reopen';
    default:
      return null;
  }
}

/** Type-specific actions used when the server suggests none. */
export function inferredActions(type: AttentionType | string): AttentionActionKey[] {
  switch (type) {
    case 'ApprovalRequired':
      return ['approve', 'reopenTask'];
    case 'ExtensionRequest':
      return ['approveExtension', 'rejectExtension'];
    case 'AiSuggestion':
      return ['acceptSuggestion', 'dismissSuggestion'];
    default:
      return FOLLOW_UP_TYPES.has(type) ? ['remind', 'escalate'] : [];
  }
}

/** Whether the item carries what the action needs (task, extension request, suggestion) and its status allows it. */
function isAvailable(key: AttentionActionKey, item: AttentionItemDto): boolean {
  const open = item.status === 'Open';
  const active = open || item.status === 'Snoozed';
  const hasTask = !!item.task?.id;
  switch (key) {
    case 'open':
      return hasTask;
    case 'remind':
    case 'escalate':
    case 'approve':
    case 'reopenTask':
      return active && hasTask;
    case 'approveExtension':
    case 'rejectExtension':
      return active && !!item.extensionRequestId;
    case 'acceptSuggestion':
    case 'dismissSuggestion':
      return active && !!item.suggestionId;
    case 'resolve':
    case 'dismiss':
      return active;
    case 'snooze':
      return open;
    case 'reopen':
      return !open;
  }
}

/** The ordered actions offered for an attention item. */
export function attentionActions(item: AttentionItemDto): AttentionAction[] {
  const keys = new Set<AttentionActionKey>(LIFECYCLE_ACTIONS);
  const suggested = (item.actions ?? [])
    .map((a) => normalizeActionKey(a, item))
    .filter((k): k is AttentionActionKey => k !== null);
  for (const k of suggested.length ? suggested : inferredActions(item.type)) keys.add(k);
  return ACTION_ORDER.filter((k) => keys.has(k) && isAvailable(k, item)).map((k) => ({ key: k, ...ACTION_META[k] }));
}

/** Splits actions into the first `count` (shown as buttons) and the rest (overflow menu). */
export function splitActions(actions: AttentionAction[], count: number): { primary: AttentionAction[]; overflow: AttentionAction[] } {
  return { primary: actions.slice(0, count), overflow: actions.slice(count) };
}
