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
import type { SeriesColors } from './series-colors';

export { ColorMemory } from './series-colors';

/** What a chart is drawn with besides its config and rows. */
export interface ChartContext {
  readonly theme: ChartTheme;
  readonly locale: string;
  /** The slices chosen in this chart (highlighted; excluded ones faded and hatched). */
  readonly selection: Selection | null;
  readonly reducedMotion: boolean;
  /** What each slice, series or measure (or a bar chart's category) is painted, by entity (`coloredEntities`). */
  readonly colors: SeriesColors;
  /** Whether the chart is narrow (a phone's width): a legend at a side goes below it, leaving the plot room. */
  readonly narrow: boolean;
}

/**
 * A chart's item as the chart keeps it: its value drawn, its key (for choosing it), and its value as it came. One of
 * "Other" (the rest of the slices or series) has no key, and is marked: clicking it steps into the rest.
 */
interface Item {
  value: number | null;
  name?: string;
  key: Key | null;
  raw: unknown;
  other?: true;
  itemStyle?: Record<string, unknown>;
}

/**
 * How many slices or series a step into a chart's "Other" shows: a pie's limit, or the largest series of a chart
 * split by a series; none for a chart without an "Other" to step into.
 */
export function stepOf(config: ChartConfig): number | null {
  if (config.kind === 'pie') {
    return config.other ? config.limit : null;
  }
  return config.series && config.seriesLimit && config.seriesOther ? config.seriesLimit : null;
}

/** The id of a chart's "Other" series: a click anywhere on its line (not only its points) steps into it. */
export const otherSeries = 'gd-other';

/** What "Other" is called beside the names shown: told apart from a value of that name. */
export function otherName(names: readonly string[]): string {
  return names.includes('Other') ? 'Other (the rest)' : 'Other';
}

/** What a step into a chart's "Other" shows, one and many: a pie's slices, the others' series. */
export function stepNoun(config: ChartConfig): { one: string; many: string } {
  return config.kind === 'pie'
    ? { one: 'slice', many: 'slices' }
    : { one: 'series', many: 'series' };
}

/** The slices or series from the first to the last (counted from 1): "slices 11–20", "slice 2". */
export function stepRange(config: ChartConfig, first: number, last: number): string {
  const noun = stepNoun(config);
  return last > first ? `${noun.many} ${first}–${last}` : `${noun.one} ${first}`;
}

/** How many slices or series a chart shows: those of a step into its "Other". */
export function stepShown(config: ChartConfig, data: WidgetData): number {
  return config.kind === 'pie' ? data.rows.length : (data.series?.length ?? 0);
}

/** What a tooltip on "Other" adds: that clicking it shows the next ones, when it does. */
function otherHint(config: ChartConfig, data: WidgetData): string {
  const size = stepOf(config);
  if (size === null || data.next === null || data.next === undefined) {
    return '';
  }
  const noun = stepNoun(config);
  return `<br>${escapeHtml(`Click to see the next ${size === 1 ? noun.one : `${size} ${noun.many}`}`)}`;
}

/** Where a chart stepped into its "Other" is, for screen readers: the slices or series shown, and the rest as Other. */
function stepped(config: ChartConfig, data: WidgetData, other: boolean): string[] {
  const parts: string[] = [];
  if (stepOf(config) !== null && data.offset > 0) {
    const shown = Math.max(stepShown(config, data), 1);
    parts.push(stepRange(config, data.offset + 1, data.offset + shown));
  }
  if (other) {
    parts.push('the rest as Other');
  }
  return parts;
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
  const other = data.other !== null && data.other !== undefined;
  if (other) {
    items.push({
      name: otherName(items.map((i) => i.name ?? '')),
      value: chartNumber(data.other),
      key: null,
      raw: data.other,
      other: true,
      itemStyle: { color: theme.neutral, ...lookOther(context) },
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
        undefined,
        stepped(config, data, other),
      ),
    ),
    tooltip: {
      ...tooltip(context),
      trigger: 'item',
      formatter: (p: unknown) => {
        const params = p as { name: string; percent: number; data: Item; marker: string };
        const hint = params.data.other ? otherHint(config, data) : '';
        return `${params.marker}${escapeHtml(params.name)}<br>${escapeHtml(formatNumber(params.data.raw, format, locale))} (${formatNumber(params.percent, { decimals: 1, prefix: null, suffix: '%', compact: false }, locale)})${hint}`;
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
    const byCategory = config.colorBy === 'category' && measures.length === 1;
    series = measures.map(({ c, i }) => {
      const items: Item[] = data.rows.map((row) => ({
        value: chartNumber(row[i]),
        key: [row[d]],
        raw: row[i],
        itemStyle: byCategory
          ? {
              color: context.colors.color('category:' + keyText([row[d]]), theme),
              ...look([row[d]], context),
            }
          : look([row[d]], context),
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
    // The largest first, when they are limited; as they came otherwise.
    const values = data.series ? [...data.series] : unique(data.rows.map((row) => row[s]));
    const rest = data.otherRows ?? null;
    const totals = categories.map(
      (category) =>
        data.rows
          .filter((row) => same(row[d], category))
          .reduce((sum, row) => sum + (chartNumber(row[m.i]) ?? 0), 0) +
        (rest ?? [])
          .filter((row) => same(row[0], category))
          .reduce((sum, row) => sum + (chartNumber(row[1]) ?? 0), 0),
    );
    const percent = (number: number | null, ci: number) =>
      config.stack === 'percent' && number !== null && totals[ci] !== 0
        ? (number / totals[ci]) * 100
        : number;
    series = values.map((value) => {
      const items: Item[] = categories.map((category, ci) => {
        const row = data.rows.find((r) => same(r[d], category) && same(r[s], value));
        const number = row ? chartNumber(row[m.i]) : null;
        return {
          value: percent(number, ci),
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
    if (rest) {
      // The rest of the series, as one: not chosen, but stepped into.
      const items: Item[] = categories.map((category, ci) => {
        const row = rest.find((r) => same(r[0], category));
        const number = row ? chartNumber(row[1]) : null;
        return {
          value: percent(number, ci),
          key: null,
          raw: row ? row[1] : null,
          other: true,
          itemStyle: lookOther(context),
        };
      });
      all = all.concat(items);
      series.push({
        type: 'bar',
        id: otherSeries,
        name: otherName(series.map((one) => String(one['name']))),
        stack: stacked ? 'total' : undefined,
        barMaxWidth: 24,
        itemStyle: {
          color: theme.neutral,
          borderRadius: stacked ? 0 : radius,
          borderColor: stacked ? theme.surface : undefined,
          borderWidth: stacked ? 1 : 0,
        },
        label: label(config.labels, horizontal, m.c.format, locale, theme),
        data: items,
      });
    }
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
        stepped(config, data, !!data.otherRows),
      ),
    ),
    tooltip: {
      ...tooltip(context),
      trigger: 'item',
      formatter: (p: unknown) => {
        const params = p as { name: string; seriesName: string; data: Item; marker: string };
        const shown =
          series.length > 1 ? `${params.marker}${escapeHtml(params.seriesName)}: ` : params.marker;
        const hint = params.data.other ? otherHint(config, data) : '';
        return `${escapeHtml(params.name)}<br>${shown}${escapeHtml(formatNumber(params.data.raw, format, locale))}${hint}`;
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
  // Its points' x values; with its rest drawn, the server's (some may be the rest's alone).
  let categories = data.categories ? [...data.categories] : unique(data.rows.map((row) => row[d]));
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
  const lines: { name: string; entity: string | null; items: Item[]; other?: true }[] = [];
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
    // The largest first, when they are limited; as they came otherwise.
    for (const value of data.series ? [...data.series] : unique(data.rows.map((row) => row[s]))) {
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
    const rest = data.otherRows;
    if (rest) {
      // The rest of the series, as one: not chosen, but stepped into.
      lines.push({
        name: otherName(lines.map((line) => line.name)),
        entity: null,
        other: true,
        items: categories.map((category) => {
          const row = rest.find((r) => same(r[0], category));
          const number = row ? chartNumber(row[1]) : null;
          return {
            value: number ?? (zero ? 0 : null),
            key: null,
            raw: row ? row[1] : null,
            other: true,
            itemStyle: lookOther(context),
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
        stepped(config, data, !!data.otherRows),
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
        const hint = points.some((point) => point.data.other) ? otherHint(config, data) : '';
        return `${escapeHtml(points[0].name)}<br>${rows.join('<br>')}${hint}`;
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
      const color = line.entity === null ? theme.neutral : context.colors.color(line.entity, theme);
      return {
        type: 'line',
        ...(line.other ? { id: otherSeries, triggerEvent: true } : {}),
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

/** How "Other" looks for the chart's selection: never chosen, so faded while slices are. */
function lookOther(context: ChartContext): Record<string, unknown> {
  const selection = context.selection;
  return selection && selection.mode === 'include' && selection.keys.length > 0
    ? { opacity: 0.3 }
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
  more: readonly string[] = [],
): string {
  // The highest of those shown: "Other" is the rest of them, not one.
  const values = items.filter((i) => i.value !== null && !i.other);
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
  parts.push(...more);
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
