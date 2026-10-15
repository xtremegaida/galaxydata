import { rowsOf } from '../../../../testing/dashboards';
import type { KpiConfig, WidgetColumn } from './definition';
import {
  changeText,
  kpiChange,
  kpiFormat,
  periodText,
  pointsData,
  sparkPoints,
  sparkShape,
} from './kpi';
import { comparedWith, kpiDefaults, overTime, pieDefaults } from './widget-defaults';

/** Text with its spaces plain (Intl puts thin ones around a range's dash). */
const plain = (text: string) => text.replace(/\s+/g, ' ');

describe('a new KPI', () => {
  it("is drawn along its source's time column by day, and compared with the first period filter", () => {
    const time = { column: 'first_seen', label: 'First seen' };
    const kpi = overTime(kpiDefaults('sessions'), time) as KpiConfig;
    expect([kpi.emits, kpi.sparkline]).toEqual([
      false,
      { field: { path: [], column: 'first_seen' }, bucket: 'day', label: 'Day' },
    ]);
    expect(overTime(kpiDefaults('sessions'), null)).toEqual(kpiDefaults('sessions'));
    const filter = {
      id: 'site',
      label: 'Site',
      field: { source: 'sessions', path: [], column: 'site' },
      kind: 'values' as const,
      value: null,
      visible: true,
      editable: true,
      multiple: true,
      except: [],
    };
    const period = { ...filter, id: 'period', kind: 'relative' as const, multiple: false };
    expect((comparedWith(kpi, [filter, period]) as KpiConfig).compare).toEqual({
      filter: 'period',
      good: 'up',
    });
    expect((comparedWith(kpi, [filter]) as KpiConfig).compare).toBeNull();
    const made = { ...period, field: { ...period.field, column: '' } };
    expect((comparedWith(kpi, [made]) as KpiConfig).compare).toBeNull();
    expect(comparedWith(pieDefaults('sessions'), [period])).toEqual(pieDefaults('sessions'));
  });
});

describe("a KPI's change against the period before", () => {
  it('is good or bad as the way it goes is, and neither colours nothing', () => {
    expect(kpiChange('120', '100', 'up')).toEqual({
      delta: 20,
      ratio: 0.2,
      direction: 'up',
      tone: 'good',
    });
    expect(kpiChange('120', '100', 'down')?.tone).toBe('bad');
    expect(kpiChange('80', '100', 'down')).toEqual({
      delta: -20,
      ratio: -0.2,
      direction: 'down',
      tone: 'good',
    });
    expect(kpiChange('80', '100', 'neither')?.tone).toBe('neutral');
    expect(kpiChange('100', '100', 'up')).toEqual({
      delta: 0,
      ratio: 0,
      direction: 'flat',
      tone: 'neutral',
    });
  });

  it('keeps to the decimals of its numbers, has no share of zero, and is none without both', () => {
    expect(kpiChange('12.25', '349.50', 'up')?.delta).toBe(-337.25);
    expect(kpiChange(0.3, 0.1, 'up')?.delta).toBe(0.2);
    expect(kpiChange('5', '0', 'up')).toEqual({
      delta: 5,
      ratio: null,
      direction: 'up',
      tone: 'good',
    });
    expect(kpiChange('5', null, 'up')).toBeNull();
    expect(kpiChange(null, '5', 'up')).toBeNull();
  });

  it('is shown with its % and its amount in the number format, and said in words', () => {
    const compact = { decimals: null, prefix: null, suffix: null, compact: true };
    const up = changeText(
      kpiChange('748000', '666800', 'up')!,
      compact,
      'Aug 15 – Sep 13',
      'en-US',
    );
    expect(up).toEqual({
      percent: '+12.2%',
      amount: '+81.2K',
      icon: 'trending_up',
      words: 'Up 12.2% (81.2K) on Aug 15 – Sep 13, for the better',
    });
    const money = { decimals: 2, prefix: 'R ', suffix: null, compact: false };
    const down = changeText(kpiChange('12.25', '349.50', 'up')!, money, 'Jan 1 – 30', 'en-US');
    expect([down.percent, down.amount, down.icon]).toEqual([
      '-96.5%',
      '−R 337.25',
      'trending_down',
    ]);
    expect(down.words).toBe('Down 96.5% (R 337.25) on Jan 1 – 30, for the worse');
    const fromNothing = changeText(kpiChange('3', '0', 'neither')!, null, 'Jan 1', 'en-US');
    expect([fromNothing.percent, fromNothing.amount, fromNothing.words]).toEqual([
      null,
      '+3',
      'Up 3 on Jan 1',
    ]);
    const same = changeText(kpiChange('3', '3', 'up')!, null, 'Jan 1', 'en-US');
    expect([same.amount, same.icon, same.words]).toEqual([
      '0',
      'trending_flat',
      'The same as in Jan 1',
    ]);
  });
});

describe("a KPI's period before", () => {
  it('is read as its first and last days, with the year when it is not this one', () => {
    const today = new Date('2026-10-14T12:00:00Z');
    expect(plain(periodText('2026-08-15', '2026-09-13', 'en-US', today))).toBe('Aug 15 – Sep 13');
    expect(plain(periodText('2025-12-15', '2026-01-13', 'en-US', today))).toBe(
      'Dec 15, 2025 – Jan 13, 2026',
    );
    expect(periodText('2026-10-13', '2026-10-13', 'en-US', today)).toBe('Oct 13');
  });
});

describe("a KPI's number format", () => {
  const column = (type: WidgetColumn['type'], format: WidgetColumn['format'] = null) =>
    ({ name: 'm0', role: 'measure', index: 0, label: 'Value', type, format }) as WidgetColumn;

  it("is its measure's, else a decimal's scale, else two decimals at most of a float", () => {
    const own = { decimals: 0, prefix: null, suffix: ' ms', compact: true };
    expect(kpiFormat(column({ kind: 'double', nullable: true, text: 'double?' }, own))).toBe(own);
    expect(
      kpiFormat(column({ kind: 'decimal', nullable: true, text: 'decimal(10,2)', scale: 2 })),
    ).toEqual({ decimals: 2, prefix: null, suffix: null, compact: false });
    expect(kpiFormat(column({ kind: 'double', nullable: true, text: 'double?' }))?.decimals).toBe(
      2,
    );
    expect(kpiFormat(column({ kind: 'int64', nullable: false, text: 'int64' }))).toBeNull();
    expect(kpiFormat(undefined)).toBeNull();
  });
});

describe("a KPI's sparkline", () => {
  it('has every period between its first and last: none counted is zero, and a gap for averages', () => {
    const points = [
      ['2026-02-01', '3'],
      ['2026-02-03', '5'],
    ];
    expect(sparkPoints(points, 'day', 'count')).toEqual([
      { day: '2026-02-01', value: 3 },
      { day: '2026-02-02', value: 0 },
      { day: '2026-02-03', value: 5 },
    ]);
    expect(sparkPoints(points, 'day', 'avg')[1]).toEqual({ day: '2026-02-02', value: null });
    expect(
      sparkPoints(
        [
          ['2026-01-01', '1'],
          ['2026-03-01', '2'],
        ],
        'month',
        'sum',
      ),
    ).toHaveLength(3);
    expect(sparkPoints(null, 'day', 'count')).toEqual([]);
    expect(sparkPoints([[null, '1']], 'day', 'count')).toEqual([]);
  });

  it('is a line across its box, the least low and the most high, broken at gaps', () => {
    const shape = sparkShape([
      { day: 'a', value: 0 },
      { day: 'b', value: 10 },
      { day: 'c', value: null },
      { day: 'd', value: 5 },
      { day: 'e', value: null },
    ])!;
    expect(shape.xs).toEqual([0, 25, 50, 75, 100]);
    expect(shape.ys).toEqual([92, 8, null, 50, null]);
    expect(shape.line).toBe('M0 92 L25 8 M75 50');
    expect(shape.last).toBe(3);
    expect(sparkShape([{ day: 'a', value: 7 }])).toEqual({
      xs: [50],
      ys: [50],
      line: 'M50 50',
      last: 0,
    });
    expect(sparkShape([{ day: 'a', value: null }])).toBeNull();
    expect(sparkShape([])).toBeNull();
  });

  it('is listed as rows of its period and the measure', () => {
    const config: KpiConfig = {
      ...kpiDefaults('orders'),
      sparkline: { field: { path: [], column: 'order_date' }, bucket: 'day', label: 'Day' },
    };
    const data = rowsOf('Status', [], {
      columns: [rowsOf('Status', []).columns[1]],
      rows: [['4']],
      points: [['2026-02-01', '1']],
    });
    const listed = pointsData(data, config)!;
    expect(listed.columns.map((c) => [c.name, c.role, c.label, c.type.kind])).toEqual([
      ['d0', 'dimension', 'Day', 'date'],
      ['m0', 'measure', 'Rows', 'int64'],
    ]);
    expect(listed.rows).toEqual([['2026-02-01', '1']]);
    expect(pointsData({ ...data, points: [] }, config)).toBeNull();
    expect(pointsData(data, { ...config, sparkline: null })).toBeNull();
  });
});
