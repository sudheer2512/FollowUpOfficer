import { TaskListItem } from '../../core/models/task.models';
import { AttentionItemDto } from '../../core/models/view.models';
import { attentionActions, attentionTypeIcon, normalizeActionKey, splitActions } from './attention-actions';

function item(overrides: Partial<AttentionItemDto> = {}): AttentionItemDto {
  return {
    id: 'a1',
    type: 'CriticalOverdue',
    typeLabel: 'Critical overdue',
    title: 'Bank reconciliation is 6 days overdue',
    detail: '',
    score: 80,
    status: 'Open',
    createdAt: '2026-10-06T08:00:00Z',
    updatedAt: '2026-10-06T08:00:00Z',
    task: { id: 't1', number: 'T-1001' } as TaskListItem,
    actions: [],
    ...overrides,
  };
}

const keys = (a: AttentionItemDto) => attentionActions(a).map((x) => x.key);

describe('attentionActions', () => {
  it('infers remind / escalate for follow-up types, plus lifecycle actions', () => {
    expect(keys(item())).toEqual(['remind', 'escalate', 'open', 'resolve', 'snooze', 'dismiss']);
  });

  it('infers approval and extension decisions from the type', () => {
    expect(keys(item({ type: 'ApprovalRequired' }))).toEqual(['approve', 'reopenTask', 'open', 'resolve', 'snooze', 'dismiss']);
    expect(keys(item({ type: 'ExtensionRequest', extensionRequestId: 'x1' }))).toEqual([
      'approveExtension', 'rejectExtension', 'open', 'resolve', 'snooze', 'dismiss',
    ]);
  });

  it('drops actions whose target is missing', () => {
    expect(keys(item({ task: null }))).toEqual(['resolve', 'snooze', 'dismiss']);
    expect(keys(item({ type: 'AiSuggestion', suggestionId: null }))).toEqual(['open', 'resolve', 'snooze', 'dismiss']);
  });

  it('uses server-suggested actions, matched case-insensitively with close variants', () => {
    const a = item({ type: 'ExtensionRequest', extensionRequestId: 'x1', actions: ['Approve', 'reject_extension'] });
    expect(keys(a)).toEqual(['approveExtension', 'rejectExtension', 'open', 'resolve', 'snooze', 'dismiss']);
    expect(keys(item({ actions: ['Send-Reminder'] }))).toEqual(['remind', 'open', 'resolve', 'snooze', 'dismiss']);
  });

  it('falls back to inference when every server key is unknown', () => {
    expect(keys(item({ actions: ['frobnicate'] }))).toEqual(['remind', 'escalate', 'open', 'resolve', 'snooze', 'dismiss']);
  });

  it('offers reopen instead of snooze for snoozed and closed items', () => {
    expect(keys(item({ status: 'Snoozed' }))).toEqual(['remind', 'escalate', 'open', 'resolve', 'dismiss', 'reopen']);
    expect(keys(item({ status: 'Resolved' }))).toEqual(['open', 'reopen']);
  });
});

describe('normalizeActionKey', () => {
  it('reads "reopen" by context', () => {
    expect(normalizeActionKey('reopen', { type: 'ApprovalRequired', status: 'Open' })).toBe('reopenTask');
    expect(normalizeActionKey('reopen', { type: 'Blocked', status: 'Dismissed' })).toBe('reopen');
    expect(normalizeActionKey('reopen', { type: 'Blocked', status: 'Open' })).toBeNull();
  });

  it('maps generic accept / reject to the item kind', () => {
    expect(normalizeActionKey('accept', { type: 'AiSuggestion', status: 'Open' })).toBe('acceptSuggestion');
    expect(normalizeActionKey('reject', { type: 'AiSuggestion', status: 'Open' })).toBe('dismissSuggestion');
    expect(normalizeActionKey('APPROVE', { type: 'ApprovalRequired', status: 'Open' })).toBe('approve');
  });
});

describe('helpers', () => {
  it('maps types to icons with a fallback', () => {
    expect(attentionTypeIcon('ExtensionRequest')).toBe('calendar-clock');
    expect(attentionTypeIcon('SomethingNew')).toBe('alert-circle');
  });

  it('splits inline and overflow actions', () => {
    const { primary, overflow } = splitActions(attentionActions(item()), 3);
    expect(primary.map((a) => a.key)).toEqual(['remind', 'escalate', 'open']);
    expect(overflow.map((a) => a.key)).toEqual(['resolve', 'snooze', 'dismiss']);
  });
});
