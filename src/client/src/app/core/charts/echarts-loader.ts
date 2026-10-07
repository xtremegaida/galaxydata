import { Injectable, InjectionToken, inject } from '@angular/core';
import type { EChartsOption } from 'echarts';

/** A chart as the dashboards use one: shown with options, resized, told what is clicked, let go. */
export interface Chart {
  setOption(option: EChartsOption, settings?: { notMerge?: boolean }): void;
  resize(): void;
  on(event: 'click' | 'contextmenu', handler: (params: ChartEvent) => void): void;
  dispose(): void;
}

/** What a click on a chart's item gives: the item's data (its key), and the browser's event (its keys held). */
export interface ChartEvent {
  readonly data?: unknown;
  readonly event?: { readonly event?: MouseEvent };
}

/** ECharts, as the application loads it: making a chart in an element. */
export interface Charts {
  init(element: HTMLElement): Chart;
}

/** Imports ECharts' chunk (a fake in tests). */
export const ECHARTS_IMPORT = new InjectionToken<() => Promise<Charts>>('ECHARTS_IMPORT', {
  providedIn: 'root',
  factory: () => () =>
    import('./echarts-modules').then((m) => ({
      init: (element) => m.init(element) as unknown as Chart,
    })),
});

/** Loads ECharts once, when a chart is first shown. A load that fails may be tried again. */
@Injectable({ providedIn: 'root' })
export class EChartsLoader {
  private readonly import = inject(ECHARTS_IMPORT);
  private loading: Promise<Charts> | null = null;

  load(): Promise<Charts> {
    this.loading ??= this.import().catch((error: unknown) => {
      this.loading = null;
      throw error;
    });
    return this.loading;
  }
}
