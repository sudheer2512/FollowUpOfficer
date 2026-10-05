/**
 * Report period presets and their resolution from query parameters. Dates are local calendar dates
 * ("yyyy-MM-dd"); the financial year starts on 1 April.
 */
import { DateOnly } from '../../core/models/common.models';
import { parseDateOnly, toDateOnly } from '../../core/util/dates';

export type FixedPreset = 'this-month' | 'last-month' | 'last-30' | 'last-90' | 'this-quarter' | 'fytd';
export type PeriodPreset = FixedPreset | 'custom';

export const PERIOD_PRESETS: ReadonlyArray<{ key: PeriodPreset; label: string }> = [
  { key: 'this-month', label: 'This month' },
  { key: 'last-month', label: 'Last month' },
  { key: 'last-30', label: 'Last 30 days' },
  { key: 'last-90', label: 'Last 90 days' },
  { key: 'this-quarter', label: 'This quarter' },
  { key: 'fytd', label: 'Financial year to date' },
  { key: 'custom', label: 'Custom' },
];

export const DEFAULT_PRESET: FixedPreset = 'last-30';

/** Month index (0-based) the financial year starts in: April. */
const FY_START_MONTH = 3;

export interface Period {
  from: DateOnly;
  to: DateOnly;
}

export interface ResolvedPeriod extends Period {
  preset: PeriodPreset;
}

export function isPreset(value: string | null | undefined): value is PeriodPreset {
  return PERIOD_PRESETS.some((p) => p.key === value);
}

/** The date range of a fixed preset, relative to `today`. */
export function presetPeriod(preset: FixedPreset, today: Date = new Date()): Period {
  const y = today.getFullYear();
  const m = today.getMonth();
  const d = today.getDate();
  const to = toDateOnly(new Date(y, m, d));
  switch (preset) {
    case 'this-month':
      return { from: toDateOnly(new Date(y, m, 1)), to };
    case 'last-month':
      // Day 0 of this month is the last day of the previous month.
      return { from: toDateOnly(new Date(y, m - 1, 1)), to: toDateOnly(new Date(y, m, 0)) };
    case 'last-30':
      return { from: toDateOnly(new Date(y, m, d - 29)), to };
    case 'last-90':
      return { from: toDateOnly(new Date(y, m, d - 89)), to };
    case 'this-quarter':
      return { from: toDateOnly(new Date(y, m - (m % 3), 1)), to };
    case 'fytd':
      return { from: toDateOnly(new Date(m >= FY_START_MONTH ? y : y - 1, FY_START_MONTH, 1)), to };
  }
}

/**
 * Query parameters → preset and dates. A known fixed preset wins; "custom" (or bare from/to) uses the given
 * dates, filling a missing bound from the default range and swapping reversed bounds; anything else falls back
 * to the default preset.
 */
export function resolvePeriod(
  params: { preset?: string | null; from?: string | null; to?: string | null },
  today: Date = new Date(),
): ResolvedPeriod {
  const preset = params.preset;
  if (isPreset(preset) && preset !== 'custom') return { preset, ...presetPeriod(preset, today) };

  const from = parseDateOnly(params.from) ? params.from! : null;
  const to = parseDateOnly(params.to) ? params.to! : null;
  if (preset === 'custom' || from || to) {
    const fallback = presetPeriod(DEFAULT_PRESET, today);
    const a = from ?? fallback.from;
    const b = to ?? fallback.to;
    return a <= b ? { preset: 'custom', from: a, to: b } : { preset: 'custom', from: b, to: a };
  }
  return { preset: DEFAULT_PRESET, ...presetPeriod(DEFAULT_PRESET, today) };
}
