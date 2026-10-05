import { DefaultUrlSerializer, type Params, type UrlSegment } from '@angular/router';
import fc from 'fast-check';
import {
  type BrowseCrumb,
  type BrowseLocation,
  type GridCondition,
  type GridFilter,
  type GridOp,
  browseUrlTree,
  crumbOf,
  followedLocation,
  locationAt,
  opValues,
  readBrowseLocation,
  sameKey,
} from './browse-url';

const serializer = new DefaultUrlSerializer();

/** The address of a location, as the browser shows it. */
function addressOf(location: BrowseLocation): string {
  return serializer.serialize(browseUrlTree(location));
}

/** Where an address leads, as the router gives browsing its segments and query. */
function read(address: string) {
  const tree = serializer.parse(address);
  const segments: UrlSegment[] = tree.root.children['primary']?.segments ?? [];
  expect(segments[0]?.path).toBe('browse');
  return readBrowseLocation(segments.slice(1), tree.queryParams as Params);
}

describe('browsing addresses', () => {
  it("reads a path through the data, each crumb's state, and the crumb shown", () => {
    expect(
      read(
        '/browse/shop.customers;f=country:eq:ZA;row=42/orders;sort=-placed_at,id;page=3;row=1001/lines;w=qty%20%3E%202?at=1',
      ),
    ).toEqual({
      location: {
        crumbs: [
          crumbOf('shop.customers', {
            filters: [{ column: 'country', conditions: [{ op: 'eq', value: 'ZA' }], any: false }],
            row: ['42'],
          }),
          crumbOf('orders', {
            sort: [
              { column: 'placed_at', desc: true },
              { column: 'id', desc: false },
            ],
            page: 2,
            row: ['1001'],
          }),
          crumbOf('lines', { where: 'qty > 2' }),
        ],
        at: 1,
      },
      problems: [],
    });
  });

  it('writes filters with their conditions, joined by or when any will do', () => {
    const filters: GridFilter[] = [
      {
        column: 'total',
        conditions: [
          { op: 'ge', value: '10' },
          { op: 'le', value: '100' },
        ],
        any: false,
      },
      {
        column: 'status',
        conditions: [
          { op: 'eq', value: 'open' },
          { op: 'eq', value: 'held' },
        ],
        any: true,
      },
      {
        column: 'placed_at',
        conditions: [{ op: 'between', value: '2026-01-01', valueTo: '2026-01-31' }],
        any: false,
      },
      { column: 'note', conditions: [{ op: 'blank' }], any: false },
    ];
    const address = addressOf({ crumbs: [crumbOf('shop.orders', { filters })], at: 0 });
    expect(address).toBe(
      '/browse/shop.orders;f=total:ge:10:le:100,status:eq:open:or:eq:held,placed_at:between:2026-01-01:2026-01-31,note:blank',
    );
    expect(read(address)?.location.crumbs[0].filters).toEqual(filters);
  });

  it('quotes names and values that are empty or hold its marks, and sorted names that start with -', () => {
    const crumb = crumbOf('xl["Budget 2024"]["Sheet 1"]', {
      filters: [
        {
          column: 'a,b',
          conditions: [
            { op: 'eq', value: "it's: 1~2" },
            { op: 'ne', value: '' },
          ],
          any: false,
        },
      ],
      sort: [
        { column: '-x', desc: false },
        { column: 'y-', desc: true },
      ],
      row: ['1', 'a~b', ''],
    });
    const address = addressOf({ crumbs: [crumb], at: 0 });
    expect(address).toBe(
      "/browse/xl%5B%22Budget%202024%22%5D%5B%22Sheet%201%22%5D;f='a,b':eq:'it''s:%201~2':ne:'';sort='-x',-y-;row=1~'a~b'~''",
    );
    expect(read(address)?.location.crumbs[0]).toEqual(crumb);
  });

  it('leaves out the crumb shown when it is the last', () => {
    const crumbs = [crumbOf('shop.customers', { row: ['42'] }), crumbOf('orders')];
    expect(addressOf({ crumbs, at: 1 })).toBe('/browse/shop.customers;row=42/orders');
    expect(addressOf({ crumbs, at: 0 })).toBe('/browse/shop.customers;row=42/orders?at=0');
  });

  it("reads names with the address's own marks, as segments hold them", () => {
    const location: BrowseLocation = { crumbs: [crumbOf('a/b;c=d?e#f(g)'), crumbOf('h i')], at: 1 };
    expect(read(addressOf(location))?.location).toEqual(location);
  });

  it('leaves out what it reads no filters, sort, page or row in, and says so', () => {
    const problems = (address: string) => read(address)?.problems;
    expect(read('/browse/shop.orders;f=total:gt;page=0;sort=;row=%271')?.location).toEqual({
      crumbs: [crumbOf('shop.orders')],
      at: 0,
    });
    expect(problems('/browse/shop.orders;f=total:gt')).toEqual([
      "The filters (f) of shop.orders couldn't be read, and are left out: ':' was expected at 9.",
    ]);
    expect(problems('/browse/shop.orders;f=total:above:1')).toEqual([
      "The filters (f) of shop.orders couldn't be read, and are left out: above isn't an operation.",
    ]);
    expect(problems('/browse/shop.orders;f=s:eq:a:or:eq:b:eq:c')).toEqual([
      "The filters (f) of shop.orders couldn't be read, and are left out: the filter on s mixes conditions joined by or with others.",
    ]);
    expect(problems('/browse/shop.orders;f=:eq:1')).toEqual([
      "The filters (f) of shop.orders couldn't be read, and are left out: a column was expected at 1.",
    ]);
    expect(problems('/browse/shop.orders;f=a:eq:1:')).toEqual([
      "The filters (f) of shop.orders couldn't be read, and are left out: an operation was expected at 8.",
    ]);
    expect(problems('/browse/shop.orders;page=0')).toEqual([
      "The page (page) of shop.orders couldn't be read, and is left out: 0 isn't a page, which counts from 1.",
    ]);
    expect(problems('/browse/shop.orders;sort=a,')).toEqual([
      "The sort (sort) of shop.orders couldn't be read, and is left out: a column was expected at 3.",
    ]);
    expect(problems("/browse/shop.orders/lines;row='1")).toEqual([
      "The row chosen (row) of the crumb lines couldn't be read, and is left out: the quote at 1 isn't closed.",
    ]);
    expect(problems("/browse/shop.orders;row=1~2x'")).toEqual([
      "The row chosen (row) of shop.orders couldn't be read, and is left out: ''' wasn't expected at 5.",
    ]);
  });

  it('shows the last crumb when the address names none it has', () => {
    for (const at of ['2', '-1', 'x', '']) {
      const found = read(`/browse/a;row=1/b?at=${at}`);
      expect(found?.location.at).toBe(1);
      expect(found?.problems).toEqual([
        `The crumb shown (at=${at}) isn't one of the address's; the last is shown.`,
      ]);
    }
  });

  it('takes a condition of only white space for none', () => {
    expect(read('/browse/a;w=%20%20')?.location.crumbs[0].where).toBeNull();
  });

  it('leads nowhere without segments', () => {
    expect(readBrowseLocation([], {})).toBeNull();
  });

  it('reads what it writes, whatever the names and values', () => {
    fc.assert(
      fc.property(locations(), (location) => {
        expect(read(addressOf(location))).toEqual({ location, problems: [] });
      }),
      { numRuns: 400 },
    );
  });
});

describe('following a path', () => {
  const customers = crumbOf('shop.customers', {
    sort: [{ column: 'name', desc: false }],
    row: ['7'],
  });
  const orders = crumbOf('orders', { page: 2, row: ['1001'] });
  const lines = crumbOf('lines', { where: 'qty > 1' });

  it("follows a navigation from a row: the row chosen, the navigation's crumb shown after it", () => {
    expect(followedLocation({ crumbs: [customers], at: 0 }, ['42'], 'orders')).toEqual({
      crumbs: [{ ...customers, row: ['42'] }, crumbOf('orders')],
      at: 1,
    });
  });

  it('lets go of the crumbs after the one shown', () => {
    expect(
      followedLocation({ crumbs: [customers, orders, lines], at: 0 }, ['42'], 'orders'),
    ).toEqual({
      crumbs: [{ ...customers, row: ['42'] }, crumbOf('orders')],
      at: 1,
    });
    expect(
      followedLocation({ crumbs: [customers, orders, lines], at: 0 }, ['7'], 'payments'),
    ).toEqual({
      crumbs: [customers, crumbOf('payments')],
      at: 1,
    });
  });

  it('keeps the crumbs before the one shown, following from a later crumb', () => {
    expect(
      followedLocation({ crumbs: [customers, orders, lines], at: 1 }, ['1002'], 'payments'),
    ).toEqual({
      crumbs: [customers, { ...orders, row: ['1002'] }, crumbOf('payments')],
      at: 2,
    });
  });

  it('keeps the crumbs after the one shown when they follow the same navigation from the same row', () => {
    const location = { crumbs: [customers, orders, lines], at: 0 };
    expect(followedLocation(location, ['7'], 'orders')).toEqual({ crumbs: location.crumbs, at: 1 });
  });

  it('shows another crumb of the same path', () => {
    const location = { crumbs: [customers, orders, lines], at: 2 };
    expect(locationAt(location, 0)).toEqual({ crumbs: location.crumbs, at: 0 });
    expect(addressOf(locationAt(location, 2))).not.toContain('at=');
  });

  it("compares rows' keys value by value", () => {
    expect(sameKey(['1', '2'], ['1', '2'])).toBe(true);
    expect(sameKey(['1', '2'], ['1'])).toBe(false);
    expect(sameKey(['1,2'], ['1', '2'])).toBe(false);
    expect(sameKey(null, null)).toBe(true);
    expect(sameKey(null, [])).toBe(false);
  });
});

/** Text with the address's marks and the grammar's often in it. */
function text(minLength = 0): fc.Arbitrary<string> {
  const marks = fc.constantFrom(
    ',',
    ':',
    '~',
    "'",
    '-',
    '/',
    ';',
    '=',
    '?',
    '#',
    '%',
    ' ',
    '(',
    ')',
    '&',
    '+',
    'or',
    'eq',
  );
  return fc.oneof(
    fc.string({ unit: 'grapheme', minLength, maxLength: 12 }),
    fc.string({ unit: fc.oneof(marks, fc.constantFrom('a', 'é', '1')), minLength, maxLength: 8 }),
  );
}

/** Names of entities and navigations: anything but the steps `.` and `..`, which addresses resolve. */
function names(): fc.Arbitrary<string> {
  return text(1).filter((name) => name !== '.' && name !== '..');
}

function conditions(): fc.Arbitrary<GridCondition> {
  return fc
    .tuple(fc.constantFrom(...(Object.keys(opValues) as GridOp[])), text(), text())
    .map(([op, value, valueTo]) => {
      switch (opValues[op]) {
        case 0:
          return { op };
        case 1:
          return { op, value };
        default:
          return { op, value, valueTo };
      }
    });
}

function filters(): fc.Arbitrary<GridFilter> {
  return fc
    .record({
      column: text(1),
      conditions: fc.array(conditions(), { minLength: 1, maxLength: 3 }),
      any: fc.boolean(),
    })
    .map((filter) => ({ ...filter, any: filter.conditions.length > 1 && filter.any }));
}

function crumbs(): fc.Arbitrary<BrowseCrumb> {
  return fc.record({
    name: names(),
    filters: fc.array(filters(), { maxLength: 3 }),
    where: fc.option(
      text(1).filter((where) => where.trim() !== ''),
      { nil: null },
    ),
    sort: fc.array(fc.record({ column: text(1), desc: fc.boolean() }), { maxLength: 3 }),
    page: fc.nat({ max: 1_000_000 }),
    row: fc.option(fc.array(text(), { minLength: 1, maxLength: 3 }), { nil: null }),
  });
}

function locations(): fc.Arbitrary<BrowseLocation> {
  return fc
    .array(crumbs(), { minLength: 1, maxLength: 4 })
    .chain((list) => fc.nat({ max: list.length - 1 }).map((at) => ({ crumbs: list, at })));
}
