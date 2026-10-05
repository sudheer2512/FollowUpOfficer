import { queuedMessage, resolveWeekStart, weekRangeLabel } from './review-format';

describe('review format helpers', () => {
  it('snaps any date to the Monday of its week and falls back to the current week', () => {
    expect(resolveWeekStart('2026-10-08', '2026-01-01')).toBe('2026-10-05');
    expect(resolveWeekStart('2026-10-05', '2026-01-01')).toBe('2026-10-05');
    expect(resolveWeekStart('2026-10-11', '2026-01-01')).toBe('2026-10-05');
    expect(resolveWeekStart(null, '2026-10-06')).toBe('2026-10-05');
    expect(resolveWeekStart('garbage', '2026-10-06')).toBe('2026-10-05');
  });

  it('formats the week heading', () => {
    expect(weekRangeLabel('2026-10-05', '2026-10-11')).toBe('Week of 5 Oct – 11 Oct 2026');
    expect(weekRangeLabel('2026-10-05')).toBe('Week of 5 Oct – 11 Oct 2026');
    expect(weekRangeLabel('2025-12-29', '2026-01-04')).toBe('Week of 29 Dec 2025 – 4 Jan 2026');
    expect(weekRangeLabel('')).toBe('');
  });

  it('describes queued results', () => {
    expect(queuedMessage({ queued: 3 })).toBe('Queued 3 messages');
    expect(queuedMessage({ queued: 1 })).toBe('Queued 1 message');
    expect(queuedMessage({ queued: true })).toBe('Queued');
    expect(queuedMessage(null)).toBe('Queued');
    expect(queuedMessage({ queued: 0 })).toContain('Nothing');
  });
});
