/**
 * Enum → human label. "InProgress" → "In progress", "BusinessCritical" → "Business critical".
 * A few labels are overridden where the generic rule reads badly (CEO, AI, Teams…).
 */
const OVERRIDES: Record<string, string> = {
  CeoOnly: 'CEO only',
  Ceo: 'CEO',
  AtRisk: 'At risk',
  TeamsChat: 'Teams chat',
  TeamsChannel: 'Teams channel',
  TeamsActivity: 'Teams activity',
  InApp: 'In-app',
  Ai: 'AI',
  AiSuggestion: 'AI suggestion',
  EscalatedToCeo: 'Escalated to CEO',
  EveryNDays: 'Every N days',
  NthWeekday: 'Nth weekday',
  DayOfMonth: 'Day of month',
  WorkingDays: 'Every working day',
  HalfYearly: 'Half-yearly',
  Yearly: 'Annual',
  FinancialYear: 'Financial year',
  AnchorDates: 'Anchor dates',
  DueThisWeek: 'Due this week',
  DueTomorrow: 'Due tomorrow',
  DueToday: 'Due today',
  SeverelyOverdue: 'Severely overdue',
  GenerateAll: 'Generate all',
  GenerateLatestOnly: 'Latest only',
  SkipMissed: 'Skip missed',
  GenerateWithinWindow: 'Within N days',
  SkippedHoliday: 'Skipped – holiday',
  SkippedCatchUp: 'Skipped – catch-up',
  SkippedByUser: 'Skipped by user',
  SkippedPaused: 'Paused',
  CreatedLate: 'Created late',
  OutOfRange: 'Out of range',
  ThisInstance: 'This instance',
  ThisAndFuture: 'This and future',
  EntireSeries: 'Entire series',
  FinishToStart: 'Finish to start',
  StartToStart: 'Start to start',
  GitCommit: 'Git commit',
  TestResult: 'Test result',
  CustomerConfirmation: 'Customer confirmation',
  GovernmentDepartment: 'Government department',
  ExternalInformation: 'External information',
  RequestMoreWork: 'Request more work',
  MoreWorkRequested: 'More work requested',
  NeedClarification: 'Need clarification',
  CannotAccept: 'Cannot accept',
  ProposeDifferentDeadline: 'Propose different deadline',
  MarkCompletedByAssignee: 'Mark completed',
  CompletedByAssignee: 'Completed by assignee',
  CompletionAwaitingApproval: 'Awaiting approval',
  AcknowledgementPending: 'Awaiting acknowledgement',
  ReplyToNotification: 'Reply to reminder',
  ConversationThread: 'Conversation thread',
  RecentContext: 'Recent context',
  TaskNumber: 'Task number',
  DeadLetter: 'Dead letter',
  AuthError: 'Auth error',
  OutlookMail: 'Outlook mail',
  RunIfWithinWindow: 'Run if within window',
  CatchUp: 'Catch-up',
  TimedOut: 'Timed out',
};

export function humanize(value: string | null | undefined): string {
  if (value === null || value === undefined) return '';
  const v = String(value);
  if (OVERRIDES[v]) return OVERRIDES[v];
  if (v.includes(',')) return v.split(',').map((s) => humanize(s.trim())).join(', ');
  const spaced = v.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z])([A-Z][a-z])/g, '$1 $2');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

/** Initials for an avatar: "Rahul Mehta" → "RM". */
export function initials(name: string | null | undefined): string {
  if (!name) return '?';
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

export function pluralize(count: number, singular: string, plural?: string): string {
  return `${count} ${count === 1 ? singular : plural ?? singular + 's'}`;
}

export function percent(value: number | null | undefined, digits = 0): string {
  if (value === null || value === undefined || isNaN(value)) return '–';
  return `${value.toFixed(digits)}%`;
}

/** 0–1 confidence → "82%". */
export function confidencePercent(value: number | null | undefined): string {
  if (value === null || value === undefined || isNaN(value)) return '–';
  const v = value <= 1 ? value * 100 : value;
  return `${Math.round(v)}%`;
}

export function fileSize(bytes: number | null | undefined): string {
  if (!bytes && bytes !== 0) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** Indian-style currency with no decimals (₹12,50,000). */
export function money(value: number | null | undefined): string {
  if (value === null || value === undefined) return '';
  try {
    return new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 }).format(value);
  } catch {
    return String(value);
  }
}
