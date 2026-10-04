import type { ColDef, ValueFormatterParams, ValueGetterParams } from 'ag-grid-community';
import { columnOf, orderColumns, rowOf } from '../../../../testing/browse';
import type { GridFilter } from '../../../core/browse/browse-url';
import {
  type GridRow,
  binaryText,
  cellText,
  colIdOf,
  columnDefsOf,
  columnStateOf,
  filterKindOf,
  filterModelOf,
  filtersOf,
  headerTooltipOf,
  indexOfColId,
  indexOfColumn,
  lineageText,
  sortOf,
} from './grid-columns';

describe("the grid's columns", () => {
  it('names columns by their places, and reads their values from the rows', () => {
    const defs = columnDefsOf(orderColumns());
    expect(defs.map((def) => [def.colId, def.headerName])).toEqual([
      ['c0', 'id'],
      ['c1', 'status'],
      ['c2', 'total'],
    ]);
    const row = rowOf(['1001', null, '12.50']);
    const value = (def: ColDef<GridRow>, data: GridRow | undefined) =>
      (def.valueGetter as (params: Partial<ValueGetterParams<GridRow>>) => unknown)({ data });
    expect(defs.map((def) => value(def, row))).toEqual(['1001', null, '12.50']);
    expect(value(defs[0], undefined)).toBeUndefined();
    const shown = (def: ColDef<GridRow>, given: unknown) =>
      (def.valueFormatter as (params: Partial<ValueFormatterParams<GridRow>>) => string)({
        value: given,
      });
    expect(shown(defs[1], null)).toBe('NULL');
    expect(shown(defs[1], undefined)).toBe('');
  });

  it("marks the key, numbers and nulls, and says each column's type and where its values come from", () => {
    const defs = columnDefsOf(orderColumns());
    expect(defs.map((def) => def.headerClass ?? null)).toEqual(['gd-key-column', null, null]);
    expect(defs.map((def) => def.cellClass ?? null)).toEqual(['gd-number', null, 'gd-number']);
    const nulls = defs[1].cellClassRules?.['gd-null'] as (params: { value: unknown }) => boolean;
    expect([nulls({ value: null }), nulls({ value: '' }), nulls({ value: undefined })]).toEqual([
      true,
      false,
      false,
    ]);
    expect(defs[0].headerTooltip).toBe('id: int64, the key. From shop.orders.id.');
    expect(defs[2].headerTooltip).toBe('total: decimal(10,2)?. From shop.orders.total.');
  });

  it('filters each column by its type, and sorts by all but binary, JSON and unknown values', () => {
    const kinds = [
      'string',
      'int32',
      'double',
      'int64',
      'decimal',
      'date',
      'dateTime',
      'dateTimeOffset',
      'time',
      'interval',
      'boolean',
      'guid',
      'binary',
      'json',
      'unknown',
    ] as const;
    const defs = columnDefsOf(kinds.map((kind) => columnOf(kind, kind)));
    expect(defs.map((def) => [def.headerName, def.filter, def.sortable])).toEqual([
      ['string', 'agTextColumnFilter', true],
      ['int32', 'agNumberColumnFilter', true],
      ['double', 'agNumberColumnFilter', true],
      ['int64', 'agBigIntColumnFilter', true],
      ['decimal', 'agTextColumnFilter', true],
      ['date', 'agDateColumnFilter', true],
      ['dateTime', 'agDateColumnFilter', true],
      ['dateTimeOffset', 'agDateColumnFilter', true],
      ['time', 'agTextColumnFilter', true],
      ['interval', 'agTextColumnFilter', true],
      ['boolean', 'agTextColumnFilter', true],
      ['guid', 'agTextColumnFilter', true],
      ['binary', 'agTextColumnFilter', false],
      ['json', 'agTextColumnFilter', false],
      ['unknown', 'agTextColumnFilter', false],
    ]);
    const optionsOf = (index: number) =>
      (defs[index].filterParams.filterOptions as (string | { displayKey: string })[]).map(
        (option) => (typeof option === 'string' ? option : option.displayKey),
      );
    expect(optionsOf(0)).toEqual([
      'contains',
      'notContains',
      'equals',
      'notEqual',
      'startsWith',
      'endsWith',
      'blank',
      'notBlank',
    ]);
    expect(optionsOf(3)).toEqual([
      'equals',
      'notEqual',
      'lessThan',
      'lessThanOrEqual',
      'greaterThan',
      'greaterThanOrEqual',
      'inRange',
      'blank',
      'notBlank',
    ]);
    expect(optionsOf(4)).toEqual([
      'equals',
      'notEqual',
      'gdLt',
      'gdLe',
      'gdGt',
      'gdGe',
      'gdBetween',
      'blank',
      'notBlank',
    ]);
    expect(optionsOf(10)).toEqual(['gdTrue', 'gdFalse', 'blank', 'notBlank']);
    expect(optionsOf(11)).toEqual(['equals', 'notEqual', 'blank', 'notBlank']);
    expect(optionsOf(12)).toEqual(['blank', 'notBlank']);
    expect(defs[5].filterParams).toMatchObject({ browserDatePicker: true, maxValidYear: 9999 });
    expect(defs[8].filterParams.filterPlaceholder).toBe('hh:mm:ss');
    expect(defs.every((def) => def.filterParams.buttons.join() === 'reset,apply')).toBe(true);
  });

  it('tells kinds of filters apart by type', () => {
    expect(filterKindOf({ kind: 'int16', nullable: false, text: 'int16' })).toBe('number');
    expect(filterKindOf({ kind: 'single', nullable: false, text: 'single' })).toBe('number');
  });

  it('shows values as text: booleans, date-times with a space, binary as hexadecimal', () => {
    const type = (kind: Parameters<typeof columnOf>[1]) => columnOf('x', kind).type;
    expect(cellText(true, type('boolean'))).toBe('true');
    expect(cellText(false, type('boolean'))).toBe('false');
    expect(cellText('2026-03-01T14:30:00', type('dateTime'))).toBe('2026-03-01 14:30:00');
    expect(cellText('2026-03-01T12:00:00+00:00', type('dateTimeOffset'))).toBe(
      '2026-03-01 12:00:00+00:00',
    );
    expect(cellText('AQL/', type('binary'))).toBe('0x0102ff');
    expect(cellText(12, type('int32'))).toBe('12');
    expect(cellText('9007199254740993', type('int64'))).toBe('9007199254740993');
    expect(cellText({ a: 1 }, type('json'))).toBe('{"a":1}');
    expect(binaryText(btoa('abcdefgh'), 4)).toBe('0x61626364… (8 bytes)');
    expect(binaryText('not base64!', 4)).toBe('not base64!');
  });

  it('says where values come from, for each kind of lineage', () => {
    const sources = [
      { column: 'shop.orders.total', path: null },
      { column: 'shop.customers.name', path: 'customer' },
    ];
    expect(lineageText({ kind: 'direct', sources: sources.slice(1), expression: null })).toBe(
      'From shop.customers.name (through customer).',
    );
    expect(lineageText({ kind: 'computed', sources, expression: 'total * 2' })).toBe(
      'Worked out: total * 2, from shop.orders.total, shop.customers.name (through customer).',
    );
    expect(lineageText({ kind: 'aggregated', sources: [], expression: 'count()' })).toBe(
      'Aggregated: count().',
    );
    expect(lineageText({ kind: 'constant', sources: [], expression: "'x'" })).toBe(
      "A constant: 'x'.",
    );
    expect(lineageText({ kind: 'constant', sources: [], expression: null })).toBe('A constant.');
    expect(lineageText({ kind: 'union', sources, expression: null })).toBe(
      'From shop.orders.total, shop.customers.name (through customer), put together.',
    );
    expect(lineageText({ kind: 'unknown', sources: [], expression: null })).toBe(
      "Where its values come from isn't known.",
    );
    expect(headerTooltipOf(columnOf('n'))).toBe('n: string?. From shop.orders.n.');
  });

  it('finds columns by name, exactly or but for case when only one is', () => {
    const columns = [columnOf('Name'), columnOf('name'), columnOf('City')];
    expect(indexOfColumn(columns, 'name')).toBe(1);
    expect(indexOfColumn(columns, 'NAME')).toBe(-1);
    expect(indexOfColumn(columns, 'city')).toBe(2);
    expect(indexOfColumn(columns, 'x')).toBe(-1);
    expect([colIdOf(3), indexOfColId('c3'), indexOfColId('x3'), indexOfColId('c')]).toEqual([
      'c3',
      3,
      -1,
      -1,
    ]);
  });
});

describe("the grid's filters and sort", () => {
  const columns = [
    columnOf('id', 'int64'),
    columnOf('status'),
    columnOf('total', 'decimal'),
    columnOf('placed', 'date'),
    columnOf('qty', 'int32'),
    columnOf('paid', 'boolean'),
    columnOf('blob', 'binary'),
  ];

  it('holds what the address says in the filters of the columns, and reads it back', () => {
    const filters: GridFilter[] = [
      {
        column: 'total',
        conditions: [
          { op: 'ge', value: '10' },
          { op: 'le', value: '100.5' },
        ],
        any: false,
      },
      { column: 'status', conditions: [{ op: 'contains', value: 'op' }], any: false },
      {
        column: 'placed',
        conditions: [
          { op: 'between', value: '2026-01-01', valueTo: '2026-01-31' },
          { op: 'blank' },
        ],
        any: true,
      },
      { column: 'qty', conditions: [{ op: 'gt', value: '5' }], any: false },
      { column: 'id', conditions: [{ op: 'eq', value: '9007199254740993' }], any: false },
      { column: 'paid', conditions: [{ op: 'eq', value: 'true' }], any: false },
      { column: 'blob', conditions: [{ op: 'notBlank' }], any: false },
    ];
    const { model, left } = filterModelOf(filters, columns);
    expect(left).toEqual([]);
    expect(model).toEqual({
      c2: {
        filterType: 'text',
        operator: 'AND',
        conditions: [
          { filterType: 'text', type: 'gdGe', filter: '10', filterTo: null },
          { filterType: 'text', type: 'gdLe', filter: '100.5', filterTo: null },
        ],
      },
      c1: { filterType: 'text', type: 'contains', filter: 'op', filterTo: null },
      c3: {
        filterType: 'date',
        operator: 'OR',
        conditions: [
          { filterType: 'date', type: 'inRange', dateFrom: '2026-01-01', dateTo: '2026-01-31' },
          { filterType: 'date', type: 'blank', dateFrom: null, dateTo: null },
        ],
      },
      c4: { filterType: 'number', type: 'greaterThan', filter: 5, filterTo: null },
      c0: { filterType: 'bigint', type: 'equals', filter: '9007199254740993', filterTo: null },
      c5: { filterType: 'text', type: 'gdTrue' },
      c6: { filterType: 'text', type: 'notBlank', filter: null, filterTo: null },
    });
    expect(filtersOf(model, columns)).toEqual(filters);
  });

  it("reads the grid's date filters as days, whatever time they hold", () => {
    expect(
      filtersOf(
        {
          c3: {
            filterType: 'date',
            type: 'lessThan',
            dateFrom: '2026-02-03 00:00:00',
            dateTo: null,
          },
        },
        columns,
      ),
    ).toEqual([{ column: 'placed', conditions: [{ op: 'lt', value: '2026-02-03' }], any: false }]);
  });

  it('leaves out conditions the grid holds incomplete, and filters on columns it has not', () => {
    expect(
      filtersOf(
        {
          c1: { filterType: 'text', type: 'contains', filter: null },
          c2: { filterType: 'text', type: 'gdBetween', filter: '1', filterTo: null },
          c5: { filterType: 'text', type: 'gdFalse' },
          c9: { filterType: 'text', type: 'contains', filter: 'x' },
          c4: { filterType: 'number', type: 'unknownOption', filter: 1 },
        },
        columns,
      ),
    ).toEqual([{ column: 'paid', conditions: [{ op: 'eq', value: 'false' }], any: false }]);
  });

  it("says what it can't hold of what the address says, and leaves it out", () => {
    const { model, left } = filterModelOf(
      [
        { column: 'gone', conditions: [{ op: 'eq', value: '1' }], any: false },
        { column: 'status', conditions: [{ op: 'lt', value: 'm' }], any: false },
        {
          column: 'qty',
          conditions: [
            { op: 'gt', value: 'many' },
            { op: 'lt', value: '10' },
          ],
          any: false,
        },
        { column: 'QTY', conditions: [{ op: 'eq', value: '3' }], any: false },
        { column: 'id', conditions: [{ op: 'eq', value: '1.5' }], any: false },
        { column: 'placed', conditions: [{ op: 'eq', value: '01/02/2026' }], any: false },
        { column: 'paid', conditions: [{ op: 'ne', value: 'true' }], any: false },
        {
          column: 'total',
          conditions: [
            { op: 'gt', value: '1' },
            { op: 'lt', value: '9' },
            { op: 'ne', value: '5' },
          ],
          any: false,
        },
        { column: 'blob', conditions: [{ op: 'eq', value: 'AA==' }], any: false },
      ],
      columns,
    );
    expect(left).toEqual([
      'The filter on gone is left out: the rows have no such column.',
      "A condition on status is left out: its filter doesn't take lt.",
      "A condition on qty is left out: many isn't a number.",
      'A second filter on QTY is left out: a column has one.',
      "A condition on id is left out: 1.5 isn't a whole number.",
      "A condition on placed is left out: 01/02/2026 isn't a date (yyyy-mm-dd).",
      'A condition on paid is left out: true or false is all its filter takes.',
      'A condition on total is left out: a filter holds 2.',
      "A condition on blob is left out: its filter doesn't take eq.",
    ]);
    expect(Object.keys(model)).toEqual(['c4', 'c2']);
    expect(model['c4']).toEqual({
      filterType: 'number',
      type: 'lessThan',
      filter: 10,
      filterTo: null,
    });
  });

  it('holds a sort as the columns do, leaving out columns it has not and those it does not sort by', () => {
    const { state, left } = columnStateOf(
      [
        { column: 'total', desc: true },
        { column: 'gone', desc: false },
        { column: 'blob', desc: false },
        { column: 'TOTAL', desc: false },
        { column: 'id', desc: false },
      ],
      columns,
    );
    expect(state).toEqual([
      { colId: 'c2', sort: 'desc', sortIndex: 0 },
      { colId: 'c0', sort: 'asc', sortIndex: 1 },
    ]);
    expect(left).toEqual([
      'The sort by gone is left out: the rows have no such column.',
      "The sort by blob is left out: its values aren't sorted.",
    ]);
    expect(
      sortOf(
        [
          { colId: 'c2', sort: 'desc' },
          { colId: 'c0', sort: 'asc' },
          { colId: 'c9', sort: 'asc' },
        ],
        columns,
      ),
    ).toEqual([
      { column: 'total', desc: true },
      { column: 'id', desc: false },
    ]);
  });
});
