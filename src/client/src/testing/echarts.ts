import type { Provider } from '@angular/core';
import type { EChartsOption } from 'echarts';
import {
  type Chart,
  type ChartEvent,
  type Charts,
  ECHARTS_IMPORT,
} from '../app/core/charts/echarts-loader';

/** A chart that draws nothing, keeping what it is given: its options, how often it was resized, its handlers. */
export class FakeChart implements Chart {
  readonly options: EChartsOption[] = [];
  resized = 0;
  disposed = false;
  private readonly handlers = new Map<string, ((params: ChartEvent) => void)[]>();

  constructor(readonly element: HTMLElement) {}

  /** The last options given. */
  get option(): EChartsOption {
    return this.options.at(-1) ?? {};
  }

  setOption(option: EChartsOption): void {
    this.options.push(option);
  }

  resize(): void {
    this.resized++;
  }

  on(event: string, handler: (params: ChartEvent) => void): void {
    this.handlers.set(event, [...(this.handlers.get(event) ?? []), handler]);
  }

  dispose(): void {
    this.disposed = true;
  }

  /** As if an item were clicked (or right-clicked), with the keys held. */
  emit(
    event: 'click' | 'contextmenu',
    data: unknown,
    keys: Partial<Pick<MouseEvent, 'ctrlKey' | 'metaKey' | 'altKey' | 'shiftKey'>> = {},
  ): void {
    const mouse = new MouseEvent(event, { ...keys, cancelable: true });
    for (const handler of this.handlers.get(event) ?? []) {
      handler({ data, event: { event: mouse } });
    }
  }
}

/** ECharts that makes fake charts, each kept. */
export class FakeECharts implements Charts {
  readonly charts: FakeChart[] = [];

  init(element: HTMLElement): FakeChart {
    const chart = new FakeChart(element);
    this.charts.push(chart);
    return chart;
  }

  /** The chart made in an element holding (or being) `element`. */
  chartIn(element: Element): FakeChart | undefined {
    return this.charts.find(
      (chart) => element.contains(chart.element) || chart.element === element,
    );
  }
}

/** Charts made by `charts`; with null, ECharts fails to load. */
export function fakeEChartsProviders(charts: FakeECharts | null): Provider[] {
  return [
    {
      provide: ECHARTS_IMPORT,
      useValue: () =>
        charts ? Promise.resolve(charts) : Promise.reject(new Error('No charts in this test')),
    },
  ];
}
