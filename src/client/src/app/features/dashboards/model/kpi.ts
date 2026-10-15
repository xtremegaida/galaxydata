import type { TypeDto } from '../../browse/grid/grid-columns';
import { categoryText, chartNumber, formatNumber, periodsBetween } from '../charts/chart-values';
import type {
  Bucket,
  KpiCompare,
  KpiConfig,
  Measure,
  NumberFormat,
  WidgetColumn,
  WidgetData,
} from './definition';

/** Which way a KPI's number went against the period before's, and whether that is for the better. */
export interface KpiChange {
  /** The number less the one before (to as many decimals as theirs). */
  readonly delta: number;
  /** As a share of the one before; none when that was zero. */
  readonly ratio: number | null;
  readonly direction: 'up' | 'down' | 'flat';
  readonly tone: 'good' | 'bad' | 'neutral';
}

/** The decimals a value is written with: `"349.50"` has 2. */
function decimalsOf(value: unknown): number {
  const text = typeof value === 'string' ? value : typeof value === 'number' ? String(value) : '';
  const point = text.indexOf('.');
  return point < 0 || /e/i.test(text) ? 0 : text.length - point - 1;
}

/**
 * A KPI's change against the period before: none when either number is missing. Up is good when `good` says so,
 * down when it says that; neither colours nothing, and nor does no change.
 */
export function kpiChange(
  value: unknown,
  previous: unknown,
  good: KpiCompare['good'],
): KpiChange | null {
  const now = chartNumber(value);
  const before = chartNumber(previous);
  if (now === null || before === null) {
    return null;
  }
  const decimals = Math.min(20, Math.max(decimalsOf(value), decimalsOf(previous)));
  const delta = Number((now - before).toFixed(decimals));
  const direction = delta > 0 ? 'up' : delta < 0 ? 'down' : 'flat';
  const tone =
    direction === 'flat' || good === 'neither'
      ? 'neutral'
      : (direction === 'up') === (good === 'up')
        ? 'good'
        : 'bad';
  return { delta, ratio: before === 0 ? null : delta / Math.abs(before), direction, tone };
}

/** A change as it is shown and said: the % (none against zero), the amount signed, and in words. */
export interface KpiChangeText {
  readonly percent: string | null;
  readonly amount: string;
  readonly icon: string;
  readonly words: string;
}

export function changeText(
  change: KpiChange,
  format: NumberFormat | null,
  period: string,
  locale: string,
): KpiChangeText {
  const percent =
    change.ratio === null
      ? null
      : new Intl.NumberFormat(locale, {
          style: 'percent',
          maximumFractionDigits: 1,
          signDisplay: 'exceptZero',
        }).format(change.ratio);
  const size = formatNumber(Math.abs(change.delta), format, locale);
  const sign = change.direction === 'up' ? '+' : change.direction === 'down' ? '−' : '';
  const unsigned = percent?.replace(/^[+\-−]/, '');
  const by = unsigned ? `${unsigned} (${size})` : size;
  const tone =
    change.tone === 'neutral' ? '' : `, for the ${change.tone === 'good' ? 'better' : 'worse'}`;
  const words =
    change.direction === 'flat'
      ? `The same as in ${period}`
      : `${change.direction === 'up' ? 'Up' : 'Down'} ${by} on ${period}${tone}`;
  return {
    percent,
    amount: `${sign}${size}`,
    icon:
      change.direction === 'up'
        ? 'trending_up'
        : change.direction === 'down'
          ? 'trending_down'
          : 'trending_flat',
    words,
  };
}

/** A period's first and last days as read: "15 Aug – 13 Sep", the year too when it isn't this one's. */
export function periodText(from: string, to: string, locale: string, today = new Date()): string {
  const first = new Date(`${from}T00:00:00Z`);
  const last = new Date(`${to}T00:00:00Z`);
  if (Number.isNaN(first.getTime()) || Number.isNaN(last.getTime())) {
    return `${from} – ${to}`;
  }
  const thisYear =
    first.getUTCFullYear() === today.getUTCFullYear() &&
    last.getUTCFullYear() === today.getUTCFullYear();
  const format = new Intl.DateTimeFormat(locale, {
    day: 'numeric',
    month: 'short',
    year: thisYear ? undefined : 'numeric',
    timeZone: 'UTC',
  });
  return from === to ? format.format(first) : format.formatRange(first, last);
}

/** A KPI's number as it is shown: its measure's format; else a decimal's scale, and at most two decimals of a float. */
export function kpiFormat(column: WidgetColumn | undefined): NumberFormat | null {
  if (!column) {
    return null;
  }
  if (column.format) {
    return column.format;
  }
  const plain = { prefix: null, suffix: null, compact: false };
  switch (column.type.kind) {
    case 'decimal':
      return column.type.scale === null || column.type.scale === undefined
        ? null
        : { ...plain, decimals: column.type.scale };
    case 'double':
    case 'single':
      return { ...plain, decimals: 2 };
    default:
      return null;
  }
}

/** A point of a sparkline: its period's first day, and the measure (none: no value then). */
export interface SparkPoint {
  readonly day: string;
  readonly value: number | null;
}

/** The measures that are zero over no rows (counts and sums); others have no value then. */
const zeroWhenEmpty: ReadonlySet<Measure['aggregate']> = new Set([
  'count',
  'countValues',
  'countDistinct',
  'approxCountDistinct',
  'sum',
]);

/**
 * A sparkline's points from the answer's (`[day, value]`, the oldest first), every period between the first and
 * the last: those without rows are zero for counts and sums, and gaps for the rest.
 */
export function sparkPoints(
  points: readonly (readonly unknown[])[] | null | undefined,
  bucket: Bucket | null | undefined,
  aggregate: Measure['aggregate'],
): SparkPoint[] {
  const given = (points ?? [])
    .filter((p) => typeof p[0] === 'string')
    .map((p) => ({ day: p[0] as string, value: chartNumber(p[1]) }));
  if (given.length < 2) {
    return given;
  }
  const all = periodsBetween(given[0].day, given[given.length - 1].day, bucket);
  if (!all) {
    return given;
  }
  const byDay = new Map(given.map((p) => [p.day, p.value]));
  const none = zeroWhenEmpty.has(aggregate) ? 0 : null;
  return all.map((day) => ({ day, value: byDay.has(day) ? byDay.get(day)! : none }));
}

/** Where a sparkline's points are in a box of 100 by 100 (y down), and its line through them, broken at gaps. */
export interface SparkShape {
  readonly xs: readonly number[];
  readonly ys: readonly (number | null)[];
  readonly line: string;
  /** The latest point with a value. */
  readonly last: number | null;
}

export function sparkShape(points: readonly SparkPoint[]): SparkShape | null {
  const values = points.map((p) => p.value).filter((v): v is number => v !== null);
  if (values.length === 0) {
    return null;
  }
  const min = Math.min(...values);
  const max = Math.max(...values);
  const n = points.length;
  const xs = points.map((_, i) => (n === 1 ? 50 : (i / (n - 1)) * 100));
  // Kept off the edges, so the line's stroke isn't cut.
  const ys = points.map((p) =>
    p.value === null ? null : max === min ? 50 : 92 - ((p.value - min) / (max - min)) * 84,
  );
  const round = (v: number) => Math.round(v * 100) / 100;
  let line = '';
  let drawing = false;
  ys.forEach((y, i) => {
    if (y === null) {
      drawing = false;
      return;
    }
    line += `${drawing ? 'L' : 'M'}${round(xs[i])} ${round(y)} `;
    drawing = true;
  });
  let last: number | null = null;
  for (let i = n - 1; i >= 0 && last === null; i--) {
    if (ys[i] !== null) {
      last = i;
    }
  }
  return { xs, ys, line: line.trim(), last };
}

const dateType: TypeDto = { kind: 'date', nullable: false, text: 'date' };

/** A sparkline's point as read: its period, and the measure in its format. */
export function pointText(
  point: SparkPoint,
  bucket: Bucket | null | undefined,
  format: NumberFormat | null,
  locale: string,
): string {
  return `${categoryText(point.day, dateType, bucket, locale)}: ${formatNumber(point.value, format, locale)}`;
}

/**
 * A KPI's sparkline as rows of its own (its period, and the measure), as the Data popup lists them; none without
 * one, or points.
 */
export function pointsData(data: WidgetData, config: KpiConfig): WidgetData | null {
  const points = data.points;
  const measure = data.columns.find((c) => c.role === 'measure');
  if (!config.sparkline || !points || points.length === 0 || !measure) {
    return null;
  }
  return {
    ...data,
    columns: [
      {
        name: 'd0',
        role: 'dimension',
        index: 0,
        label: config.sparkline.label,
        type: dateType,
        format: null,
      },
      { ...measure, name: 'm0' },
    ],
    rows: points,
  };
}
