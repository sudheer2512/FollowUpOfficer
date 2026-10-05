/**
 * ECharts option builders for the reports page. The <fu-chart> wrapper adds theme text colours, axis styling,
 * the default grid, legend and tooltip; these builders only add data and semantic colours.
 */
import type { EChartsCoreOption } from 'echarts/core';
import { AgeingBucket, EscalationBreakdown, GroupPerformance, TrendPoint } from '../../core/models/view.models';
import { formatDateOnly } from '../../core/util/dates';
import { chartPalette } from '../../shared/ui/chart.component';
import { OnTimeTone, onTimeTone } from './report-format';

export interface ChartColors {
  accent: string;
  good: string;
  danger: string;
  warning: string;
  orange: string;
  /** Axis-label / legend text colour, for options that replace the wrapper's axisLabel or legend. */
  muted: string;
}

/** Semantic colours from the current theme (chartPalette() series order: accent, good, danger, warning, info, orange, neutral). */
export function chartColors(): ChartColors {
  const p = chartPalette();
  const s = p.series;
  return { accent: s[0], good: s[1], danger: s[2], warning: s[3], orange: s[5], muted: p.muted };
}

const BAR_RADIUS = [4, 4, 0, 0];

export function hasCounts(items: ReadonlyArray<{ count: number }> | null | undefined): boolean {
  return (items ?? []).some((i) => (i.count ?? 0) > 0);
}

export function hasTrend(points: readonly TrendPoint[] | null | undefined): boolean {
  return (points ?? []).some((p) => (p.created ?? 0) + (p.completed ?? 0) + (p.overdue ?? 0) > 0);
}

/** Created / completed / overdue per day: smooth lines, a subtle area under Created. */
export function trendOptions(points: readonly TrendPoint[], c: ChartColors): EChartsCoreOption {
  const line = (name: string, color: string, data: number[], area = false) => ({
    name,
    type: 'line',
    smooth: true,
    showSymbol: false,
    color,
    lineStyle: { width: 2 },
    ...(area ? { areaStyle: { opacity: 0.08 } } : {}),
    data,
  });
  return {
    tooltip: { trigger: 'axis', confine: true },
    xAxis: { type: 'category', boundaryGap: false, data: points.map((p) => formatDateOnly(p.date, { short: true })) },
    yAxis: { type: 'value', minInterval: 1 },
    series: [
      line('Created', c.accent, points.map((p) => p.created ?? 0), true),
      line('Completed', c.good, points.map((p) => p.completed ?? 0)),
      line('Overdue', c.danger, points.map((p) => p.overdue ?? 0)),
    ],
  };
}

/** Overdue tasks by age; older buckets are drawn more intensely. Bars are clickable (drill-down). */
export function ageingOptions(buckets: readonly AgeingBucket[], c: ChartColors): EChartsCoreOption {
  const n = Math.max(1, buckets.length - 1);
  return {
    legend: { show: false },
    tooltip: { trigger: 'axis', confine: true, axisPointer: { type: 'shadow' } },
    xAxis: { type: 'category', data: buckets.map((b) => b.label) },
    yAxis: { type: 'value', minInterval: 1 },
    series: [
      {
        name: 'Overdue tasks',
        type: 'bar',
        cursor: 'pointer',
        barMaxWidth: 48,
        label: { show: true, position: 'top' },
        data: buckets.map((b, i) => ({ value: b.count ?? 0, itemStyle: { color: c.danger, opacity: 0.45 + (0.55 * i) / n, borderRadius: BAR_RADIUS } })),
      },
    ],
  };
}

export function escalationLevelOptions(levels: readonly EscalationBreakdown[], c: ChartColors): EChartsCoreOption {
  return {
    legend: { show: false },
    tooltip: { trigger: 'axis', confine: true, axisPointer: { type: 'shadow' } },
    xAxis: { type: 'category', data: levels.map((l) => l.label) },
    yAxis: { type: 'value', minInterval: 1 },
    series: [
      {
        name: 'Escalations',
        type: 'bar',
        barMaxWidth: 48,
        color: c.orange,
        itemStyle: { borderRadius: BAR_RADIUS },
        label: { show: true, position: 'top' },
        data: levels.map((l) => l.count ?? 0),
      },
    ],
  };
}

/** Escalations by trigger as a donut with a scrolling legend underneath. */
export function escalationTriggerOptions(triggers: readonly EscalationBreakdown[], c: ChartColors): EChartsCoreOption {
  return {
    tooltip: { trigger: 'item', confine: true, formatter: '{b}: {c} ({d}%)' },
    legend: { type: 'scroll', bottom: 0, top: 'auto', icon: 'circle', itemWidth: 8, itemHeight: 8, textStyle: { color: c.muted } },
    series: [
      {
        name: 'Escalations',
        type: 'pie',
        radius: ['50%', '75%'],
        center: ['50%', '45%'],
        avoidLabelOverlap: true,
        itemStyle: { borderWidth: 2, borderColor: 'transparent' },
        label: { show: false },
        data: triggers.filter((t) => (t.count ?? 0) > 0).map((t) => ({ name: t.label, value: t.count })),
      },
    ],
  };
}

/** On-time completion % per department (0–100), coloured good / watch / poor, with value labels. */
export function onTimeByDepartmentOptions(departments: readonly GroupPerformance[], c: ChartColors): EChartsCoreOption {
  const tone: Record<OnTimeTone, string> = { good: c.good, warning: c.warning, danger: c.danger };
  return {
    legend: { show: false },
    tooltip: { trigger: 'axis', confine: true, axisPointer: { type: 'shadow' }, valueFormatter: (v: unknown) => `${v}%` },
    xAxis: { type: 'category', data: departments.map((d) => d.name), axisLabel: { color: c.muted, interval: 0, hideOverlap: true } },
    yAxis: { type: 'value', min: 0, max: 100, axisLabel: { color: c.muted, formatter: '{value}%' } },
    series: [
      {
        name: 'On time',
        type: 'bar',
        barMaxWidth: 44,
        label: { show: true, position: 'top', formatter: '{c}%' },
        data: departments.map((d) => {
          const v = Math.round(d.onTimePercent ?? 0);
          return { value: v, itemStyle: { color: tone[onTimeTone(v)], borderRadius: BAR_RADIUS } };
        }),
      },
    ],
  };
}
