import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  afterNextRender,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import type { ECharts, EChartsCoreOption } from 'echarts/core';
import { ThemeService } from '../../core/state/theme.service';

type EChartsCore = typeof import('echarts/core');

let echartsPromise: Promise<EChartsCore> | null = null;
/**
 * ECharts is imported lazily (tree-shaken core + the chart types we use), so only routes that render a chart
 * download it.
 */
function loadECharts(): Promise<EChartsCore> {
  echartsPromise ??= import('./echarts-setup').then((m) => m.echarts);
  return echartsPromise;
}

/** Reads the design tokens so charts match the current theme. */
export function chartPalette(): { text: string; muted: string; border: string; series: string[] } {
  const css = getComputedStyle(document.documentElement);
  const v = (name: string, fallback: string) => css.getPropertyValue(name).trim() || fallback;
  return {
    text: v('--text-2', '#3d4352'),
    muted: v('--muted', '#687083'),
    border: v('--border', '#e3e6ec'),
    series: [v('--accent', '#3b47c4'), v('--good', '#18864b'), v('--danger', '#c8322f'), v('--warning', '#b7791f'), v('--info', '#2563c9'), v('--orange', '#d35a12'), v('--neutral', '#5f6779')],
  };
}

/**
 * Tiny ECharts wrapper (no ngx-echarts).
 *   <fu-chart [options]="trendOptions()" height="280px" (chartClick)="drill($event)" />
 * Options are merged over theme defaults (text colours, grid, palette). Resizes with its container.
 */
@Component({
  selector: 'fu-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="host" [style.height]="height()"></div>
    @if (!ready()) {
      <div class="skeleton" [style.height]="height()" style="position:absolute;inset:0"></div>
    }`,
  styles: `:host { display: block; position: relative; } .host { width: 100%; }`,
})
export class ChartComponent implements OnDestroy {
  private readonly el = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly theme = inject(ThemeService);
  private chart: ECharts | null = null;
  private observer: ResizeObserver | null = null;

  readonly options = input.required<EChartsCoreOption>();
  readonly height = input('280px');
  /** Emits the ECharts click params (name, seriesName, dataIndex, data…). */
  readonly chartClick = output<{ name: string; seriesName?: string; dataIndex: number; data: unknown }>();

  protected readonly ready = signal(false);

  constructor() {
    afterNextRender(() => {
      void loadECharts().then((echarts) => {
        const container = this.el.nativeElement.querySelector<HTMLElement>('.host');
        if (!container) return;
        this.chart = echarts.init(container, undefined, { renderer: 'canvas' });
        this.chart.on('click', (p: unknown) => {
          const params = p as { name: string; seriesName?: string; dataIndex: number; data: unknown };
          this.chartClick.emit({ name: params.name, seriesName: params.seriesName, dataIndex: params.dataIndex, data: params.data });
        });
        this.observer = new ResizeObserver(() => this.chart?.resize());
        this.observer.observe(container);
        this.ready.set(true);
        this.render();
      });
    });
    effect(() => {
      this.options();
      this.theme.effective();
      if (this.ready()) this.render();
    });
  }

  private render(): void {
    if (!this.chart) return;
    const p = chartPalette();
    const base: EChartsCoreOption = {
      color: p.series,
      textStyle: { fontFamily: getComputedStyle(document.body).fontFamily, color: p.text },
      grid: { left: 8, right: 16, top: 36, bottom: 8, containLabel: true },
      tooltip: { trigger: 'axis', confine: true },
      legend: { top: 0, textStyle: { color: p.muted }, icon: 'roundRect', itemWidth: 10, itemHeight: 10 },
      animationDuration: 300,
    };
    const opts = this.options() as Record<string, unknown>;
    type Obj = Record<string, unknown>;
    const obj = (v: unknown): Obj => (v && typeof v === 'object' && !Array.isArray(v) ? (v as Obj) : {});
    // Theme styling is merged one level deep so a chart can override e.g. axisLabel.formatter without losing colours.
    const styleAxis = (a: unknown): Obj => {
      const ax = obj(a);
      return {
        ...ax,
        axisLine: { ...obj(ax['axisLine']), lineStyle: { color: p.border, ...obj(obj(ax['axisLine'])['lineStyle']) } },
        axisLabel: { color: p.muted, ...obj(ax['axisLabel']) },
        splitLine: { ...obj(ax['splitLine']), lineStyle: { color: p.border, ...obj(obj(ax['splitLine'])['lineStyle']) } },
      };
    };
    const withAxis = (axis: unknown) => (Array.isArray(axis) ? axis.map(styleAxis) : axis ? styleAxis(axis) : axis);
    const baseLegend = obj(base['legend' as keyof EChartsCoreOption]);
    const legend =
      opts['legend'] && typeof opts['legend'] === 'object' && !Array.isArray(opts['legend'])
        ? { ...baseLegend, ...obj(opts['legend']), textStyle: { ...obj(baseLegend['textStyle']), ...obj(obj(opts['legend'])['textStyle']) } }
        : (opts['legend'] ?? baseLegend);
    this.chart.setOption(
      { ...base, ...opts, legend, xAxis: withAxis(opts['xAxis']), yAxis: withAxis(opts['yAxis']) } as EChartsCoreOption,
      { notMerge: true },
    );
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
    this.chart?.dispose();
    this.chart = null;
  }
}
