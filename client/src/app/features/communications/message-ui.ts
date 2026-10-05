/** Display helpers for email / Teams messages. */
import { CommunicationChannel, LinkStatus, MessageCategory, MessageProcessingStatus } from '../../core/models/enums';
import { LinkedTaskRef, MessageDto, MessageParty } from '../../core/models/view.models';
import { UiTone } from '../../core/util/tone';

export function channelIcon(channel: CommunicationChannel | string | null | undefined): string {
  switch (channel) {
    case 'Email':
      return 'mail';
    case 'TeamsChat':
      return 'message';
    case 'TeamsChannel':
      return 'hash';
    default:
      return 'messages';
  }
}

/** Completion good · Delay/Rejection orange · Blocker/Escalation danger · Commitment/DeadlineChange accent · … */
export function categoryTone(category: MessageCategory | string | null | undefined): UiTone {
  switch (category) {
    case 'Completion':
    case 'Approval':
      return 'good';
    case 'Delay':
    case 'Rejection':
      return 'orange';
    case 'Blocker':
    case 'Escalation':
      return 'danger';
    case 'Commitment':
    case 'DeadlineChange':
      return 'accent';
    case 'ProgressUpdate':
      return 'info';
    case 'Clarification':
    case 'RequestForHelp':
      return 'warning';
    default:
      return 'neutral';
  }
}

export function processingTone(status: MessageProcessingStatus | string | null | undefined): UiTone {
  switch (status) {
    case 'Processed':
      return 'good';
    case 'NeedsReview':
      return 'warning';
    case 'Failed':
      return 'danger';
    case 'Pending':
      return 'info';
    default:
      return 'neutral';
  }
}

export function linkStatusTone(status: LinkStatus | string | null | undefined): UiTone {
  switch (status) {
    case 'Linked':
      return 'good';
    case 'Suggested':
      return 'warning';
    default:
      return 'neutral';
  }
}

/** Subject, or the first non-empty line of the body when there is none (Teams messages). */
export function messageHeadline(m: Pick<MessageDto, 'subject' | 'body'>): string {
  const subject = (m.subject ?? '').trim();
  if (subject) return subject;
  const line = (m.body ?? '').split(/\r?\n/).find((l) => l.trim().length > 0) ?? '';
  return line.trim().slice(0, 160) || '(no text)';
}

/** Body collapsed to one paragraph for previews. */
export function messagePreview(m: Pick<MessageDto, 'body'>, max = 280): string {
  const text = (m.body ?? '').replace(/\s+/g, ' ').trim();
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

export function senderLabel(m: Pick<MessageDto, 'sender' | 'senderName' | 'senderAddress'>): string {
  return m.sender?.name || m.senderName || m.senderAddress || 'Unknown sender';
}

/** "Name <address>", or whichever part is known. */
export function partyLabel(p: MessageParty): string {
  const name = (p.name ?? '').trim();
  const address = (p.address ?? '').trim();
  if (name && address && name !== address) return `${name} <${address}>`;
  return name || address || p.externalUserId || 'Unknown';
}

export function suggestedLinks(m: Pick<MessageDto, 'links'>): LinkedTaskRef[] {
  return (m.links ?? []).filter((l) => l.status === 'Suggested');
}

/** Links worth showing on a row (rejected suggestions are hidden). */
export function visibleLinks(m: Pick<MessageDto, 'links'>): LinkedTaskRef[] {
  return (m.links ?? []).filter((l) => l.status !== 'Rejected');
}

/** 0–1 (or 0–100) confidence → 0–100 for bars. */
export function confidenceValue(value: number | null | undefined): number {
  if (value === null || value === undefined || isNaN(value)) return 0;
  const v = value <= 1 ? value * 100 : value;
  return Math.max(0, Math.min(100, Math.round(v)));
}

export function confidenceColor(pct: number): string {
  return pct >= 80 ? 'var(--good)' : pct >= 55 ? 'var(--warning)' : 'var(--orange)';
}

/** Removes one link from a list of messages and drops messages that no longer have a pending suggestion. */
export function withoutSuggestion(messages: readonly MessageDto[], linkId: string): MessageDto[] {
  return messages
    .map((m) => ({ ...m, links: (m.links ?? []).filter((l) => l.linkId !== linkId) }))
    .filter((m) => suggestedLinks(m).length > 0);
}
