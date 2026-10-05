/**
 * Pure helpers for the search results page: query-term highlighting (rendered as text + <mark> segments, never as
 * HTML) and per-kind presentation (icon, labels, display order).
 */

export interface TextSegment {
  text: string;
  match: boolean;
}

export interface KindMeta {
  icon: string;
  /** Singular label for the filter chips ("Task", "Teams message"). */
  label: string;
  /** Section heading ("Tasks", "Teams messages"). */
  plural: string;
}

const KIND_META: Record<string, KindMeta> = {
  Task: { icon: 'tasks', label: 'Task', plural: 'Tasks' },
  Person: { icon: 'user', label: 'Person', plural: 'People' },
  Team: { icon: 'users', label: 'Team', plural: 'Teams' },
  Department: { icon: 'briefcase', label: 'Department', plural: 'Departments' },
  Project: { icon: 'folder', label: 'Project', plural: 'Projects' },
  Commitment: { icon: 'flag', label: 'Commitment', plural: 'Commitments' },
  Comment: { icon: 'messages', label: 'Comment', plural: 'Comments' },
  Email: { icon: 'mail', label: 'Email', plural: 'Emails' },
  Teams: { icon: 'message', label: 'Teams message', plural: 'Teams messages' },
  TeamsMessage: { icon: 'message', label: 'Teams message', plural: 'Teams messages' },
  Tag: { icon: 'hash', label: 'Tag', plural: 'Tags' },
  Attachment: { icon: 'paperclip', label: 'Attachment', plural: 'Attachments' },
};

/** Display order of result groups; unknown kinds follow alphabetically. */
const KIND_ORDER = Object.keys(KIND_META);

/** Icon and labels for a hit kind; unknown kinds get the search icon and their own name. */
export function kindMeta(kind: string): KindMeta {
  const known = KIND_META[kind];
  if (known) return known;
  const label = kind.replace(/([a-z0-9])([A-Z])/g, '$1 $2') || 'Other';
  return { icon: 'search', label, plural: label };
}

/** Comparator that puts kinds in the canonical order (tasks first, attachments last). */
export function compareKinds(a: string, b: string): number {
  const ia = KIND_ORDER.indexOf(a);
  const ib = KIND_ORDER.indexOf(b);
  if (ia >= 0 && ib >= 0) return ia - ib;
  if (ia >= 0) return -1;
  if (ib >= 0) return 1;
  return a.localeCompare(b);
}

/** Distinct query terms of two or more characters, longest first so a longer term wins an overlapping match. */
export function searchTerms(query: string | null | undefined): string[] {
  const seen = new Set<string>();
  const terms: string[] = [];
  for (const raw of (query ?? '').split(/\s+/)) {
    const term = raw.replace(/^["']+|["']+$/g, '');
    const key = term.toLowerCase();
    if (term.length < 2 || seen.has(key)) continue;
    seen.add(key);
    terms.push(term);
  }
  return terms.sort((a, b) => b.length - a.length);
}

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Splits `text` into plain and matching segments (case-insensitive). With a single capture group,
 * `String.split` alternates non-match / match parts, so odd indexes are the matches.
 */
export function highlightSegments(text: string | null | undefined, terms: readonly string[]): TextSegment[] {
  const value = text ?? '';
  if (!value) return [];
  if (!terms.length) return [{ text: value, match: false }];
  const pattern = new RegExp(`(${terms.map(escapeRegExp).join('|')})`, 'gi');
  return value
    .split(pattern)
    .map((part, i) => ({ text: part, match: i % 2 === 1 }))
    .filter((s) => s.text.length > 0);
}
