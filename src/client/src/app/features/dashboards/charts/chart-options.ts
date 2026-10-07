import type { EChartsOption } from 'echarts';
import type {
  BarConfig,
  ChartConfig,
  Key,
  LegendPosition,
  LineConfig,
  NumberFormat,
  PieConfig,
  Selection,
  WidgetData,
} from '../model/definition';
import { keyText } from '../model/definition';
import type { ChartTheme } from './chart-theme';
import {
  categoryText,
  chartNumber,
  escapeHtml,
  formatNumber,
  periodsBetween,
} from './chart-values';

/**
 * Colors by the entity they stand for (a series, a slice), in the palette's order as entities are first met, kept
 * while the widget is shown: a filter that leaves some out doesn't paint the others anew. Past the palette's last
 * color, entities are the neutral one.
 */
export class ColorMemory {
  private readonly slots = new Map<string, number>();

  slot(entity: string): number {
    let slot = this.slots.get(entity);
    if (slot === undefined) {
      slot = this.slots.size;
      this.slots.set(entity, slot);
    }
    return slot;
  }

  color(entity: string, theme: ChartTheme): string {
    return theme.series[this.slot(entity)] ?? theme.neutral;
  }
}

/** What a chart is drawn with besides its config and rows. */
export interface ChartContext {
  readonly theme: ChartTheme;
  readonly locale: string;
  /** The slices chosen in this chart (highlighted; excluded ones faded and hatched). */
  readonly selection: Selection | null;
  readonly reducedMotion: boolean;
  readonly colors: ColorMemory;
  /** Whether the chart is narrow (a phone's width): a legend at a side goes below it, leaving the plot room. */
  readonly narrow: boolean;
}

/** A chart's item as the chart keeps it: its value drawn, its key (for choosing it), and its value as it came. */
interface Item {
  value: number | null;
  name?: string;
  key: Key | null;
  raw: unknown;
  itemStyle?: Record<string, unknown>;
}

export function chartOption(
  config: ChartConfig,
  data: WidgetData,
  context: ChartContext,
): EChartsOption {
  switch (config.kind) {
    case 'pie':
      return pieOption(config, data, context);
    case 'bar':
      return barOption(config, data, context);
    default:
      return lineOption(config, data, context);
  }
}

export function pieOption(
  config: PieConfig,
  data: WidgetData,
  context: ChartContext,
): EChartsOption {
  const { theme, locale } = context;
  const legendAt = legendPosition(config.legend, context);
  const d = column(data, 'dimension');
  const m = column(data, 'measure');
  const type = data.columns[d].type;
  const format = data.columns[m].format;
  const items: Item[] = data.rows.map((row) => {
    const key: Key = [row[d]];
    return {
      name: categoryText(row[d], type, config.dimension.bucket, locale),
      value: chartNumber(row[m]),
      key,
      raw: row[m],
      itemStyle: { color: context.colors.color(keyText(key), theme), ...look(key, context) },
    };
  });
  if (data.other !== null && data.other !== undefined) {
    items.push({
      name: 'Other',
      value: chartNumber(data.other),
      key: null,
      raw: data.other,
      itemStyle: { color: theme.neutral },
    });
  }
  // Labels outside the pie need room beside it, more so with the legend at a side.
  const outer = config.labels ? '62%' : '72%';
  return {
    ...common(
      context,
      summary(
        'Pie chart',
        [config.measure.label],
        config.dimension.label,
        items,
        format,
        locale,
        context.selection,
      ),
    ),
    tooltip: {
      ...tooltip(context),
      trigger: 'item',
      formatter: (p: unknown) => {
        const params = p as { name: string; percent: number; data: Item; marker: string };
        return `${params.marker}${escapeHtml(params.name)}<br>${escapeHtml(formatNumber(params.data.raw, format, locale))} (${formatNumber(params.percent, { decimals: 1, prefix: null, suffix: '%', compact: false }, locale)})`;
      },
    },
    legend:
      items.length >= 2 && legendAt !== 'none' ? legend(legendAt, theme, true) : { show: false },
    series: [
      {
        type: 'pie',
        name: config.measure.label,
        radius: config.donut ? ['45%', outer] : [0, outer],
        // Without rows the widget says so; no grey ring.
        showEmptyCircle: false,
        center: pieCenter(items.length >= 2 ? legendAt : 'none'),
        itemStyle: { borderColor: theme.surface, borderWidth: 2 },
        label: {
          show: config.labels,
          color: theme.text,
          formatter: '{b}',
          overflow: 'truncate',
          ellipsis: '…',
        },
        labelLine: { length: 8, length2: 8, lineStyle: { color: theme.grid } },
        data: items as never,
      },
    ],
  };
}

export function barOption(
  config: BarConfig,
  data: WidgetData,
  context: ChartContext,
): EChartsOption {
  const { theme, locale } = context;
  const legendAt = legendPosition(config.legend, context);
  const d = column(data, 'dimension');
  const s = column(data, 'series');
  const type = data.columns[d].type;
  const measures = data.columns.map((c, i) => ({ c, i })).filter(({ c }) => c.role === 'measure');
  const horizontal = config.orientation === 'horizontal';
  const stacked = config.stack !== 'none';
  const radius = horizontal ? [0, 4, 4, 0] : [4, 4, 0, 0];
  let categories: unknown[];
  let series: Record<string, unknown>[];
  let all: Item[] = [];
  if (s < 0) {
    categories = data.rows.map((row) => row[d]);
    series = measures.map(({ c, i }) => {
      const items: Item[] = data.rows.map((row) => ({
        value: chartNumber(row[i]),
        key: [row[d]],
        raw: row[i],
        itemStyle: look([row[d]], context),
      }));
      all = all.concat(items);
      return {
        type: 'bar',
        name: c.label,
        stack: stacked ? 'total' : undefined,
        barMaxWidth: 24,
        itemStyle: {
          color: context.colors.color('measure:' + c.label, theme),
          borderRadius: stacked ? 0 : radius,
          borderColor: stacked ? theme.surface : undefined,
          borderWidth: stacked ? 1 : 0,
        },
        label: label(config.labels, horizontal, c.format, locale, theme),
        data: items,
        format: c.format,
      };
    });
  } else {
    categories = data.categories ? [...data.categories] : unique(data.rows.map((row) => row[d]));
    const m = measures[0];
    const values = unique(data.rows.map((row) => row[s]));
    const totals = categories.map((category) =>
      data.rows
        .filter((row) => same(row[d], category))
        .reduce((sum, row) => sum + (chartNumber(row[m.i]) ?? 0), 0),
    );
    series = values.map((value) => {
      const items: Item[] = categories.map((category, ci) => {
        const row = data.rows.find((r) => same(r[d], category) && same(r[s], value));
        const number = row ? chartNumber(row[m.i]) : null;
        return {
          value:
            config.stack === 'percent' && number !== null && totals[ci] !== 0
              ? (number / totals[ci]) * 100
              : number,
          key: [category, value],
          raw: row ? row[m.i] : null,
          itemStyle: look([category, value], context),
        };
      });
      all = all.concat(items);
      const name = categoryText(value, data.columns[s].type, config.series?.bucket, locale);
      return {
        type: 'bar',
        name,
        stack: stacked ? 'total' : undefined,
        barMaxWidth: 24,
        itemStyle: {
          color: context.colors.color('series:' + keyText([value]), theme),
          borderRadius: stacked ? 0 : radius,
          borderColor: stacked ? theme.surface : undefined,
          borderWidth: stacked ? 1 : 0,
        },
        label: label(config.labels, horizontal, m.c.format, locale, theme),
        data: items,
      };
    });
  }
  const names = categories.map((category) =>
    categoryText(category, type, config.dimension.bucket, locale),
  );
  const format = measures[0]?.c.format ?? null;
  const categoryAxis = {
    type: 'category' as const,
    data: names,
    inverse: horizontal,
    name: config.xTitle ?? undefined,
    nameLocation: 'middle' as const,
    nameGap: 28,
    axisLabel: { color: theme.muted, hideOverlap: true },
    axisLine: { lineStyle: { color: theme.grid } },
    axisTick: { show: false },
  };
  const valueAxis = {
    type: 'value' as const,
    max: config.stack === 'percent' ? 100 : undefined,
    name: config.yTitle ?? undefined,
    nameTextStyle: { color: theme.muted },
    axisLabel: {
      color: theme.muted,
      formatter: (n: number) =>
        formatNumber(n, { ...(format ?? emptyFormat), compact: true, decimals: null }, locale),
    },
    splitLine: { lineStyle: { color: theme.grid, width: 1, type: 'solid' as const } },
  };
  return {
    ...common(
      context,
      summary(
        'Bar chart',
        measures.map((m) => m.c.label),
        config.dimension.label,
        all,
        format,
        locale,
        context.selection,
        names,
      ),
    ),
    tooltip: {
      ...tooltip(context),
      trigger: 'item',
      formatter: (p: unknown) => {
        const params = p as { name: string; seriesName: string; data: Item; marker: string };
        const shown =
          series.length > 1 ? `${params.marker}${escapeHtml(params.seriesName)}: ` : params.marker;
        return `${escapeHtml(params.name)}<br>${shown}${escapeHtml(formatNumber(params.data.raw, format, locale))}`;
      },
    },
    legend:
      series.length >= 2 && legendAt !== 'none' ? legend(legendAt, theme, false) : { show: false },
    grid: {
      outerBoundsMode: 'same',
      outerBoundsContain: 'all',
      left: 8,
      right: 16,
      top: 16,
      bottom: series.length >= 2 && legendAt === 'bottom' ? 40 : 8,
    },
    xAxis: horizontal ? valueAxis : categoryAxis,
    yAxis: horizontal ? categoryAxis : valueAxis,
    series: series as never,
  };
}

export function lineOption(
  config: LineConfig,
  data: WidgetData,
  context: ChartContext,
): EChartsOption {
  const { theme, locale } = context;
  const legendAt = legendPosition(config.legend, context);
  const d = column(data, 'dimension');
  const s = column(data, 'series');
  const type = data.columns[d].type;
  const measures = data.columns.map((c, i) => ({ c, i })).filter(({ c }) => c.role === 'measure');
  let categories = unique(data.rows.map((row) => row[d]));
  // Periods without rows are there all the same: as zero, or a gap (or the line goes on over them).
  const present = categories.filter((c) => c !== null);
  if (config.gaps !== 'connect' && present.length >= 2) {
    const filled = periodsBetween(
      String(present[0]),
      String(present.at(-1)),
      config.dimension.bucket,
    );
    if (filled) {
      categories = categories.includes(null) ? [null, ...filled] : filled;
    }
  }
  const zero = config.gaps === 'zero';
  const lines: { name: string; entity: string; items: Item[] }[] = [];
  if (s < 0) {
    for (const { c, i } of measures) {
      lines.push({
        name: c.label,
        entity: 'measure:' + c.label,
        items: categories.map((category) => {
          const row = data.rows.find((r) => same(r[d], category));
          const value = row ? chartNumber(row[i]) : null;
          return {
            value: value ?? (zero ? 0 : null),
            key: [category],
            raw: row ? row[i] : null,
            itemStyle: look([category], context),
          };
        }),
      });
    }
  } else {
    const m = measures[0];
    for (const value of unique(data.rows.map((row) => row[s]))) {
      lines.push({
        name: categoryText(value, data.columns[s].type, config.series?.bucket, locale),
        entity: 'series:' + keyText([value]),
        items: categories.map((category) => {
          const row = data.rows.find((r) => same(r[d], category) && same(r[s], value));
          const number = row ? chartNumber(row[m.i]) : null;
          return {
            value: number ?? (zero ? 0 : null),
            key: [category, value],
            raw: row ? row[m.i] : null,
            itemStyle: look([category, value], context),
          };
        }),
      });
    }
  }
  const names = categories.map((category) =>
    categoryText(category, type, config.dimension.bucket, locale),
  );
  const format = measures[0]?.c.format ?? null;
  return {
    ...common(
      context,
      summary(
        'Line chart',
        measures.map((m) => m.c.label),
        config.dimension.label,
        lines.flatMap((l) => l.items),
        format,
        locale,
        context.selection,
        names,
      ),
    ),
    tooltip: {
      ...tooltip(context),
      trigger: 'axis',
      axisPointer: { type: 'line', lineStyle: { color: theme.grid } },
      formatter: (p: unknown) => {
        const points = p as { name: string; seriesName: string; data: Item; marker: string }[];
        if (points.length === 0) {
          return '';
        }
        const rows = points.map(
          (point) =>
            `${point.marker}${lines.length > 1 ? escapeHtml(point.seriesName) + ': ' : ''}${escapeHtml(formatNumber(point.data.raw, format, locale))}`,
        );
        return `${escapeHtml(points[0].name)}<br>${rows.join('<br>')}`;
      },
    },
    legend:
      lines.length >= 2 && legendAt !== 'none' ? legend(legendAt, theme, false) : { show: false },
    grid: {
      outerBoundsMode: 'same',
      outerBoundsContain: 'all',
      left: 8,
      right: 16,
      top: 16,
      bottom: lines.length >= 2 && legendAt === 'bottom' ? 40 : 8,
    },
    xAxis: {
      type: 'category',
      data: names,
      boundaryGap: false,
      name: config.xTitle ?? undefined,
      nameLocation: 'middle',
      nameGap: 28,
      axisLabel: { color: theme.muted, hideOverlap: true },
      axisLine: { lineStyle: { color: theme.grid } },
      axisTick: { show: false },
    },
    yAxis: {
      type: 'value',
      name: config.yTitle ?? undefined,
      nameTextStyle: { color: theme.muted },
      axisLabel: {
        color: theme.muted,
        formatter: (n: number) =>
          formatNumber(n, { ...(format ?? emptyFormat), compact: true, decimals: null }, locale),
      },
      splitLine: { lineStyle: { color: theme.grid, width: 1, type: 'solid' } },
    },
    series: lines.map((line) => {
      const color = context.colors.color(line.entity, theme);
      return {
        type: 'line',
        name: line.name,
        data: line.items,
        connectNulls: config.gaps === 'connect',
        showSymbol: true,
        symbol: 'circle',
        symbolSize: 8,
        lineStyle: { width: 2, cap: 'round', join: 'round', color },
        itemStyle: { color, borderColor: theme.surface, borderWidth: 2 },
        areaStyle: config.area ? { color, opacity: 0.1 } : undefined,
        label: label(config.labels, false, format, locale, theme),
      };
    }) as never,
  };
}

const emptyFormat: NumberFormat = { decimals: null, prefix: null, suffix: null, compact: false };

/** The index of a column of a role (the first, or of an index among its role's); -1 when there is none. */
export function column(
  data: WidgetData,
  role: WidgetData['columns'][number]['role'],
  index = 0,
): number {
  return data.columns.findIndex((c) => c.role === role && c.index === index);
}

/** How an item looks for the chart's selection: those not chosen faded; those left out faded and hatched (not told by color alone). */
function look(key: Key, context: ChartContext): Record<string, unknown> {
  const selection = context.selection;
  if (!selection || selection.keys.length === 0) {
    return {};
  }
  const chosen = selection.keys.some((k) => keyText(k) === keyText(key));
  if (selection.mode === 'include') {
    return chosen ? {} : { opacity: 0.3 };
  }
  return chosen
    ? {
        opacity: 0.25,
        decal: {
          symbol: 'rect',
          dashArrayX: [1, 0],
          dashArrayY: [2, 4],
          rotation: Math.PI / 4,
          color: context.theme.text,
        },
      }
    : {};
}

function common(context: ChartContext, description: string): EChartsOption {
  return {
    backgroundColor: 'transparent',
    animation: !context.reducedMotion,
    textStyle: { fontFamily: context.theme.font, color: context.theme.text },
    aria: { enabled: true, label: { description } },
  };
}

function tooltip(context: ChartContext): Record<string, unknown> {
  return {
    confine: true,
    backgroundColor: context.theme.tooltip,
    borderWidth: 0,
    textStyle: { color: context.theme.tooltipText, fontFamily: context.theme.font },
    transitionDuration: context.reducedMotion ? 0 : 0.2,
  };
}

/** Where a chart's legend is: where its config says, but below a narrow chart rather than beside it. */
function legendPosition(position: LegendPosition, context: ChartContext): LegendPosition {
  return context.narrow && (position === 'left' || position === 'right') ? 'bottom' : position;
}

/** A pie's middle, moved away from its legend. */
function pieCenter(legendAt: LegendPosition): [string, string] {
  switch (legendAt) {
    case 'right':
      return ['40%', '50%'];
    case 'left':
      return ['60%', '50%'];
    case 'bottom':
      return ['50%', '46%'];
    case 'top':
      return ['50%', '54%'];
    case 'none':
      return ['50%', '50%'];
  }
}

function legend(position: string, theme: ChartTheme, slices: boolean): Record<string, unknown> {
  const side = position === 'left' || position === 'right';
  return {
    show: true,
    type: 'scroll',
    orient: side ? 'vertical' : 'horizontal',
    [position]: 0,
    ...(side ? { top: 'middle' } : {}),
    textStyle: { color: theme.text },
    pageTextStyle: { color: theme.muted },
    icon: 'circle',
    // Clicking the legend would hide a slice or a series, which looks like choosing it: it doesn't.
    selectedMode: slices ? false : true,
  };
}

function label(
  show: boolean,
  horizontal: boolean,
  format: NumberFormat | null | undefined,
  locale: string,
  theme: ChartTheme,
): Record<string, unknown> | undefined {
  return show
    ? {
        show: true,
        position: horizontal ? 'right' : 'top',
        color: theme.text,
        formatter: (p: { data: Item }) => formatNumber(p.data.raw, format, locale),
      }
    : undefined;
}

/** A sentence that says what the chart shows, for screen readers: what of what, how many, the largest, what is chosen. */
function summary(
  kind: string,
  measures: readonly string[],
  dimension: string,
  items: readonly Item[],
  format: NumberFormat | null | undefined,
  locale: string,
  selection: Selection | null,
  names?: readonly string[],
): string {
  const values = items.filter((i) => i.value !== null);
  const highest = values.reduce<Item | null>(
    (best, item) => (best === null || (item.value ?? 0) > (best.value ?? 0) ? item : best),
    null,
  );
  const count = names?.length ?? items.length;
  const parts = [
    `${kind} of ${measures.join(', ')} by ${dimension}, ${count} ${count === 1 ? 'category' : 'categories'}`,
  ];
  if (highest) {
    const at =
      highest.name ??
      (names && highest.key ? names[items.indexOf(highest) % names.length] : undefined);
    parts.push(`highest ${at ? at + ' ' : ''}${formatNumber(highest.raw, format, locale)}`);
  }
  if (selection && selection.keys.length > 0) {
    parts.push(`${selection.mode === 'include' ? 'chosen' : 'left out'}: ${selection.keys.length}`);
  }
  return parts.join('; ');
}

function unique(values: readonly unknown[]): unknown[] {
  const seen = new Set<string>();
  const kept: unknown[] = [];
  for (const value of values) {
    const text = JSON.stringify(value);
    if (!seen.has(text)) {
      seen.add(text);
      kept.push(value);
    }
  }
  return kept;
}

function same(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}
