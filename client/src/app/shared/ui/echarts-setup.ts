/**
 * Tree-shaken ECharts build: only the chart types and components the app renders.
 * Imported dynamically by ChartComponent so it stays out of the initial bundle (named imports let the
 * bundler drop every unused chart type).
 */
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import {
  DatasetComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  TitleComponent,
  TooltipComponent,
} from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';

echarts.use([
  LineChart,
  BarChart,
  PieChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  TitleComponent,
  DatasetComponent,
  MarkLineComponent,
  CanvasRenderer,
]);

export { echarts };
