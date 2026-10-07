import type { BarConfig, LineConfig, PieConfig, WidgetData } from '../model/definition';
import { barDefaults, lineDefaults, pieDefaults } from '../model/widget-defaults';
import { actionOf } from './chart-events';
import { ColorMemory, type ChartContext, barOption, lineOption, pieOption } from './chart-options';
import { fallbackTheme } from './chart-theme';
import {
  categoryText,
  chartNumber,
  escapeHtml,
  formatNumber,
  periodsBetween,
} from './chart-values';

const text = { kind: 'string', nullable: true, text: 'string?' } as const;
const int64 = { kind: 'int64', nullable: false, text: 'int64' } as const;
const decimal = { kind: 'decimal', nullable: false, text: 'decimal(10,2)' } as const;
const date = { kind: 'date', nullable: false, text: 'date' } as const;

function dataOf(
  columns: [
    string,
    'dimension' | 'series' | 'measure',
    typeof text | typeof int64 | typeof decimal | typeof date,
  ][],
  rows: unknown[][],
  extra: Partial<WidgetData> = {},
): WidgetData {
  const indexes: Record<string, number> = {};
  return {
    columns: columns.map(([label, role, type]) => {
      const index = indexes[role] ?? 0;
      indexes[role] = index + 1;
      return { name: role[0] + index, role, index, label, type, format: null };
    }),
    rows,
    truncated: false,
    offset: 0,
    total: null,
    categories: null,
    other: null,
    refreshedAt: '2026-03-01T10:00:00Z',
    cached: false,
    issues: [],
    ...extra,
  } as WidgetData;
}

function contextOf(partial: Partial<ChartContext> = {}): ChartContext {
  return {
    theme: fallbackTheme(false),
    locale: 'en-ZA',
    selection: null,
    reducedMotion: false,
    colors: new ColorMemory(),
    narrow: false,
    ...partial,
  };
}

interface Series {
  name: string;
  type: string;
  data: {
    value: number | null;
    key: unknown[];
    raw: unknown;
    itemStyle?: Record<string, unknown>;
    name?: string;
  }[];
}

describe('chart values', () => {
  it('draws numbers, and text of whole numbers and decimals as numbers', () => {
    expect([
      chartNumber(3),
      chartNumber('9007199254740993'),
      chartNumber('12.25'),
      chartNumber(null),
      chartNumber('x'),
      chartNumber(Number.NaN),
    ]).toEqual([3, 9007199254740992, 12.25, null, null, null]);
  });

  it('labels numbers exactly, in the locale, as the measure says', () => {
    expect(formatNumber('9007199254740993', null, 'en-US')).toBe('9,007,199,254,740,993');
    expect(
      formatNumber('1234.5', { decimals: 2, prefix: 'R ', suffix: null, compact: false }, 'en-US'),
    ).toBe('R 1,234.50');
    expect(
      formatNumber(1234567, { decimals: null, prefix: null, suffix: null, compact: true }, 'en-US'),
    ).toBe('1.2M');
    expect(formatNumber(null, null, 'en-US')).toBe('—');
  });

  it('labels periods as they are read, and parts that repeat by name', () => {
    expect(categoryText('2026-01-01', date, 'month', 'en-US')).toBe('Jan 2026');
    expect(categoryText('2026-04-01', date, 'quarter', 'en-US')).toBe('Q2 2026');
    expect(categoryText('2026-01-05', date, 'week', 'en-US')).toBe('Week of Jan 5, 2026');
    expect(categoryText(1, int64, 'dayOfWeek', 'en-US')).toBe('Monday');
    expect(categoryText(7, int64, 'dayOfWeek', 'en-US')).toBe('Sunday');
    expect(categoryText(3, int64, 'monthOfYear', 'en-US')).toBe('March');
    expect(categoryText(9, int64, 'hourOfDay', 'en-US')).toBe('09:00');
    expect(categoryText(null, text, null, 'en-US')).toBe('(no value)');
    expect(categoryText('1001', int64, null, 'en-US')).toBe('1,001');
  });

  it('lists the periods between two, for gaps', () => {
    expect(periodsBetween('2026-01-01', '2026-04-01', 'month')).toEqual([
      '2026-01-01',
      '2026-02-01',
      '2026-03-01',
      '2026-04-01',
    ]);
    expect(periodsBetween('2026-01-05', '2026-01-19', 'week')).toEqual([
      '2026-01-05',
      '2026-01-12',
      '2026-01-19',
    ]);
    expect(periodsBetween('2026-01-01', '2036-01-01', 'day', 100)).toBeNull();
    expect(periodsBetween('1', '3', null)).toBeNull();
  });

  it('makes database text safe in tooltips', () => {
    expect(escapeHtml('<img src=x onerror="alert(1)">')).toBe(
      '&lt;img src=x onerror=&quot;alert(1)&quot;&gt;',
    );
  });
});

describe('chart events', () => {
  const keys = (k: Partial<MouseEvent>) => ({
    altKey: false,
    ctrlKey: false,
    metaKey: false,
    ...k,
  });

  it('chooses alone, adds with Ctrl (⌘ on a Mac), leaves out with Alt', () => {
    expect(actionOf(keys({}), false)).toBe('replace');
    expect(actionOf(keys({ ctrlKey: true }), false)).toBe('add');
    expect(actionOf(keys({ metaKey: true }), false)).toBe('replace');
    expect(actionOf(keys({ metaKey: true }), true)).toBe('add');
    expect(actionOf(keys({ altKey: true, ctrlKey: true }), false)).toBe('exclude');
    expect(actionOf(undefined, false)).toBe('replace');
  });
});

describe('chart options', () => {
  const statuses = dataOf(
    [
      ['Status', 'dimension', text],
      ['Orders', 'measure', int64],
    ],
    [
      ['open', '2'],
      ['cancelled', '1'],
      [null, '1'],
    ],
  );

  it('draws bars of a measure by a dimension, keyed for choosing, values exact in tooltips', () => {
    const config: BarConfig = {
      ...barDefaults('orders'),
      dimension: { field: { path: [], column: 'status' }, bucket: null, label: 'Status' },
    };
    const option = barOption(config, statuses, contextOf());
    const series = option.series as unknown as Series[];
    expect(series).toHaveLength(1);
    expect(series[0].data.map((d) => [d.value, d.key])).toEqual([
      [2, ['open']],
      [1, ['cancelled']],
      [1, [null]],
    ]);
    expect((option.xAxis as { data: string[] }).data).toEqual(['open', 'cancelled', '(no value)']);
    expect((option.legend as { show: boolean }).show).toBe(false);
    const tip = (option.tooltip as { formatter: (p: unknown) => string }).formatter({
      name: '<b>',
      seriesName: 'Orders',
      marker: '',
      data: series[0].data[0],
    });
    expect(tip).toBe('&lt;b&gt;<br>2');
    expect((option.aria as { label: { description: string } }).label.description).toBe(
      'Bar chart of Orders by Status, 3 categories; highest open 2',
    );
  });

  it('draws a series in the categories found first, with a legend, and stacks to 100%', () => {
    const data = dataOf(
      [
        ['Status', 'dimension', text],
        ['Customer', 'series', int64],
        ['Orders', 'measure', int64],
      ],
      [
        ['open', '1', '1'],
        ['open', '2', '3'],
        ['shipped', '1', '1'],
      ],
      { categories: ['shipped', 'open'] },
    );
    const config: BarConfig = {
      ...barDefaults('orders'),
      series: { field: { path: [], column: 'customer_id' }, bucket: null, label: 'Customer' },
      stack: 'percent',
    };
    const option = barOption(config, data, contextOf());
    const series = option.series as unknown as Series[];
    expect(series.map((s) => s.name)).toEqual(['1', '2']);
    expect(series[0].data.map((d) => [d.key, d.value])).toEqual([
      [['shipped', '1'], 100],
      [['open', '1'], 25],
    ]);
    expect(series[1].data.map((d) => d.value)).toEqual([null, 75]);
    expect((option.legend as { show: boolean }).show).toBe(true);
  });

  it("fades what isn't chosen, and hatches what is left out", () => {
    const config: BarConfig = barDefaults('orders');
    const chosen = barOption(
      config,
      statuses,
      contextOf({ selection: { mode: 'include', keys: [['open']] } }),
    );
    expect(
      (chosen.series as unknown as Series[])[0].data.map((d) => d.itemStyle?.['opacity']),
    ).toEqual([undefined, 0.3, 0.3]);
    const out = barOption(
      config,
      statuses,
      contextOf({ selection: { mode: 'exclude', keys: [[null]] } }),
    );
    const items = (out.series as unknown as Series[])[0].data;
    expect([items[0].itemStyle?.['opacity'], items[2].itemStyle?.['opacity']]).toEqual([
      undefined,
      0.25,
    ]);
    expect(items[2].itemStyle?.['decal']).toBeDefined();
  });

  it('gives slices their colors by what they are, kept as others come and go', () => {
    const colors = new ColorMemory();
    const theme = fallbackTheme(false);
    const config: PieConfig = pieDefaults('orders');
    const first = pieOption(config, statuses, contextOf({ colors }));
    const later = pieOption(
      config,
      dataOf(
        [
          ['Status', 'dimension', text],
          ['Orders', 'measure', int64],
        ],
        [['cancelled', '1']],
      ),
      contextOf({ colors }),
    );
    const color = (o: typeof first, i: number) =>
      ((o.series as unknown as Series[])[0].data[i].itemStyle as { color: string }).color;
    expect([color(first, 0), color(first, 1)]).toEqual([theme.series[0], theme.series[1]]);
    expect(color(later, 0)).toBe(theme.series[1]);
    const nine = new ColorMemory();
    Array.from({ length: 8 }, (_, i) => nine.slot(String(i)));
    expect(nine.color('ninth', theme)).toBe(theme.neutral);
  });

  it("adds the rest of a pie as Other, which can't be chosen", () => {
    const option = pieOption(
      pieDefaults('orders'),
      dataOf(
        [
          ['Status', 'dimension', text],
          ['Total', 'measure', decimal],
        ],
        [['open', '262.25']],
        { other: '99.50' },
      ),
      contextOf(),
    );
    const items = (option.series as unknown as Series[])[0].data;
    expect(items.map((i) => [i.name, i.value, i.key])).toEqual([
      ['open', 262.25, ['open']],
      ['Other', 99.5, null],
    ]);
  });

  it('draws lines over every period, as zero or gaps', () => {
    const months = dataOf(
      [
        ['Month', 'dimension', date],
        ['Orders', 'measure', int64],
      ],
      [
        ['2026-01-01', '2'],
        ['2026-03-01', '1'],
      ],
    );
    const config: LineConfig = {
      ...lineDefaults('orders'),
      dimension: { field: { path: [], column: 'order_date' }, bucket: 'month', label: 'Month' },
    };
    const gaps = lineOption(config, months, contextOf());
    expect((gaps.xAxis as { data: string[] }).data).toEqual(['Jan 2026', 'Feb 2026', 'Mar 2026']);
    expect((gaps.series as unknown as Series[])[0].data.map((d) => d.value)).toEqual([2, null, 1]);
    const zeros = lineOption({ ...config, gaps: 'zero' }, months, contextOf());
    expect((zeros.series as unknown as Series[])[0].data.map((d) => d.value)).toEqual([2, 0, 1]);
    const connected = lineOption({ ...config, gaps: 'connect' }, months, contextOf());
    expect((connected.xAxis as { data: string[] }).data).toEqual(['Jan 2026', 'Mar 2026']);
  });

  it('puts a legend at a side below a narrow chart, and its pie above it', () => {
    const config = pieDefaults('orders');
    const legendOf = (option: ReturnType<typeof pieOption>) =>
      option.legend as { orient: string; right?: number; bottom?: number };
    const centerOf = (option: ReturnType<typeof pieOption>) =>
      (option.series as unknown as { center: string[] }[])[0].center;
    const wide = pieOption(config, statuses, contextOf());
    expect(legendOf(wide)).toMatchObject({ orient: 'vertical', right: 0 });
    expect(centerOf(wide)).toEqual(['40%', '50%']);
    const narrow = pieOption(config, statuses, contextOf({ narrow: true }));
    expect(legendOf(narrow)).toMatchObject({ orient: 'horizontal', bottom: 0 });
    expect(centerOf(narrow)).toEqual(['50%', '46%']);
    const top = pieOption({ ...config, legend: 'top' }, statuses, contextOf({ narrow: true }));
    expect(legendOf(top)).toMatchObject({ orient: 'horizontal', top: 0 });
  });

  it("doesn't animate for those who ask for less motion", () => {
    expect(
      barOption(barDefaults('o'), statuses, contextOf({ reducedMotion: true })).animation,
    ).toBe(false);
  });
});

describe('chart palette', () => {
  /** WCAG's relative luminance and contrast. */
  function luminance(hex: string): number {
    const [r, g, b] = [1, 3, 5]
      .map((i) => parseInt(hex.slice(i, i + 2), 16) / 255)
      .map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  }
  const contrast = (a: string, b: string) => {
    const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p);
    return (x + 0.05) / (y + 0.05);
  };

  it('stands out from the dark surface everywhere, and the light one but where labels and a table say', () => {
    const dark = fallbackTheme(true);
    expect(dark.series.every((color) => contrast(color, '#121316') >= 3)).toBe(true);
    const light = fallbackTheme(false);
    const low = light.series.filter((color) => contrast(color, '#faf9fd') < 3);
    expect(low).toEqual(['#1baf7a', '#eda100', '#e87ba4']);
    expect(new Set(light.series).size).toBe(8);
  });
});
