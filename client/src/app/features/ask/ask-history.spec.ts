import { ASK_HISTORY_KEY, AskTurn, SUGGESTED_QUESTIONS, loadAskHistory, saveAskHistory } from './ask-history';

class MemoryStorage {
  readonly data = new Map<string, string>();
  getItem(k: string): string | null {
    return this.data.get(k) ?? null;
  }
  setItem(k: string, v: string): void {
    this.data.set(k, v);
  }
  removeItem(k: string): void {
    this.data.delete(k);
  }
}

function turn(id: number, state: AskTurn['state']): AskTurn {
  return { id, question: `Q${id}`, askedAt: '2026-10-06T08:00:00Z', state, answer: null, error: state === 'error' ? 'failed' : null };
}

describe('ask history', () => {
  it('keeps the six suggested questions from the spec', () => {
    expect(SUGGESTED_QUESTIONS.length).toBe(6);
    expect(SUGGESTED_QUESTIONS[5]).toBe('What is blocking the eInvoice project?');
  });

  it('round-trips turns and turns interrupted (pending) ones into retryable errors', () => {
    const storage = new MemoryStorage();
    saveAskHistory([turn(1, 'error'), turn(2, 'pending')], storage);
    const restored = loadAskHistory(storage);
    expect(restored.map((t) => t.state)).toEqual(['error', 'error']);
    expect(restored[1].error).toContain('interrupted');
  });

  it('ignores malformed storage and clears the key for an empty history', () => {
    const storage = new MemoryStorage();
    storage.setItem(ASK_HISTORY_KEY, '{not json');
    expect(loadAskHistory(storage)).toEqual([]);
    storage.setItem(ASK_HISTORY_KEY, JSON.stringify([{ nope: true }, turn(3, 'error')]));
    expect(loadAskHistory(storage).map((t) => t.id)).toEqual([3]);
    saveAskHistory([], storage);
    expect(storage.getItem(ASK_HISTORY_KEY)).toBeNull();
  });

  it('stores at most the 25 newest turns', () => {
    const storage = new MemoryStorage();
    saveAskHistory(Array.from({ length: 30 }, (_, i) => turn(i + 1, 'error')), storage);
    const restored = loadAskHistory(storage);
    expect(restored.length).toBe(25);
    expect(restored[0].id).toBe(6);
  });
});
