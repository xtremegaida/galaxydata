import { barDefaults, tableDefaults } from './widget-defaults';
import { excludingToday, filterValueText, relativeText, selectionText } from './describe';
import type { Widget } from './definition';

describe('what dashboards say', () => {
  it('says periods relative to today as they are said', () => {
    expect(
      [
        { mode: 'last', count: 1, unit: 'day' },
        { mode: 'last', count: 7, unit: 'day' },
        { mode: 'this', count: 1, unit: 'day' },
        { mode: 'this', count: 1, unit: 'quarter' },
        { mode: 'previous', count: 1, unit: 'day' },
        { mode: 'previous', count: 1, unit: 'month' },
        { mode: 'previous', count: 2, unit: 'year' },
        { mode: 'toDate', count: 1, unit: 'year' },
      ].map((range) => relativeText(range as Parameters<typeof relativeText>[0])),
    ).toEqual([
      'last day',
      'last 7 days',
      'today',
      'this quarter',
      'yesterday',
      'previous month',
      'previous 2 years',
      'year to date',
    ]);
  });

  it('says periods leaving today out as up to yesterday, and leaves it out only of those ending today', () => {
    const say = (range: object) => relativeText(range as Parameters<typeof relativeText>[0]);
    expect([
      say({ mode: 'last', count: 30, unit: 'day', excludeToday: true }),
      say({ mode: 'last', count: 1, unit: 'day', excludeToday: true }),
      say({ mode: 'last', count: 1, unit: 'month', excludeToday: true }),
      say({ mode: 'toDate', count: 1, unit: 'month', excludeToday: true }),
    ]).toEqual([
      'last 30 days to yesterday',
      'yesterday',
      'last month to yesterday',
      'month to yesterday',
    ]);
    expect(excludingToday({ mode: 'last', count: 7, unit: 'day' }, true)).toEqual({
      mode: 'last',
      count: 7,
      unit: 'day',
      excludeToday: true,
    });
    expect(
      excludingToday({ mode: 'last', count: 7, unit: 'day', excludeToday: true }, false),
    ).toEqual({
      mode: 'last',
      count: 7,
      unit: 'day',
    });
    expect(
      excludingToday({ mode: 'previous', count: 1, unit: 'month', excludeToday: true }, true),
    ).toEqual({
      mode: 'previous',
      count: 1,
      unit: 'month',
    });
  });

  it("says what a filter's value keeps", () => {
    const say = (value: Parameters<typeof filterValueText>[0]) => filterValueText(value, 'en-ZA');
    expect([
      say(null),
      say({ op: 'in', values: ['open', null] }),
      say({ op: 'notIn', values: ['EMEA'] }),
      say({ op: 'between', value: 10, valueTo: '99.5' }),
      say({ op: 'ge', value: '2026-01-01' }),
      say({ op: 'le', value: 1500 }),
      say({ op: 'contains', value: 'Ada' }),
      say({ op: 'eq', value: true }),
    ]).toEqual([
      'any',
      'open, (no value)',
      'not EMEA',
      '10 to 99.5',
      'from 2026-01-01',
      'up to 1 500',
      'contains Ada',
      'yes',
    ]);
  });

  it('says what is chosen in a widget by its parts, periods as they are read', () => {
    const bars: Widget = {
      id: 'by-month',
      title: 'Orders by month',
      config: {
        ...barDefaults('orders'),
        dimension: { field: { path: [], column: 'placed' }, bucket: 'month', label: 'Month' },
        series: { field: { path: [], column: 'status' }, bucket: null, label: 'Status' },
      },
    };
    expect(
      selectionText(
        bars,
        {
          mode: 'include',
          keys: [
            ['2026-01-01', 'open'],
            ['2026-02-01', null],
          ],
        },
        'en-ZA',
      ),
    ).toEqual({ name: 'Month · Status', values: 'Jan 2026 · open, Feb 2026 · (no value)' });
    const bounced: Widget = {
      id: 'bounced',
      title: null,
      config: {
        ...barDefaults('sessions'),
        dimension: {
          field: { path: [], column: 'bounced' },
          bucket: null,
          label: 'Visit',
          booleans: { true: 'Bounced', false: '' },
        },
      },
    };
    expect(selectionText(bounced, { mode: 'exclude', keys: [[true], [false]] }, 'en-ZA')).toEqual({
      name: 'Visit',
      values: 'not Bounced, No',
    });
    const raw: Widget = {
      id: 'rows',
      title: 'Orders',
      config: { ...tableDefaults('orders'), mode: 'raw' },
    };
    expect(selectionText(raw, { mode: 'exclude', keys: [['42']] }, 'en-ZA')).toEqual({
      name: 'Orders',
      values: 'not 42',
    });
  });
});
