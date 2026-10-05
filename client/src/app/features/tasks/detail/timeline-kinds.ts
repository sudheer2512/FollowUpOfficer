/**
 * Timeline item kinds are free-form strings from the server ("email", "teams", "status", "ai.suggestion"…).
 * They are matched case-insensitively by substring (or by whole word where a substring would be ambiguous —
 * "ai" occurs inside "email", "date" inside "update") to pick an icon and a filter group.
 */
export type TimelineGroup = 'communications' | 'updates' | 'system';

interface KindRule {
  icon: string;
  group: TimelineGroup;
  /** Substrings of the lower-cased kind. */
  contains?: string[];
  /** Whole words of the kind (camelCase and punctuation split). */
  words?: string[];
}

/** First match wins, so more specific rules come first (Teams channel before Teams, email before "ai"). */
const RULES: KindRule[] = [
  { icon: 'hash', group: 'communications', contains: ['channel'] },
  { icon: 'mail', group: 'communications', contains: ['email'], words: ['mail'] },
  { icon: 'message', group: 'communications', contains: ['teams', 'chat'] },
  { icon: 'messages', group: 'communications', contains: ['comment', 'message'] },
  { icon: 'bell', group: 'system', contains: ['remind'] },
  { icon: 'zap', group: 'system', contains: ['escalat'] },
  { icon: 'flag', group: 'updates', contains: ['commitment'] },
  { icon: 'check-square', group: 'updates', contains: ['approv'] },
  { icon: 'calendar-clock', group: 'updates', contains: ['extension'] },
  { icon: 'check-circle', group: 'updates', contains: ['complet'] },
  { icon: 'trending-up', group: 'updates', contains: ['progress'] },
  { icon: 'activity', group: 'updates', contains: ['status'] },
  { icon: 'sparkles', group: 'system', contains: ['suggestion'], words: ['ai'] },
  { icon: 'user-check', group: 'updates', contains: ['acknowledg'] },
  { icon: 'user-plus', group: 'updates', contains: ['assign', 'delegat'] },
  { icon: 'plus', group: 'system', contains: ['created'], words: ['create'] },
  { icon: 'link', group: 'updates', contains: ['dependenc'] },
  { icon: 'paperclip', group: 'updates', contains: ['attachment', 'evidence'] },
  { icon: 'calendar', group: 'updates', contains: ['deadline', 'duedate'], words: ['due', 'date'] },
];

function words(kind: string): string[] {
  return kind
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter(Boolean);
}

function match(kind: string | null | undefined): KindRule | null {
  if (!kind) return null;
  const lower = kind.toLowerCase();
  const tokens = words(kind);
  return RULES.find((r) => (r.contains ?? []).some((s) => lower.includes(s)) || (r.words ?? []).some((w) => tokens.includes(w))) ?? null;
}

export function timelineIcon(kind: string | null | undefined): string {
  return match(kind)?.icon ?? 'activity';
}

/** Communications (emails, Teams, comments), updates (status, dates, approvals…) or system (reminders, AI, audit…). */
export function timelineGroup(kind: string | null | undefined): TimelineGroup {
  return match(kind)?.group ?? 'system';
}

/** Emails and Teams messages carry long bodies worth expanding in place. */
export function isMessageKind(kind: string | null | undefined): boolean {
  const icon = timelineIcon(kind);
  return icon === 'mail' || icon === 'message' || icon === 'hash';
}
