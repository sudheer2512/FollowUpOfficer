/**
 * Conversation state for the Ask page and its sessionStorage persistence (the history survives navigation within
 * the browser session, and is dropped when the tab closes).
 */
import { DateTimeUtc } from '../../core/models/common.models';
import { AskAnswer } from '../../core/models/view.models';

/** The spec's suggested questions, verbatim. */
export const SUGGESTED_QUESTIONS: readonly string[] = [
  'What critical work requires my attention?',
  'What promises were missed this week?',
  'What has Development not responded to?',
  'Show Finance tasks overdue by more than five days.',
  'Which recurring activities were missed this month?',
  'What is blocking the eInvoice project?',
];

export type AskTurnState = 'pending' | 'done' | 'error';

export interface AskTurn {
  id: number;
  question: string;
  askedAt: DateTimeUtc;
  state: AskTurnState;
  answer?: AskAnswer | null;
  error?: string | null;
}

export const ASK_HISTORY_KEY = 'fu.ask.history';
/** Older turns are dropped to keep sessionStorage small (answers carry full task lists). */
const MAX_TURNS = 25;
const INTERRUPTED = 'This question was interrupted before an answer arrived. Ask it again.';

function isTurn(value: unknown): value is AskTurn {
  if (!value || typeof value !== 'object') return false;
  const t = value as Partial<AskTurn>;
  return typeof t.id === 'number' && typeof t.question === 'string' && typeof t.askedAt === 'string' && typeof t.state === 'string';
}

/** Restores the session's conversation. A turn still pending when the page was left becomes a retryable error. */
export function loadAskHistory(storage: Pick<Storage, 'getItem'> | null = safeSession()): AskTurn[] {
  try {
    const raw = storage?.getItem(ASK_HISTORY_KEY);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(isTurn).map((t) => {
      if (t.state === 'done' && t.answer) return t;
      if (t.state === 'error') return t;
      return { ...t, state: 'error' as const, answer: null, error: INTERRUPTED };
    });
  } catch {
    return [];
  }
}

/** Saves the newest turns; when the quota is exceeded, keeps only the last few, else clears the key. */
export function saveAskHistory(turns: readonly AskTurn[], storage: Pick<Storage, 'setItem' | 'removeItem'> | null = safeSession()): void {
  if (!storage) return;
  try {
    if (!turns.length) {
      storage.removeItem(ASK_HISTORY_KEY);
      return;
    }
    storage.setItem(ASK_HISTORY_KEY, JSON.stringify(turns.slice(-MAX_TURNS)));
  } catch {
    try {
      storage.setItem(ASK_HISTORY_KEY, JSON.stringify(turns.slice(-3)));
    } catch {
      try {
        storage.removeItem(ASK_HISTORY_KEY);
      } catch {
        /* storage unavailable */
      }
    }
  }
}

function safeSession(): Storage | null {
  try {
    return typeof sessionStorage === 'undefined' ? null : sessionStorage;
  } catch {
    return null;
  }
}
