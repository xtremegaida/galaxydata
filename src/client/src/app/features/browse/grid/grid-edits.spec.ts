import type {
  CellClassParams,
  ColDef,
  ICellRendererParams,
  SuppressKeyboardEventParams,
  ValueFormatterParams,
  ValueGetterParams,
  ValueSetterParams,
} from 'ag-grid-community';
import { changeOf, insertOf } from '../../../../testing/changes';
import {
  columnOf,
  customerReference,
  editableColumns,
  linkedColumns,
  rowOf,
} from '../../../../testing/browse';
import type { ChangeIssue, PendingChange } from '../../../core/changes/pending-changes';
import type { GridColumn, GridRow } from './grid-columns';
import {
  type Capabilities,
  type EditRow,
  type GridEdits,
  StateCell,
  cellEditable,
  conflicts,
  editedColumnDefsOf,
  parsedValue,
  referenceChangesOf,
  rowClassRulesOf,
  rowStateOf,
  shownValue,
  stateColId,
} from './grid-edits';
import { type LinkSchema, linkedColumnDefsOf } from './grid-links';

/** What the grid's edits were asked to do. */
interface Asked {
  set: [GridRow, Map<number, unknown>, unknown][];
  refused: [string, string][];
  picked: [GridRow, number][];
  toggled: GridRow[];
  reverted: [GridRow, number][];
}

function editsOf(
  changes: Map<GridRow, PendingChange>,
  capabilities: Partial<Capabilities> = {},
  issues = new Map<GridRow, ChangeIssue[]>(),
): { edits: GridEdits; asked: Asked } {
  const asked: Asked = { set: [], refused: [], picked: [], toggled: [], reverted: [] };
  const edits: GridEdits = {
    capabilities: { canInsert: true, canUpdate: true, canDelete: true, ...capabilities },
    changeOf: (row) => changes.get(row) ?? null,
    issuesOf: (row) => issues.get(row) ?? [],
    set: (row, values, display) => asked.set.push([row, new Map(values), display]),
    refused: (_, column, reason) => asked.refused.push([column.name, reason]),
    pick: (row, at) => asked.picked.push([row, at]),
    toggleDelete: (row) => asked.toggled.push(row),
    revertCell: (row, index) => asked.reverted.push([row, index]),
  };
  return { edits, asked };
}

const schemaOf = (columns: GridColumn[]): LinkSchema => ({
  columns,
  references: [],
  collections: [],
});

const newRow: EditRow = { id: null, k: null, v: [], r: null, tempId: 't1' };

describe('parsedValue', () => {
  const of = (kind: GridColumn['type']['kind'], changes: Partial<GridColumn> = {}) =>
    columnOf('x', kind, changes);

  it('reads text as typed, empty text too', () => {
    expect(parsedValue(' a b ', of('string'))).toEqual({ value: ' a b ' });
    expect(parsedValue('', of('string'))).toEqual({ value: '' });
    expect(parsedValue(null, of('string'))).toEqual({ value: null });
  });

  it('takes nothing for NULL, where a column may be NULL', () => {
    expect(parsedValue('  ', of('int32'))).toEqual({ value: null });
    expect(
      parsedValue(
        null,
        columnOf('n', 'int32', { type: { kind: 'int32', nullable: false, text: 'int32' } }),
      ),
    ).toEqual({
      problem: "'n' can't be NULL",
    });
  });

  it('reads whole numbers in range: those to 32 bits as numbers, 64 as text', () => {
    expect(parsedValue(' 42 ', of('int32'))).toEqual({ value: 42 });
    expect(parsedValue('-32768', of('int16'))).toEqual({ value: -32768 });
    expect(parsedValue('32768', of('int16'))).toEqual({
      problem: '32768 is out of range (-32768 to 32767)',
    });
    expect(parsedValue('4.5', of('int32'))).toEqual({ problem: "4.5 isn't a whole number" });
    expect(parsedValue('9223372036854775807', of('int64'))).toEqual({
      value: '9223372036854775807',
    });
    expect(parsedValue('9223372036854775808', of('int64'))).toEqual({
      problem: '9223372036854775808 is out of range',
    });
    expect(parsedValue(7, of('int64'))).toEqual({ value: '7' });
  });

  it('reads decimals as text, within their precision and scale', () => {
    const money = of('decimal', {
      type: { kind: 'decimal', nullable: true, text: 'decimal(5,2)?', precision: 5, scale: 2 },
    });
    expect(parsedValue('123.40', money)).toEqual({ value: '123.40' });
    expect(parsedValue('-0.5', money)).toEqual({ value: '-0.5' });
    expect(parsedValue('1.234', money)).toEqual({
      problem: '1.234 has more than 2 places after the point',
    });
    expect(parsedValue('1234', money)).toEqual({ problem: '1234 is too large for decimal(5,2)' });
    expect(parsedValue('1,5', money)).toEqual({ problem: "1,5 isn't a number" });
    expect(parsedValue('.', money)).toEqual({ problem: ". isn't a number" });
  });

  it('reads doubles as numbers, and what JSON has no number for as text', () => {
    expect(parsedValue('1e3', of('double'))).toEqual({ value: 1000 });
    expect(parsedValue('NaN', of('double'))).toEqual({ value: 'NaN' });
    expect(parsedValue('1e999', of('double'))).toEqual({ problem: "1e999 isn't a number" });
    // A single holds less than a double.
    expect(parsedValue('1e39', of('double'))).toEqual({ value: 1e39 });
    expect(parsedValue('-1e39', of('single'))).toEqual({
      problem: '-1e39 is out of range for a single',
    });
    expect(parsedValue('3e38', of('single'))).toEqual({ value: 3e38 });
  });

  it('reads booleans, dates, date-times, times, intervals and guids', () => {
    expect(parsedValue('TRUE', of('boolean'))).toEqual({ value: true });
    expect(parsedValue(false, of('boolean'))).toEqual({ value: false });
    expect(parsedValue('yes', of('boolean'))).toEqual({ problem: "yes isn't true or false" });
    expect(parsedValue('2024-02-29', of('date'))).toEqual({ value: '2024-02-29' });
    expect(parsedValue('2023-02-29', of('date'))).toEqual({
      problem: "2023-02-29 isn't a date (yyyy-mm-dd)",
    });
    expect(parsedValue('2026-03-01 10:30', of('dateTime'))).toEqual({
      value: '2026-03-01T10:30',
    });
    expect(parsedValue('2026-03-01T10:30:15.5', of('dateTime'))).toEqual({
      value: '2026-03-01T10:30:15.5',
    });
    expect(parsedValue('2026-03-01 10:30+02:00', of('dateTime'))).toMatchObject({
      problem: expect.stringContaining("isn't a date-time"),
    });
    expect(parsedValue('2026-03-01 10:30+02:00', of('dateTimeOffset'))).toEqual({
      value: '2026-03-01T10:30+02:00',
    });
    expect(parsedValue('25:00', of('time'))).toEqual({ problem: "25:00 isn't a time (hh:mm:ss)" });
    expect(parsedValue('08:30:00', of('time'))).toEqual({ value: '08:30:00' });
    expect(parsedValue('1.02:03:04', of('interval'))).toEqual({ value: '1.02:03:04' });
    expect(parsedValue('soon', of('interval'))).toEqual({
      problem: "soon isn't an interval (d.hh:mm:ss)",
    });
    expect(parsedValue('{0f8fad5b-d9cb-469f-a165-70867728950e}', of('guid'))).toEqual({
      value: '{0f8fad5b-d9cb-469f-a165-70867728950e}',
    });
    expect(parsedValue('0f8fad5b', of('guid'))).toEqual({ problem: "0f8fad5b isn't a guid" });
  });

  it('shortens long values it says are wrong', () => {
    expect(parsedValue('x'.repeat(50), of('int32'))).toEqual({
      problem: `${'x'.repeat(37)}... isn't a whole number`,
    });
  });
});

describe('the cells of rows with changes', () => {
  const columns = editableColumns();
  const read = rowOf(['1001', 'open', '12.50']);

  it("shows values as they will be: changed, read, or a new row's default", () => {
    const change = changeOf({ values: { STATUS: 'paid' }, original: { STATUS: 'open' } });
    expect(shownValue(columns[1], 1, read, change)).toBe('paid');
    expect(shownValue(columns[2], 2, read, change)).toBe('12.50');
    expect(shownValue(columns[1], 1, newRow, insertOf('t1'))).toBeUndefined();
    expect(shownValue(columns[1], 1, newRow, insertOf('t1', { values: { status: null } }))).toBe(
      null,
    );
  });

  it('marks columns changed elsewhere since: their values read now not the originals', () => {
    const change = changeOf({ values: { status: 'paid' }, original: { status: 'new' } });
    expect(conflicts(columns[1], 1, read, change)).toBe(true);
    expect(conflicts(columns[1], 1, rowOf(['1001', 'new', '1']), change)).toBe(false);
    expect(conflicts(columns[2], 2, read, change)).toBe(false);
    expect(conflicts(columns[1], 1, newRow, insertOf('t1'))).toBe(false);
    const deleted = changeOf({ kind: 'delete', values: {}, original: { total: '9.99' } });
    expect(conflicts(columns[2], 2, read, deleted)).toBe(true);
  });

  it("says each row's change", () => {
    expect(rowStateOf(newRow, null)).toBe('new');
    expect(rowStateOf(read, changeOf())).toBe('changed');
    expect(rowStateOf(read, changeOf({ kind: 'delete' }))).toBe('deleted');
    expect(rowStateOf(read, null)).toBeNull();
  });

  it('lets cells be given values as the entity and their columns allow', () => {
    const changes = new Map<GridRow, PendingChange>();
    const { edits } = editsOf(changes);
    expect(cellEditable(columns[1], read, edits)).toBe(true);
    expect(cellEditable(columns[0], read, edits)).toBe(false);
    expect(cellEditable(columns[0], newRow, edits)).toBe(true);
    expect(cellEditable({ ...columns[1], insert: 'never' }, newRow, edits)).toBe(false);
    expect(cellEditable(columns[1], rowOf(['x', 'y', 'z'], null), edits)).toBe(false);
    expect(cellEditable(columns[1], null, edits)).toBe(false);
    changes.set(read, changeOf({ kind: 'delete' }));
    expect(cellEditable(columns[1], read, edits)).toBe(false);
    const { edits: readOnly } = editsOf(new Map(), { canUpdate: false, canInsert: false });
    expect(cellEditable(columns[1], rowOf(['1', 'a', '1']), readOnly)).toBe(false);
    expect(cellEditable(columns[1], newRow, readOnly)).toBe(false);
  });
});

describe('editedColumnDefsOf', () => {
  const columns = editableColumns();
  const read = rowOf(['1001', 'open', '12.50']);

  function defsOf(
    changes = new Map<GridRow, PendingChange>(),
    capabilities = {},
    issues = new Map<GridRow, ChangeIssue[]>(),
  ) {
    const { edits, asked } = editsOf(changes, capabilities, issues);
    const schema = schemaOf(columns);
    return {
      defs: editedColumnDefsOf(schema, linkedColumnDefsOf(schema, null, true), edits),
      asked,
      edits,
    };
  }

  const getter = (def: ColDef<GridRow>, data: GridRow) =>
    (def.valueGetter as (p: ValueGetterParams<GridRow>) => unknown)({
      data,
    } as ValueGetterParams<GridRow>);
  const formatted = (def: ColDef<GridRow>, data: GridRow) =>
    (def.valueFormatter as (p: ValueFormatterParams<GridRow>) => string)({
      data,
      value: getter(def, data),
    } as ValueFormatterParams<GridRow>);
  const classes = (def: ColDef<GridRow>, data: GridRow) =>
    Object.entries(def.cellClassRules ?? {})
      .filter(([, rule]) =>
        (rule as (p: CellClassParams<GridRow>) => boolean)({
          data,
          value: getter(def, data),
        } as CellClassParams<GridRow>),
      )
      .map(([name]) => name);
  const setter = (def: ColDef<GridRow>, data: GridRow, newValue: unknown) =>
    (def.valueSetter as (p: ValueSetterParams<GridRow>) => boolean)({
      data,
      newValue,
      oldValue: getter(def, data),
    } as ValueSetterParams<GridRow>);
  const editable = (def: ColDef<GridRow>, data: GridRow) =>
    (def.editable as (p: { data: GridRow }) => boolean)({ data });
  const tooltip = (def: ColDef<GridRow>, data: GridRow) =>
    (def.tooltip as (p: { data: GridRow }) => string | undefined)({ data });

  it("puts a column saying each row's change first, pinned", () => {
    const changes = new Map<GridRow, PendingChange>([[read, changeOf({ kind: 'delete' })]]);
    const { defs } = defsOf(changes);
    expect(defs.map((def) => def.colId)).toEqual([stateColId, 'c0', 'c1', 'c2']);
    expect(defs[0]).toMatchObject({ pinned: 'left', sortable: false, filter: false });
    expect(getter(defs[0], read)).toBe('deleted');
    expect(getter(defs[0], newRow)).toBe('new');
    expect(getter(defs[0], rowOf(['1002', 'x', '1']))).toBeNull();
  });

  it('shows changed values, marked, and says what they were', () => {
    const change = changeOf({ values: { status: 'paid' }, original: { status: 'open' } });
    const { defs } = defsOf(new Map([[read, change]]));
    expect(getter(defs[2], read)).toBe('paid');
    expect(classes(defs[2], read)).toEqual(['gd-dirty']);
    expect(tooltip(defs[2], read)).toBe('Changed from open');
    expect(classes(defs[3], read)).toEqual([]);
    expect(tooltip(defs[3], read)).toBeUndefined();
  });

  it("marks what a preview found can't be committed: cells by their columns, and rows", () => {
    const change = changeOf({ values: { status: 'lost' }, original: { status: 'open' } });
    const issues = [
      { change: 1, column: 'STATUS', message: "'status' takes open, paid or shipped" },
      { change: 1, column: null, message: 'The row is gone' },
    ];
    const { defs } = defsOf(new Map([[read, change]]), {}, new Map([[read, issues]]));
    expect(classes(defs[2], read)).toEqual(['gd-dirty', 'gd-invalid']);
    expect(tooltip(defs[2], read)).toBe("Can't be committed: 'status' takes open, paid or shipped");
    expect(classes(defs[3], read)).toEqual([]);
    expect(classes(defs[0], read)).toEqual(['gd-invalid']);
    expect(
      (defs[0].tooltip as (p: { value: unknown; data: GridRow }) => string)({
        value: 'changed',
        data: read,
      }),
    ).toBe("Changed, can't be committed: 'status' takes open, paid or shipped The row is gone");
    expect(
      (defs[0].tooltip as (p: { value: unknown; data: GridRow }) => string)({
        value: 'changed',
        data: rowOf(['1002', 'x', '1']),
      }),
    ).toBe('Changed');
    // The state cell draws it.
    const cell = new StateCell();
    cell.init({
      ...(defs[0].cellRendererParams as object),
      value: 'changed',
      data: read,
      eGridCell: document.createElement('div'),
    } as unknown as ICellRendererParams<GridRow>);
    expect(cell.getGui().textContent).toBe("errorChanged, can't be committed");
    expect(cell.getGui().querySelector('.gd-state-invalid')).not.toBeNull();
  });

  it('marks values changed elsewhere since they were changed here', () => {
    const change = changeOf({ values: { status: 'paid' }, original: { status: 'new' } });
    const { defs } = defsOf(new Map([[read, change]]));
    expect(classes(defs[2], read)).toEqual(['gd-dirty', 'gd-conflict']);
    expect(tooltip(defs[2], read)).toBe(
      'Changed elsewhere since it was changed here: it was new, it is open',
    );
  });

  it("shows new rows' defaults, and the values they need", () => {
    const { defs } = defsOf(new Map([[newRow, insertOf('t1', { values: { id: '7' } })]]));
    expect(formatted(defs[1], newRow)).toBe('7');
    expect(classes(defs[1], newRow)).toEqual(['gd-dirty']);
    expect(formatted(defs[2], newRow)).toBe('DEFAULT');
    expect(classes(defs[2], newRow)).toEqual(['gd-default']);
    expect(tooltip(defs[2], newRow)).toBe("The column's default");
    expect(formatted(defs[3], newRow)).toBe('');
    expect(classes(defs[3], newRow)).toEqual(['gd-invalid']);
    expect(tooltip(defs[3], newRow)).toBe('Needs a value');
  });

  it('sets values edited, checked first, unless they are the same', () => {
    const { defs, asked } = defsOf();
    expect(setter(defs[3], read, '1.5')).toBe(true);
    expect(asked.set).toEqual([[read, new Map([[2, '1.5']]), undefined]]);
    expect(setter(defs[3], read, '1.555')).toBe(false);
    expect(asked.refused).toEqual([['total', '1.555 has more than 2 places after the point']]);
    expect(setter(defs[3], read, null)).toBe(false);
    expect(asked.refused[1]).toEqual(['total', "'total' can't be NULL"]);
    expect(setter(defs[2], read, 'open')).toBe(false);
    expect(asked.set.length).toBe(1);
    // A new row's default is no value: a value the same as nothing is still one.
    expect(setter(defs[2], newRow, null)).toBe(true);
  });

  it('edits cells as their rows and columns allow, with editors of their types', () => {
    const { defs } = defsOf();
    expect(editable(defs[2], read)).toBe(true);
    expect(editable(defs[1], read)).toBe(false);
    expect(editable(defs[1], newRow)).toBe(true);
    const flag = editedColumnDefsOf(
      schemaOf([columnOf('ok', 'boolean', { canUpdate: true })]),
      linkedColumnDefsOf(schemaOf([columnOf('ok', 'boolean')]), null, true),
      editsOf(new Map()).edits,
    )[1];
    expect(flag.cellEditor).toBe('agSelectCellEditor');
    expect(flag.cellEditorParams.values).toEqual([true, false, null]);
    expect(flag.cellEditorParams.formatValue(null)).toBe('NULL');
    const day = editedColumnDefsOf(
      schemaOf([columnOf('on', 'date')]),
      linkedColumnDefsOf(schemaOf([columnOf('on', 'date')]), null, true),
      editsOf(new Map()).edits,
    )[1];
    expect(day.cellEditor).toBe('agDateStringCellEditor');
    const selector = defs[2].cellEditorSelector as (p: { value: unknown }) => { component: string };
    expect(selector({ value: 'short' }).component).toBe('agTextCellEditor');
    expect(selector({ value: 'a\nb' }).component).toBe('agLargeTextCellEditor');
    expect(defs[3].cellEditor).toBe('agTextCellEditor');
  });

  it('deletes rows (Ctrl+Delete) and reverts cells (Ctrl+Z), but not while editing', () => {
    const { defs, asked } = defsOf();
    const key = (def: ColDef<GridRow>, init: KeyboardEventInit, editing = false) => {
      const event = new KeyboardEvent('keydown', { cancelable: true, ...init });
      const suppressed = def.suppressKeyboardEvent?.({
        event,
        editing,
        data: read,
      } as SuppressKeyboardEventParams<GridRow>);
      return { suppressed, prevented: event.defaultPrevented };
    };
    expect(key(defs[2], { key: 'Delete', ctrlKey: true })).toEqual({
      suppressed: true,
      prevented: true,
    });
    expect(key(defs[0], { key: 'Backspace', metaKey: true }).suppressed).toBe(true);
    expect(asked.toggled).toEqual([read, read]);
    expect(key(defs[2], { key: 'z', ctrlKey: true }).suppressed).toBe(true);
    expect(asked.reverted).toEqual([[read, 1]]);
    // Not on the change's column, nor while editing, nor with other keys.
    expect(key(defs[0], { key: 'z', ctrlKey: true }).suppressed).toBe(false);
    expect(key(defs[2], { key: 'z', ctrlKey: true }, true).suppressed).toBe(false);
    expect(key(defs[2], { key: 'Delete', ctrlKey: true, shiftKey: true }).suppressed).toBe(false);
    expect(key(defs[2], { key: 'Delete' }).suppressed).toBe(false);
  });
});

describe('references edited', () => {
  const columns = linkedColumns().map((column, index) =>
    index === 1 ? { ...column, canUpdate: true } : column,
  );
  const schema: LinkSchema = { columns, references: [customerReference], collections: [] };
  const read = { ...rowOf(['1001', '42', 'open']), r: ['Acme'] };

  function defsOf(changes = new Map<GridRow, PendingChange>()) {
    const { edits, asked } = editsOf(changes);
    const linked = linkedColumnDefsOf(schema, null, true, referenceChangesOf(schema, edits));
    return { defs: editedColumnDefsOf(schema, linked, edits), asked };
  }

  const key = (def: ColDef<GridRow>, data: GridRow, init: KeyboardEventInit) =>
    def.suppressKeyboardEvent?.({
      event: new KeyboardEvent('keydown', init),
      editing: false,
      data,
    } as SuppressKeyboardEventParams<GridRow>);

  it('chooses the row a reference refers to (F2), rather than editing its value', () => {
    const { defs, asked } = defsOf();
    expect((defs[2].editable as (p: { data: GridRow }) => boolean)({ data: read })).toBe(false);
    expect(key(defs[2], read, { key: 'F2' })).toBe(true);
    expect(asked.picked).toEqual([[read, 0]]);
  });

  it('sets a reference that may refer to none to NULL (Delete), and says so of one that may not', () => {
    const { defs, asked } = defsOf();
    expect(key(defs[2], read, { key: 'Delete' })).toBe(true);
    // What was shown for the row it referred to goes too.
    expect(asked.set).toEqual([[read, new Map([[1, null]]), { customer: null }]]);
    // Backspace too, as on macOS for other cells.
    expect(key(defs[2], read, { key: 'Backspace' })).toBe(true);
    expect(asked.set.length).toBe(2);

    const required = linkedColumns().map((column, index) =>
      index === 1
        ? {
            ...column,
            canUpdate: true,
            type: { kind: 'int64' as const, nullable: false, text: 'int64' },
          }
        : column,
    );
    const strict: LinkSchema = { ...schema, columns: required };
    const { edits, asked: strictAsked } = editsOf(new Map());
    const strictDefs = editedColumnDefsOf(strict, linkedColumnDefsOf(strict, null, true), edits);
    expect(key(strictDefs[2], read, { key: 'Delete' })).toBe(true);
    expect(strictAsked.refused).toEqual([['customer_id', "'customer_id' can't be NULL"]]);
  });

  it("follows a reference's link on Enter, but not a reference changed", () => {
    const followed: unknown[] = [];
    const links = { href: () => '/x', follow: (link: unknown) => followed.push(link) };
    const changed = { ...rowOf(['1002', '42', 'open']), r: ['Acme'] };
    const change = changeOf({
      key: ['1002'],
      rowId: '["1002"]',
      values: { customer_id: '43' },
      original: { customer_id: '42' },
    });
    const { edits } = editsOf(new Map([[changed, change]]));
    const linked = linkedColumnDefsOf(schema, links, true, referenceChangesOf(schema, edits));
    const defs = editedColumnDefsOf(schema, linked, edits);
    expect(key(defs[2], read, { key: 'Enter' })).toBe(true);
    expect(followed).toEqual([{ navigation: 'customer', row: ['1001'] }]);
    expect(key(defs[2], changed, { key: 'Enter' })).toBe(false);
    expect(followed.length).toBe(1);
  });

  it("doesn't choose a reference whose columns aren't all the grid's", () => {
    const partial: LinkSchema = {
      ...schema,
      references: [{ ...customerReference, complete: false }],
    };
    const { edits, asked } = editsOf(new Map());
    const defs = editedColumnDefsOf(partial, linkedColumnDefsOf(partial, null, true), edits);
    expect(key(defs[2], read, { key: 'F2' })).toBe(false);
    expect(key(defs[2], read, { key: 'Delete' })).toBe(false);
    expect(asked.picked).toEqual([]);
  });

  it("leaves keys alone where a reference can't be changed", () => {
    const changes = new Map<GridRow, PendingChange>([[read, changeOf({ kind: 'delete' })]]);
    const { defs, asked } = defsOf(changes);
    expect(key(defs[2], read, { key: 'F2' })).toBe(false);
    expect(asked.picked).toEqual([]);
  });

  it('shows a reference changed by the display value given with it, without a link', () => {
    const change = changeOf({
      values: { customer_id: '43' },
      original: { customer_id: '42' },
      display: { customer: 'Beta' },
    });
    const { defs } = defsOf(new Map([[read, change]]));
    const content = defs[2].cellRendererParams.content({
      data: read,
      value: '43',
      valueFormatted: '43',
    } as ICellRendererParams<GridRow>);
    expect(content).toEqual({ text: 'Beta', link: null, aside: '43', arrow: false });
  });
});

describe('the rows of changes', () => {
  it('marks rows to be deleted, and new rows', () => {
    const read = rowOf(['1001', 'open', '1']);
    const { edits } = editsOf(new Map([[read, changeOf({ kind: 'delete' })]]));
    const rules = rowClassRulesOf(edits);
    expect(rules['gd-deleted']({ data: read })).toBe(true);
    expect(rules['gd-inserted']({ data: read })).toBe(false);
    expect(rules['gd-inserted']({ data: newRow })).toBe(true);
    expect(rules['gd-deleted']({})).toBe(false);
    expect(rowClassRulesOf(edits)).not.toBe(rules);
  });

  it("draws and says each row's change", () => {
    const cell = new StateCell();
    const eGridCell = document.createElement('div');
    cell.init({ value: 'new', eGridCell } as unknown as ICellRendererParams<GridRow>);
    const element = cell.getGui();
    expect(element.querySelector('[aria-hidden="true"]')?.textContent).toBe('add');
    expect(element.textContent).toBe('addNew row');
    expect(
      cell.refresh({ value: 'deleted', eGridCell } as unknown as ICellRendererParams<GridRow>),
    ).toBe(true);
    expect(cell.getGui()).toBe(element);
    expect(element.textContent).toBe('deleteTo be deleted');
    cell.refresh({ value: null, eGridCell } as unknown as ICellRendererParams<GridRow>);
    expect(element.textContent).toBe('');
  });
});
