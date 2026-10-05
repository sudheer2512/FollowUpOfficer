import { isMessageKind, timelineGroup, timelineIcon } from './timeline-kinds';

describe('timeline kinds', () => {
  it('maps the documented kinds to icons', () => {
    expect(timelineIcon('email')).toBe('mail');
    expect(timelineIcon('teams')).toBe('message');
    expect(timelineIcon('TeamsChat')).toBe('message');
    expect(timelineIcon('TeamsChannel')).toBe('hash');
    expect(timelineIcon('comment')).toBe('messages');
    expect(timelineIcon('status')).toBe('activity');
    expect(timelineIcon('progress')).toBe('trending-up');
    expect(timelineIcon('reminder')).toBe('bell');
    expect(timelineIcon('escalation')).toBe('zap');
    expect(timelineIcon('commitment')).toBe('flag');
    expect(timelineIcon('approval')).toBe('check-square');
    expect(timelineIcon('completion')).toBe('check-circle');
    expect(timelineIcon('extension')).toBe('calendar-clock');
    expect(timelineIcon('suggestion')).toBe('sparkles');
    expect(timelineIcon('created')).toBe('plus');
    expect(timelineIcon('reassign')).toBe('user-plus');
    expect(timelineIcon('delegation')).toBe('user-plus');
    expect(timelineIcon('acknowledgement')).toBe('user-check');
    expect(timelineIcon('dueDate')).toBe('calendar');
    expect(timelineIcon('dependency')).toBe('link');
    expect(timelineIcon('attachment')).toBe('paperclip');
  });

  it('avoids ambiguous substrings', () => {
    expect(timelineIcon('ai.suggestion')).toBe('sparkles');
    expect(timelineIcon('AiApplied')).toBe('sparkles');
    expect(timelineIcon('email.inbound')).toBe('mail');
    expect(timelineIcon('update')).toBe('activity');
    expect(timelineIcon('awaiting')).toBe('activity');
  });

  it('falls back to activity / system', () => {
    expect(timelineIcon(undefined)).toBe('activity');
    expect(timelineIcon('something-new')).toBe('activity');
    expect(timelineGroup('something-new')).toBe('system');
  });

  it('groups kinds for the filter chips', () => {
    expect(timelineGroup('email')).toBe('communications');
    expect(timelineGroup('comment')).toBe('communications');
    expect(timelineGroup('status')).toBe('updates');
    expect(timelineGroup('reminder')).toBe('system');
    expect(isMessageKind('teams')).toBe(true);
    expect(isMessageKind('comment')).toBe(false);
  });
});
