// The parts of ECharts the dashboards use, registered once: three kinds of charts, the components they show, and
// the canvas. The one file that imports ECharts' values, so the rest of its library stays out of the bundle (and
// with it, GeoJSON's loader, which would read text as code).
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import {
  AriaComponent,
  GridComponent,
  LegendComponent,
  TooltipComponent,
} from 'echarts/components';
import { init, use } from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';

use([
  BarChart,
  LineChart,
  PieChart,
  AriaComponent,
  GridComponent,
  LegendComponent,
  TooltipComponent,
  CanvasRenderer,
]);

export { init };
