import { addDays, diffDays, formatDateOnly, parseDateOnly, startOfWeek, toDateOnly, toTimeInput, toTimeOnly } from './dates';
import { hasFlag, parseFlags, toFlags, toggleFlag } from './flags';
import { confidencePercent, humanize, initials } from './humanize';
import { criticalityTone, healthTone, loadTone, statusTone, toneOf } from './tone';

describe('flags', () => {
  it('parses comma lists and ignores None / numeric leftovers', () => {
    expect(parseFlags('TeamLeader, Ceo')).toEqual(['TeamLeader', 'Ceo']);
    expect(parseFlags('None')).toEqual([]);
    expect(parseFlags('')).toEqual([]);
    expect(parseFlags(null)).toEqual([]);
  });

  it('serialises and toggles', () => {
    expect(toFlags(['Email', 'Teams'])).toBe('Email, Teams');
    expect(toFlags([])).toBe('None');
    expect(toggleFlag('Email', 'InApp', true)).toBe('Email, InApp');
    expect(toggleFlag('Email, InApp', 'Email', false)).toBe('InApp');
    expect(hasFlag('TeamMember, Executive', 'Executive')).toBe(true);
  });
});

describe('dates', () => {
  it('handles DateOnly strings as local calendar dates', () => {
    const d = parseDateOnly('2026-10-06');
    expect(d?.getFullYear()).toBe(2026);
    expect(d?.getMonth()).toBe(9);
    expect(d?.getDate()).toBe(6);
    expect(toDateOnly(new Date(2026, 0, 5))).toBe('2026-01-05');
    expect(parseDateOnly('not a date')).toBeNull();
  });

  it('adds days across month boundaries and finds the Monday of a week', () => {
    expect(addDays('2026-10-30', 3)).toBe('2026-11-02');
    expect(startOfWeek('2026-10-08')).toBe('2026-10-05');
    expect(startOfWeek('2026-10-11')).toBe('2026-10-05');
    expect(diffDays('2026-10-01', '2026-10-08')).toBe(7);
  });

  it('formats dates and times', () => {
    expect(formatDateOnly('2027-03-31')).toBe('31 Mar 2027');
    expect(toTimeOnly('9:05')).toBe('09:05:00');
    expect(toTimeOnly('18:30:15')).toBe('18:30:15');
    expect(toTimeInput('08:00:00')).toBe('08:00');
    expect(toTimeOnly('')).toBeNull();
  });
});

describe('humanize & tone', () => {
  it('humanizes enum values', () => {
    expect(humanize('InProgress')).toBe('In progress');
    expect(humanize('BusinessCritical')).toBe('Business critical');
    expect(humanize('CeoOnly')).toBe('CEO only');
    expect(humanize('TeamLeader, Ceo')).toBe('Team leader, CEO');
    expect(initials('Rahul Mehta')).toBe('RM');
    expect(confidencePercent(0.823)).toBe('82%');
  });

  it('maps meaning to tones', () => {
    expect(healthTone('AtRisk')).toBe('orange');
    expect(criticalityTone('BusinessCritical')).toBe('danger');
    expect(criticalityTone('Important')).toBe('info');
    expect(statusTone('Overdue')).toBe('danger');
    expect(statusTone('Completed')).toBe('good');
    expect(toneOf('warn')).toBe('warning');
    expect(toneOf('something-else')).toBe('neutral');
    expect(loadTone('Overloaded')).toBe('danger');
    expect(loadTone('Light')).toBe('good');
  });
});
