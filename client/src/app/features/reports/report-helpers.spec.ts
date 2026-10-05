import { formatMetric, onTimeTone, oneDecimal, periodLabel } from './report-format';
import { DEFAULT_PRESET, presetPeriod, resolvePeriod } from './report-period';

// Tuesday 6 October 2026 (local time).
const TODAY = new Date(2026, 9, 6);

describe('report periods', () => {
  it('computes the fixed presets', () => {
    expect(presetPeriod('this-month', TODAY)).toEqual({ from: '2026-10-01', to: '2026-10-06' });
    expect(presetPeriod('last-month', TODAY)).toEqual({ from: '2026-09-01', to: '2026-09-30' });
    expect(presetPeriod('last-30', TODAY)).toEqual({ from: '2026-09-07', to: '2026-10-06' });
    expect(presetPeriod('last-90', TODAY)).toEqual({ from: '2026-07-09', to: '2026-10-06' });
    expect(presetPeriod('this-quarter', TODAY)).toEqual({ from: '2026-10-01', to: '2026-10-06' });
    expect(presetPeriod('fytd', TODAY)).toEqual({ from: '2026-04-01', to: '2026-10-06' });
  });

  it('starts the financial year in the previous calendar year before April', () => {
    expect(presetPeriod('fytd', new Date(2027, 1, 10))).toEqual({ from: '2026-04-01', to: '2027-02-10' });
    expect(presetPeriod('last-month', new Date(2027, 0, 15))).toEqual({ from: '2026-12-01', to: '2026-12-31' });
  });

  it('resolves query parameters', () => {
    expect(resolvePeriod({}, TODAY)).toEqual({ preset: DEFAULT_PRESET, ...presetPeriod(DEFAULT_PRESET, TODAY) });
    expect(resolvePeriod({ preset: 'fytd', from: '2020-01-01' }, TODAY)).toEqual({ preset: 'fytd', from: '2026-04-01', to: '2026-10-06' });
    expect(resolvePeriod({ preset: 'custom', from: '2026-08-01', to: '2026-08-31' }, TODAY)).toEqual({ preset: 'custom', from: '2026-08-01', to: '2026-08-31' });
    expect(resolvePeriod({ from: '2026-08-31', to: '2026-08-01' }, TODAY)).toEqual({ preset: 'custom', from: '2026-08-01', to: '2026-08-31' });
    expect(resolvePeriod({ preset: 'custom', from: '2026-09-15' }, TODAY)).toEqual({ preset: 'custom', from: '2026-09-15', to: '2026-10-06' });
    expect(resolvePeriod({ preset: 'bogus' }, TODAY).preset).toBe(DEFAULT_PRESET);
  });
});

describe('report formatting', () => {
  it('formats metrics by unit', () => {
    expect(formatMetric({ value: 82.4, unit: '%' })).toBe('82%');
    expect(formatMetric({ value: 3.44, unit: 'days' })).toBe('3.4 days');
    expect(formatMetric({ value: 1, unit: 'days' })).toBe('1 day');
    expect(formatMetric({ value: 5.25, unit: 'hours' })).toBe('5.3 h');
    expect(formatMetric({ value: 12.6, unit: '' })).toBe('13');
    expect(oneDecimal(4)).toBe('4');
  });

  it('labels periods and on-time tones', () => {
    expect(periodLabel('2026-09-07', '2026-10-06')).toBe('7 Sep – 6 Oct 2026');
    expect(periodLabel('2025-12-01', '2026-01-31')).toBe('1 Dec 2025 – 31 Jan 2026');
    expect(periodLabel(null, '2026-01-31')).toBe('');
    expect(onTimeTone(85)).toBe('good');
    expect(onTimeTone(65)).toBe('warning');
    expect(onTimeTone(10)).toBe('danger');
  });
});
