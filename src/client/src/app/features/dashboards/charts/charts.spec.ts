import type { BarConfig, LineConfig, PieConfig, WidgetData } from '../model/definition';
import { barDefaults, lineDefaults, pieDefaults } from '../model/widget-defaults';
import { actionOf } from './chart-events';
import {
  ColorMemory,
  type ChartContext,
  barOption,
  lineOption,
  otherName,
  otherSeries,
  pieOption,
} from './chart-options';
import { fallbackTheme } from './chart-theme';
import { contrast } from './color-checks';
import {
  booleanText,
  categoryText,
  chartNumber,
  durationText,
  escapeHtml,
  formatNumber,
  periodsBetween,
} from './chart-values';

const text = { kind: 'string', nullable: true, text: 'string?' } as const;
const int64 = { kind: 'int64', nullable: false, text: 'int64' } as const;
const decimal = { kind: 'decimal', nullable: false, text: 'decimal(10,2)' } as const;
const date = { kind: 'date', nullable: false, text: 'date' } as const;
const boolean = { kind: 'boolean', nullable: true, text: 'boolean?' } as const;

function dataOf(
  columns: [
    string,
    'dimension' | 'series' | 'measure',
    typeof text | typeof int64 | typeof decimal | typeof date | typeof boolean,
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
    moreSeries: false,
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
  itemStyle?: Record<string, unknown>;
  lineStyle?: Record<string, unknown>;
  data: {
    value: number | null;
    key: unknown[] | null;
    raw: unknown;
    other?: true;
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

  it('shows shares of 1 as percentages: exactly, to one place unless decimals are given', () => {
    const percent = {
      decimals: null,
      prefix: null,
      suffix: null,
      compact: false,
      style: 'percent',
    } as const;
    expect(formatNumber(0.2567, percent, 'en-US')).toBe('25.7%');
    expect(formatNumber(0.07, percent, 'en-US')).toBe('7%');
    expect(formatNumber('0.125', { ...percent, decimals: 2 }, 'en-US')).toBe('12.50%');
    expect(formatNumber('0.0012345', { ...percent, decimals: 4 }, 'en-US')).toBe('0.1235%');
    expect(formatNumber('-1.5', percent, 'en-US')).toBe('-150%');
    expect(formatNumber(1, { ...percent, prefix: '≈ ' }, 'en-US')).toBe('≈ 100%');
    expect(formatNumber(0.5, percent, 'de-DE')).toBe('50%');
    expect(formatNumber(0.505, percent, 'de-DE')).toBe('50,5%');
  });

  it('shows durations in their two largest parts', () => {
    const ms = (n: number) => durationText(n, 'milliseconds', null, 'en-US');
    expect(ms(473_000)).toBe('7m 53s');
    expect(ms(135_000)).toBe('2m 15s');
    expect(ms(120_000)).toBe('2m');
    expect(ms(45_300)).toBe('45.3s');
    expect(ms(59_960)).toBe('1m');
    expect(ms(850)).toBe('850ms');
    expect(ms(0)).toBe('0ms');
    expect(ms(3_599_600)).toBe('1h');
    expect(ms(3_900_000)).toBe('1h 5m');
    expect(ms(-135_000)).toBe('-2m 15s');
    expect(durationText(100 * 3600 + 1200, 'seconds', null, 'en-US')).toBe('4d 4h');
    expect(durationText(1.5, 'minutes', null, 'en-US')).toBe('1m 30s');
    expect(durationText(2.25, 'seconds', 2, 'en-US')).toBe('2.25s');
    expect(durationText(0.25, 'seconds', null, 'en-US')).toBe('250ms');
    expect(durationText('x', 'seconds', null, 'en-US')).toBe('x');
    expect(
      formatNumber(
        '473000.5',
        {
          decimals: 0,
          prefix: null,
          suffix: ' avg',
          compact: true,
          style: 'duration',
          unit: 'milliseconds',
        },
        'en-US',
      ),
    ).toBe('7m 53s avg');
  });

  it('shows true and false by their labels, or yes and no', () => {
    expect(booleanText(true, null)).toBe('Yes');
    expect(booleanText(false, undefined)).toBe('No');
    expect(booleanText(true, { true: 'Bounced', false: '' })).toBe('Bounced');
    expect(booleanText(false, { true: 'Bounced', false: ' ' })).toBe('No');
    expect(categoryText(false, boolean, null, 'en-US', { true: 'Bounced', false: 'Went on' })).toBe(
      'Went on',
    );
    expect(categoryText(true, boolean, null, 'en-US')).toBe('Yes');
    expect(categoryText(null, boolean, null, 'en-US', { true: 'Bounced', false: 'Went on' })).toBe(
      '(no value)',
    );
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

  it('names true and false by their labels, and shows rates as percentages', () => {
    const data = dataOf(
      [
        ['Traffic', 'dimension', text],
        ['Bounced', 'series', boolean],
        ['Sessions', 'measure', int64],
      ],
      [
        ['direct', true, '3'],
        ['direct', false, '1'],
        ['search', null, '2'],
      ],
    );
    const config: BarConfig = {
      ...barDefaults('sessions'),
      series: {
        field: { path: [], column: 'bounced' },
        bucket: null,
        label: 'Bounced',
        booleans: { true: 'Bounced', false: 'Went on' },
      },
      stack: 'percent',
    };
    const option = barOption(config, data, contextOf());
    expect((option.series as unknown as Series[]).map((s) => s.name)).toEqual([
      'Bounced',
      'Went on',
      '(no value)',
    ]);
    const axis = (o: typeof option) =>
      (o.yAxis as { axisLabel: { formatter: (n: number) => string } }).axisLabel.formatter;
    expect(axis(option)(40)).toBe('40%');

    const rates = dataOf(
      [
        ['Traffic', 'dimension', text],
        ['Bounce rate', 'measure', int64],
      ],
      [
        ['direct', 0.4567],
        ['search', 0.25],
      ],
    );
    rates.columns[1].format = {
      decimals: 1,
      prefix: null,
      suffix: null,
      compact: false,
      style: 'percent',
    };
    const rated = barOption(barDefaults('sessions'), rates, contextOf());
    expect(axis(rated)(0.5)).toBe('50%');
    const tip = (rated.tooltip as { formatter: (p: unknown) => string }).formatter({
      name: 'direct',
      seriesName: 'Bounce rate',
      marker: '',
      data: (rated.series as unknown as Series[])[0].data[0],
    });
    // In the locale (en-ZA).
    expect(tip).toBe('direct<br>45,7%');
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
    // Beside an Other (grey), the colours again from the first: none looks like it.
    const again = new ColorMemory(true);
    Array.from({ length: 8 }, (_, i) => again.slot(String(i)));
    expect([again.color('ninth', theme), again.color('tenth', theme)]).toEqual([
      theme.series[0],
      theme.series[1],
    ]);
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

  it('marks the rest of a pie as Other, stepped into, and says where it is', () => {
    const config: PieConfig = {
      ...pieDefaults('orders'),
      dimension: { field: { path: [], column: 'status' }, bucket: null, label: 'Status' },
      limit: 1,
      other: true,
    };
    const data = dataOf(
      [
        ['Status', 'dimension', text],
        ['Orders', 'measure', int64],
      ],
      [['cancelled', '1']],
      { other: '1', offset: 1, next: 2, truncated: true },
    );
    const option = pieOption(config, data, contextOf());
    const items = (option.series as unknown as Series[])[0].data;
    expect(items.map((i) => [i.name, i.key, i.other])).toEqual([
      ['cancelled', ['cancelled'], undefined],
      ['Other', null, true],
    ]);
    const tip = (option.tooltip as { formatter: (p: unknown) => string }).formatter({
      name: 'Other',
      percent: 50,
      marker: '',
      data: items[1],
    });
    expect(tip).toBe('Other<br>1 (50,0%)<br>Click to see the next slice');
    expect((option.aria as { label: { description: string } }).label.description).toBe(
      'Pie chart of Rows by Status, 2 categories; highest cancelled 1; slice 2; the rest as Other',
    );
    // At the last step, nothing further: no hint.
    const last = pieOption(config, { ...data, next: null }, contextOf());
    const end = (last.tooltip as { formatter: (p: unknown) => string }).formatter({
      name: 'Other',
      percent: 50,
      marker: '',
      data: (last.series as unknown as Series[])[0].data[1],
    });
    expect(end).toBe('Other<br>1 (50,0%)');
  });

  it('draws the largest series first, and the rest as Other: grey, in its stack, stepped into', () => {
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
      {
        categories: ['open', 'shipped'],
        series: ['2', '1'],
        moreSeries: true,
        otherRows: [
          ['open', '2'],
          ['shipped', '4'],
        ],
        next: 2,
      },
    );
    const config: BarConfig = {
      ...barDefaults('orders'),
      series: { field: { path: [], column: 'customer_id' }, bucket: null, label: 'Customer' },
      seriesLimit: 2,
      seriesOther: true,
      stack: 'percent',
    };
    const theme = fallbackTheme(false);
    const option = barOption(config, data, contextOf());
    const series = option.series as unknown as Series[];
    expect(series.map((s) => s.name)).toEqual(['2', '1', 'Other']);
    // Open: 1 and 3 shown, 2 the rest's (of 6); shipped: 1 shown, 4 the rest's (of 5).
    expect(series.map((s) => s.data.map((d) => Math.round(d.value ?? 0)))).toEqual([
      [50, 0],
      [17, 20],
      [33, 80],
    ]);
    expect(series[2].data.map((d) => [d.key, d.other])).toEqual([
      [null, true],
      [null, true],
    ]);
    expect(series[2].itemStyle?.['color']).toBe(theme.neutral);
    const tip = (option.tooltip as { formatter: (p: unknown) => string }).formatter({
      name: 'open',
      seriesName: 'Other',
      marker: '',
      data: series[2].data[0],
    });
    expect(tip).toBe('open<br>Other: 2<br>Click to see the next 2 series');
    expect((option.aria as { label: { description: string } }).label.description).toContain(
      'the rest as Other',
    );
    // Faded while slices are chosen, as it is never one of them.
    const chosen = barOption(
      config,
      data,
      contextOf({ selection: { mode: 'include', keys: [['open', '2']] } }),
    );
    expect((chosen.series as unknown as Series[])[2].data[0].itemStyle).toEqual({ opacity: 0.3 });
  });

  it("draws a line's x values as the server gives them: some may be its rest's alone", () => {
    const data = dataOf(
      [
        ['Day', 'dimension', date],
        ['Status', 'series', text],
        ['Orders', 'measure', int64],
      ],
      [
        ['2026-01-05', 'open', '1'],
        ['2026-02-01', 'open', '1'],
      ],
      {
        categories: ['2026-01-05', '2026-01-09', '2026-02-01', '2026-02-14'],
        series: ['open'],
        moreSeries: true,
        otherRows: [
          ['2026-01-09', '1'],
          ['2026-02-14', '1'],
        ],
        next: 1,
      },
    );
    const config: LineConfig = {
      ...lineDefaults('orders'),
      dimension: { field: { path: [], column: 'order_date' }, bucket: 'day', label: 'Day' },
      series: { field: { path: [], column: 'status' }, bucket: null, label: 'Status' },
      seriesLimit: 1,
      seriesOther: true,
      gaps: 'connect',
    };
    const option = lineOption(config, data, contextOf());
    expect((option.xAxis as { data: string[] }).data).toHaveLength(4);
    const series = option.series as unknown as Series[];
    expect(series.map((s) => [s.name, s.data.map((d) => d.value)])).toEqual([
      ['open', [1, null, 1, null]],
      ['Other', [null, 1, null, 1]],
    ]);
    expect(series[1].lineStyle?.['color']).toBe(fallbackTheme(false).neutral);
    // Its line itself takes clicks, not only its points: the chart knows it by its id.
    expect(series[1]).toMatchObject({ id: otherSeries, triggerEvent: true });
    expect(series[0]).not.toHaveProperty('triggerEvent');
  });

  it('tells Other apart from a value of that name', () => {
    expect(otherName(['open', 'shipped'])).toBe('Other');
    expect(otherName(['open', 'Other'])).toBe('Other (the rest)');
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
  it('stands out from the dark surface everywhere, and the light one but where labels and a table say', () => {
    const dark = fallbackTheme(true);
    expect(dark.series.every((color) => contrast(color, '#121316') >= 3)).toBe(true);
    const light = fallbackTheme(false);
    const low = light.series.filter((color) => contrast(color, '#faf9fd') < 3);
    expect(low).toEqual(['#1baf7a', '#eda100', '#e87ba4']);
    expect(new Set(light.series).size).toBe(8);
  });
});
