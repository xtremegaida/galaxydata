import fc from 'fast-check';
import cases from '../../../../../../../tests/fixtures/palette-labels.json';
import type {
  BarConfig,
  ChartConfig,
  LineConfig,
  PieConfig,
  WidgetData,
} from '../model/definition';
import { barDefaults, lineDefaults, pieDefaults } from '../model/widget-defaults';
import { barOption, chartOption, pieOption } from './chart-options';
import { fallbackTheme, galaxyColors } from './chart-theme';
import {
  ColorMemory,
  type ColoredEntity,
  type LabelMatching,
  PaletteMemory,
  type PaletteView,
  type SeriesColors,
  WidgetColors,
  cellEntity,
  coloredEntities,
  fnv1a64,
  hexOf,
  jumpHash,
  labelText,
  normalizeLabel,
} from './series-colors';

const light = fallbackTheme(false);
const dark = fallbackTheme(true);
const text = { kind: 'string', nullable: true, text: 'string?' } as const;
const int64 = { kind: 'int64', nullable: false, text: 'int64' } as const;

function ignoring(ignore: readonly string[]): LabelMatching {
  return {
    ignoreCase: ignore.includes('case'),
    ignoreWhitespace: ignore.includes('whitespace'),
    ignoreBrackets: ignore.includes('brackets'),
    ignoreAccents: ignore.includes('accents'),
  };
}

const exact = ignoring([]);

/** A palette of `n` colours (#000001, #000002, …), by label unless said. */
function palette(n: number, extra: Partial<PaletteView> = {}): PaletteView {
  return {
    id: 1,
    basis: 'one',
    colors: Array.from({ length: n }, (_, i) => ({
      light: '#' + (i + 1).toString(16).padStart(6, '0'),
      dark: null,
    })),
    assign: 'label',
    distinct: true,
    whenOut: 'repeat',
    matching: ignoring(['case']),
    overrides: [],
    ...extra,
  };
}

const entity = (label: string | null): ColoredEntity => ({
  entity: JSON.stringify([label]),
  label,
  shown: label ?? '(no value)',
});

function colorsOf(memory: PaletteMemory, labels: (string | null)[], theme = light): string[] {
  return memory.assign(labels.map(entity), theme, null).map((c) => c.color);
}

function dataOf(
  columns: [string, 'dimension' | 'series' | 'measure'][],
  rows: unknown[][],
): WidgetData {
  const indexes: Record<string, number> = {};
  return {
    columns: columns.map(([label, role]) => {
      const index = indexes[role] ?? 0;
      indexes[role] = index + 1;
      return {
        name: role[0] + index,
        role,
        index,
        label,
        type: role === 'measure' ? int64 : text,
        format: null,
      };
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
  };
}

describe('how palettes match labels', () => {
  it('normalises labels as the server does (the shared fixture)', () => {
    const wrong = cases.normalize
      .map((c) => ({ ...c, got: normalizeLabel(c.text, ignoring(c.ignore)) }))
      .filter((c) => c.got !== c.expected)
      .map(
        (c) => `${JSON.stringify(c.text)} ${JSON.stringify(c.ignore)}: ${JSON.stringify(c.got)}`,
      );
    expect(wrong).toEqual([]);
  });

  it("matches values by their text as answers carry them, never as they're shown", () => {
    const wrong = cases.values
      .map((c) => ({ ...c, got: labelText(c.value) }))
      .filter((c) => c.got !== c.text)
      .map((c) => `${JSON.stringify(c.value)}: ${c.got}`);
    expect(wrong).toEqual([]);
  });
});

describe('hashing labels to colours', () => {
  it("is FNV-1a's 64-bit hash of the UTF-8", () => {
    expect(fnv1a64('')).toBe(0xcbf29ce484222325n);
    expect(fnv1a64('a')).toBe(0xaf63dc4c8601ec8cn);
    expect(fnv1a64('foobar')).toBe(0x85944171f73967e8n);
    expect(fnv1a64('é')).toBe(fnv1a64('é'));
  });

  it('moves only the labels a colour added takes (a jump consistent hash)', () => {
    fc.assert(
      fc.property(
        fc.bigInt({ min: 0n, max: (1n << 64n) - 1n }),
        fc.integer({ min: 1, max: 40 }),
        (key, n) => {
          const before = jumpHash(key, n);
          const after = jumpHash(key, n + 1);
          expect(before).toBeGreaterThanOrEqual(0);
          expect(before).toBeLessThan(n);
          expect(after === before || after === n).toBe(true);
        },
      ),
      { numRuns: 400 },
    );
    // And spreads labels about evenly.
    const counts = new Array<number>(8).fill(0);
    for (let i = 0; i < 2000; i++) {
      counts[jumpHash(fnv1a64(`label ${i}`), 8)]++;
    }
    expect(Math.min(...counts)).toBeGreaterThan(180);
    expect(Math.max(...counts)).toBeLessThan(320);
  });
});

describe("a widget's palette colours", () => {
  it('gives overrides first, which take no slot, then colours in order', () => {
    const memory = new PaletteMemory(
      palette(3, {
        assign: 'order',
        overrides: [{ label: 'B', color: { light: '#ff0000', dark: '#880000' } }],
      }),
    );
    const colored = memory.assign(['a', 'b', 'c', null].map(entity), light, null);
    expect(colored.map((c) => [c.color, c.source])).toEqual([
      ['#000001', 'order'],
      ['#ff0000', 'override'],
      ['#000002', 'order'],
      ['#000003', 'order'],
    ]);
    expect(memory.assign([entity('b')], dark, null)[0].color).toBe('#880000');
  });

  it('repeats colours or greys what is past them, as the palette says', () => {
    const order = { assign: 'order' as const, distinct: false };
    expect(colorsOf(new PaletteMemory(palette(2, order)), ['a', 'b', 'c'])).toEqual([
      '#000001',
      '#000002',
      '#000001',
    ]);
    expect(
      colorsOf(new PaletteMemory(palette(2, { ...order, whenOut: 'neutral' })), ['a', 'b', 'c']),
    ).toEqual(['#000001', '#000002', light.neutral]);
    // By label, distinct: two colours for three labels; the third repeats one, or is grey.
    const three = ['north', 'south', 'east'];
    expect(new Set(colorsOf(new PaletteMemory(palette(2)), three)).size).toBe(2);
    expect(colorsOf(new PaletteMemory(palette(2, { whenOut: 'neutral' })), three)).toContain(
      light.neutral,
    );
  });

  it('gives a label its colour in every chart, whatever else is in it', () => {
    const labels = Array.from({ length: 6 }, (_, i) => `status ${i}`);
    const wide = palette(40, { distinct: false });
    const all = colorsOf(new PaletteMemory(wide), labels);
    const some = colorsOf(new PaletteMemory(wide), [labels[4], labels[1]]);
    expect(some).toEqual([all[4], all[1]]);
    // Matched as the palette says.
    expect(colorsOf(new PaletteMemory(wide), ['STATUS 4'])).toEqual([all[4]]);
    expect(
      colorsOf(new PaletteMemory(palette(40, { distinct: false, matching: exact })), ['STATUS 4']),
    ).not.toEqual([all[4]]);
  });

  it('keeps labels apart within a chart (distinct), whatever order its rows come in', () => {
    fc.assert(
      fc.property(
        fc.uniqueArray(fc.string({ minLength: 1, maxLength: 6 }), { minLength: 1, maxLength: 8 }),
        fc.integer({ min: 1, max: 10 }),
        (labels, n) => {
          const keys = new Set(labels.map((l) => normalizeLabel(l, ignoring(['case']))));
          const shuffled = [...labels].reverse();
          const one = new PaletteMemory(palette(n)).assign(labels.map(entity), light, null);
          const other = new PaletteMemory(palette(n)).assign(shuffled.map(entity), light, null);
          const byLabel = (list: { label: string | null; color: string }[]) =>
            Object.fromEntries(list.map((c) => [c.label, c.color]));
          expect(byLabel(other)).toEqual(byLabel(one));
          // As many colours as there are labels (as matched), or all the palette's.
          expect(new Set(one.map((c) => c.color)).size).toBe(Math.min(keys.size, n));
        },
      ),
      { numRuns: 300 },
    );
    // Labels matched the same share their colour.
    const same = colorsOf(new PaletteMemory(palette(8)), ['Open', 'open', 'shipped']);
    expect(same[0]).toBe(same[1]);
    expect(same[2]).not.toBe(same[0]);
  });

  it('keeps what it has coloured while it is shown: a filter repaints nothing', () => {
    fc.assert(
      fc.property(
        fc.uniqueArray(fc.string({ minLength: 1, maxLength: 5 }), { minLength: 2, maxLength: 10 }),
        fc.boolean(),
        (labels, byOrder) => {
          const memory = new PaletteMemory(
            palette(4, { assign: byOrder ? 'order' : 'label', matching: exact }),
          );
          const first = colorsOf(memory, labels);
          const kept = labels.filter((_, i) => i % 2 === 1);
          expect(colorsOf(memory, kept)).toEqual(first.filter((_, i) => i % 2 === 1));
          expect(colorsOf(memory, labels)).toEqual(first);
        },
      ),
      { numRuns: 200 },
    );
  });

  it('says where each colour came from: moved, for one distinct moved off a colour taken', () => {
    // Two labels the hash gives one colour of two.
    const n = 2;
    const [a, b] = Array.from({ length: 50 }, (_, i) => `l${i}`).filter(
      (l, _, all) => jumpHash(fnv1a64(l), n) === jumpHash(fnv1a64(all[0]), n),
    );
    const colored = new PaletteMemory(palette(n, { matching: exact })).assign(
      [entity(b), entity(a)],
      light,
      null,
    );
    expect(colored.map((c) => c.source).sort()).toEqual(['label', 'moved']);
    expect(colored[0].color).not.toBe(colored[1].color);
  });

  it("takes an embedded chart's overrides from its answer, by the labels there", () => {
    const memory = new PaletteMemory(palette(3, { assign: 'order' }));
    const colored = memory.assign([entity('open'), entity('shipped'), entity(null)], light, [
      { label: 'open', light: '#123456', dark: null },
      { label: null, light: '#654321', dark: null },
    ]);
    expect(colored.map((c) => [c.color, c.source])).toEqual([
      ['#123456', 'override'],
      ['#000001', 'order'],
      ['#654321', 'override'],
    ]);
  });
});

describe('what a chart colours', () => {
  const pie: PieConfig = {
    ...pieDefaults('orders'),
    dimension: { field: { path: [], column: 'status' }, bucket: null, label: 'Status' },
  };
  const bar: BarConfig = {
    ...barDefaults('orders'),
    dimension: { field: { path: [], column: 'status' }, bucket: null, label: 'Status' },
  };
  const line: LineConfig = {
    ...lineDefaults('orders'),
    dimension: { field: { path: [], column: 'month' }, bucket: null, label: 'Month' },
  };
  const rows = dataOf(
    [
      ['Status', 'dimension'],
      ['Orders', 'measure'],
      ['Revenue', 'measure'],
    ],
    [
      ['open', '2', '10'],
      [null, '1', '4'],
    ],
  );
  const bySeries = dataOf(
    [
      ['Month', 'dimension'],
      ['Region', 'series'],
      ['Orders', 'measure'],
    ],
    [
      ['2026-01-01', 'EMEA', '2'],
      ['2026-01-01', 'APAC', '1'],
      ['2026-02-01', 'EMEA', '3'],
    ],
  );
  const oneMeasure = dataOf(
    [
      ['Status', 'dimension'],
      ['Orders', 'measure'],
    ],
    [
      ['open', '2'],
      ['shipped', '1'],
    ],
  );

  /** The entities a chart's options ask the colours of, in order. */
  function asked(config: ChartConfig, data: WidgetData): string[] {
    const entities: string[] = [];
    const colors: SeriesColors = {
      color: (e) => {
        entities.push(e);
        return '#000000';
      },
    };
    chartOption(config, data, {
      theme: light,
      locale: 'en-ZA',
      selection: null,
      reducedMotion: true,
      colors,
      narrow: false,
    });
    return entities;
  }

  it('lists what its options paint, in the order they meet them', () => {
    const configs: [ChartConfig, WidgetData][] = [
      [pie, rows],
      [bar, rows],
      [{ ...bar, colorBy: 'category' }, oneMeasure],
      [
        {
          ...bar,
          series: { field: { path: [], column: 'region' }, bucket: null, label: 'Region' },
        },
        bySeries,
      ],
      [line, rows],
      [
        {
          ...line,
          series: { field: { path: [], column: 'region' }, bucket: null, label: 'Region' },
        },
        bySeries,
      ],
    ];
    for (const [config, data] of configs) {
      const listed = coloredEntities(config, data, 'en-ZA').map((e) => e.entity);
      const painted = [...new Set(asked(config, data))];
      expect(listed, config.kind).toEqual(painted.filter((e) => listed.includes(e)));
      expect(painted.every((e) => listed.includes(e) || e.startsWith('measure:'))).toBe(true);
    }
    expect(coloredEntities(pie, rows, 'en-ZA')).toEqual([
      { entity: '["open"]', label: 'open', shown: 'open' },
      { entity: '[null]', label: null, shown: '(no value)' },
    ]);
    expect(coloredEntities(bar, rows, 'en-ZA').map((e) => e.label)).toEqual(['Orders', 'Revenue']);
    // Categories only with one measure.
    expect(
      coloredEntities({ ...bar, colorBy: 'category' }, rows, 'en-ZA').map((e) => e.label),
    ).toEqual(['Orders', 'Revenue']);
  });

  it('paints bars by their categories when asked, each its own colour', () => {
    const colors = new WidgetColors().paint(
      { ...bar, colorBy: 'category' },
      oneMeasure,
      palette(8, { assign: 'order' }),
      light,
      'en-ZA',
    );
    const option = barOption({ ...bar, colorBy: 'category' }, oneMeasure, {
      theme: light,
      locale: 'en-ZA',
      selection: null,
      reducedMotion: true,
      colors,
      narrow: false,
    });
    const series = option.series as { data: { itemStyle: { color: string } }[] }[];
    expect(series[0].data.map((d) => d.itemStyle.color)).toEqual(['#000001', '#000002']);
    expect(cellEntity({ ...bar, colorBy: 'category' }, oneMeasure, oneMeasure.rows[1], 0)).toBe(
      'category:["shipped"]',
    );
    expect(cellEntity(bar, oneMeasure, null, 1)).toBe('measure:Orders');
    expect(cellEntity(bar, oneMeasure, oneMeasure.rows[0], 1)).toBeNull();
  });

  it("says each colour's pair, light and dark, and writes a page's colours as #rrggbb", () => {
    const painted = new WidgetColors().paint(pie, rows, null, dark, 'en-ZA');
    expect(painted.entries[0]).toMatchObject({ color: dark.series[0], pair: galaxyColors[0] });
    const own = new WidgetColors().paint(
      pie,
      rows,
      palette(2, { overrides: [{ label: 'open', color: { light: '#ff0000', dark: null } }] }),
      dark,
      'en-ZA',
    );
    expect(own.entries.map((e) => e.pair)).toEqual([
      { light: '#ff0000', dark: null },
      { light: '#000001', dark: null },
    ]);
    expect(hexOf('rgb(57, 135, 229)')).toBe('#3987e5');
    expect(hexOf('rgba(1, 2, 3, 0.5)')).toBe('#010203');
    expect(hexOf('#ABCDEF')).toBe('#abcdef');
    expect(hexOf('red')).toBe('#000000');
  });

  it('is drawn as ever without a palette, and anew when what it colours changes', () => {
    const painter = new WidgetColors();
    const ever = new ColorMemory();
    const first = painter.paint(pie, rows, null, light, 'en-ZA');
    expect(first.entries.map((e) => [e.color, e.source])).toEqual([
      [ever.color('["open"]', light), 'default'],
      [ever.color('[null]', light), 'default'],
    ]);
    const options = pieOption(pie, rows, {
      theme: light,
      locale: 'en-ZA',
      selection: null,
      reducedMotion: true,
      colors: first,
      narrow: false,
    });
    expect(
      (options.series as { data: { itemStyle: { color: string } }[] }[])[0].data.map(
        (d) => d.itemStyle.color,
      ),
    ).toEqual([light.series[0], light.series[1]]);
    // Another field: its values start from the first colour again.
    const other = { ...pie, dimension: { ...pie.dimension, field: { path: [], column: 'city' } } };
    const shipped = dataOf(
      [
        ['City', 'dimension'],
        ['Orders', 'measure'],
      ],
      [['Durban', '1']],
    );
    expect(painter.paint(other, shipped, null, light, 'en-ZA').entries[0].color).toBe(
      light.series[0],
    );
    // A palette's change (its hash) too.
    const one = painter.paint(pie, rows, palette(3, { assign: 'order' }), light, 'en-ZA');
    const two = painter.paint(
      pie,
      rows,
      palette(3, { assign: 'order', basis: 'two', colors: [{ light: '#abcdef', dark: null }] }),
      light,
      'en-ZA',
    );
    expect(one.entries[0].color).toBe('#000001');
    expect(two.entries[0].color).toBe('#abcdef');
  });
});
