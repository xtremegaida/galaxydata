import { DefaultUrlSerializer, type Params } from '@angular/router';
import fc from 'fast-check';
import type { Schema } from '../api/api-client';
import {
  type ConditionValue,
  type DashboardUrlState,
  type SelectionState,
  dashboardParams,
  isDashboardParam,
  readDashboardParams,
} from './dashboard-url';

type Definition = Schema<'DashboardDefinition'>;
type Filter = Schema<'DashboardFilter'>;

const serializer = new DefaultUrlSerializer();

function filterOf(id: string, kind: Filter['kind'], extra: Partial<Filter> = {}): Filter {
  return {
    id,
    label: id[0].toUpperCase() + id.slice(1),
    field: { source: 'orders', path: [], column: id },
    kind,
    value: null,
    visible: true,
    editable: true,
    multiple: true,
    except: [],
    ...extra,
  };
}

const bar = {
  kind: 'bar',
  source: 'orders',
  conditions: [],
  emits: true,
  listens: { mode: 'all', widgets: [] },
};

const definition = {
  schema: 1,
  layout: { rowHeight: 48, gap: 12, breakpoints: [], items: {}, overrides: {} },
  sources: [{ id: 'orders', entity: 'shop.orders', label: 'Orders' }],
  links: [],
  filters: [
    filterOf('status', 'values', { value: { op: 'in', values: ['open'] } }),
    filterOf('city', 'values', { multiple: false }),
    filterOf('total', 'range'),
    filterOf('placed', 'relative'),
    filterOf('name', 'text'),
    filterOf('vip', 'boolean'),
    filterOf('region', 'values', { editable: false, value: { op: 'in', values: ['EMEA'] } }),
    filterOf('tenant', 'values', { visible: false, editable: false }),
  ],
  widgets: [
    { id: 'title', title: null, config: { kind: 'text', markdown: '# Sales' } },
    { id: 'by-status', title: 'Orders by status', config: bar },
    { id: 'by-month', title: 'Orders by month', config: { ...bar, kind: 'line' } },
    { id: 'quiet', title: 'Quiet', config: { ...bar, emits: false } },
  ],
  refresh: { mode: 'manual', seconds: null },
  public: { showData: true },
} as unknown as Definition;

/** The address of a state, as the browser shows it. */
function addressOf(state: DashboardUrlState): string {
  const tree = serializer.parse('/dashboards/7');
  tree.queryParams = dashboardParams(definition, state);
  return serializer.serialize(tree);
}

/** A state from an address, as the router gives the page its query. */
function read(address: string, maxKeys = 50) {
  return readDashboardParams(definition, serializer.parse(address).queryParams as Params, maxKeys);
}

describe('dashboard addresses', () => {
  it('writes filters by their kinds, and slices chosen and left out', () => {
    const address = addressOf({
      filters: {
        status: { op: 'in', values: ['open', 'shipped'] },
        total: { op: 'between', value: '10', valueTo: '99.5' },
        placed: { op: 'relative', relative: { mode: 'last', count: 3, unit: 'month' } },
        name: { op: 'contains', value: 'Ada' },
        vip: { op: 'eq', value: true },
      },
      selections: {
        'by-status': { mode: 'exclude', keys: [['Cape Town'], [null]] },
        'by-month': {
          mode: 'include',
          keys: [
            ['2026-01-01', 'open'],
            [3, true],
          ],
        },
      },
    });
    expect(decodeURIComponent(address)).toBe(
      "/dashboards/7?f.status=open,shipped&f.total='10'~'99.5'&f.placed=last.3.month&f.name=Ada&f.vip=true" +
        '&s.by-status=!Cape Town,null&s.by-month=2026-01-01|open,3|true',
    );
  });

  it("writes only what isn't the dashboard's own, and a default cleared as nothing", () => {
    expect(
      dashboardParams(definition, {
        filters: { status: { op: 'in', values: ['open'] }, city: null },
        selections: {},
      }),
    ).toEqual({});
    expect(dashboardParams(definition, { filters: { status: null }, selections: {} })).toEqual({
      'f.status': '',
    });
    expect(read('/dashboards/7?f.status=')).toEqual({
      state: { filters: { status: null }, selections: {} },
      problems: [],
    });
  });

  it('reads values in their types: bare literals, quoted text', () => {
    const { state } = read("/dashboards/7?s.by-status=null,'null',42,'42',true,'true',-1.5e3,''");
    expect(state.selections['by-status'].keys).toEqual([
      [null],
      ['null'],
      [42],
      ['42'],
      [true],
      ['true'],
      [-1500],
      [''],
    ]);
    expect(read('/dashboards/7?f.total=~5&f.city=007').state.filters).toEqual({
      total: { op: 'le', value: 5 },
      city: { op: 'in', values: ['007'] },
    });
  });

  it("leaves out what isn't there, can't be changed or read, and says so", () => {
    const { state, problems } = read(
      '/dashboards/7?f.nope=1&f.region=APAC&f.tenant=2&f.city=a,b&f.placed=soon&f.vip=yes&f.total=~' +
        "&s.title=a&s.quiet=a&s.ghost=a&s.by-status='a&s.by-month=a,,b&copy=working",
    );
    expect(state).toEqual({ filters: {}, selections: {} });
    expect(problems).toEqual([
      "There's no filter nope.",
      "The filter Region can't be changed.",
      "The filter Tenant can't be changed.",
      "The filter City couldn't be read: it takes one value.",
      "The filter Placed couldn't be read: 'soon' isn't a period such as last.7.day.",
      "The filter Vip couldn't be read: 'yes' isn't true or false.",
      "The filter Total couldn't be read: a value from or to was expected.",
      "There's no widget title that chooses slices.",
      "There's no widget quiet that chooses slices.",
      "There's no widget ghost that chooses slices.",
      "The slices chosen in Orders by status couldn't be read: the quote at 1 isn't closed.",
      "The slices chosen in Orders by month couldn't be read: a value was expected at 3.",
    ]);
  });

  it('keeps the first slices of a selection longer than the most there may be', () => {
    const { state, problems } = read('/dashboards/7?s.by-status=a,b,c', 2);
    expect(state.selections['by-status']).toEqual({ mode: 'include', keys: [['a'], ['b']] });
    expect(problems).toEqual(['Only the first 2 slices chosen in Orders by status are kept.']);
  });

  it("knows its parameters from the page's", () => {
    expect(['f.status', 's.by-status', 'copy', 'theme', 'f'].map(isDashboardParam)).toEqual([
      true,
      true,
      false,
      false,
      false,
    ]);
  });

  it('reads what it writes, whatever the values', () => {
    fc.assert(
      fc.property(states(), (state) => {
        expect(read(addressOf(state))).toEqual({ state, problems: [] });
      }),
      { numRuns: 400 },
    );
  });
});

/** Text with the address's marks and the grammar's often in it. */
function text(minLength = 0): fc.Arbitrary<string> {
  const marks = fc.constantFrom(
    ',',
    '|',
    '~',
    "'",
    '!',
    '.',
    '&',
    '=',
    '+',
    '%',
    '#',
    '?',
    ' ',
    'null',
    'true',
    '1',
    '-',
    'e',
  );
  return fc.oneof(
    fc.string({ unit: 'grapheme', minLength, maxLength: 12 }),
    fc.string({ unit: fc.oneof(marks, fc.constantFrom('a', 'é', '0')), minLength, maxLength: 8 }),
  );
}

/** JSON's values but objects: what rows' values are. */
function scalars(): fc.Arbitrary<unknown> {
  return fc.oneof(
    fc.constant(null),
    fc.boolean(),
    fc.integer(),
    fc.double({ noNaN: true, noDefaultInfinity: true }).filter((n) => !Object.is(n, -0)),
    text(),
  );
}

function bounds(): fc.Arbitrary<unknown> {
  return scalars().filter((v) => v !== null);
}

function valuesOf(filter: Filter): fc.Arbitrary<ConditionValue | null> {
  const value = ((): fc.Arbitrary<ConditionValue> => {
    switch (filter.kind) {
      case 'values':
        return fc.record({
          op: fc.constantFrom('in' as const, 'notIn' as const),
          values: fc.array(scalars(), { minLength: 1, maxLength: filter.multiple ? 4 : 1 }),
        });
      case 'range':
        return fc.oneof(
          fc.record({ op: fc.constant('between' as const), value: bounds(), valueTo: bounds() }),
          fc.record({ op: fc.constantFrom('ge' as const, 'le' as const), value: bounds() }),
        );
      case 'relative':
        return fc.record({
          op: fc.constant('relative' as const),
          relative: fc.record({
            mode: fc.constantFrom(
              'last' as const,
              'this' as const,
              'previous' as const,
              'toDate' as const,
            ),
            count: fc.integer({ min: 1, max: 1000 }),
            unit: fc.constantFrom(
              'day' as const,
              'week' as const,
              'month' as const,
              'quarter' as const,
              'year' as const,
            ),
          }),
        });
      case 'text':
        return fc.record({ op: fc.constant('contains' as const), value: text() });
      case 'boolean':
        return fc.record({ op: fc.constant('eq' as const), value: fc.boolean() });
    }
  })();
  // Cleared, only where that isn't the default.
  return filter.value ? fc.option(value, { nil: null }) : value;
}

function selections(): fc.Arbitrary<SelectionState> {
  return fc.record({
    mode: fc.constantFrom('include' as const, 'exclude' as const),
    keys: fc.array(fc.array(scalars(), { minLength: 1, maxLength: 2 }), {
      minLength: 1,
      maxLength: 5,
    }),
  });
}

/** States as a viewer makes them: values of the filters they may change unlike the defaults, slices of widgets choosing. */
function states(): fc.Arbitrary<DashboardUrlState> {
  const editable = definition.filters.filter((f) => f.visible && f.editable);
  const filters = fc.record(
    Object.fromEntries(editable.map((f) => [f.id, valuesOf(f)])) as Record<
      string,
      fc.Arbitrary<ConditionValue | null>
    >,
    { requiredKeys: [] },
  );
  const choosing = fc.record(
    { 'by-status': selections(), 'by-month': selections() },
    { requiredKeys: [] },
  );
  return fc
    .record({ filters, selections: choosing })
    .filter(({ filters }) =>
      Object.entries(filters).every(
        ([id, value]) =>
          JSON.stringify(value) !==
          JSON.stringify(definition.filters.find((f) => f.id === id)?.value),
      ),
    )
    .map((state) => state as DashboardUrlState);
}
