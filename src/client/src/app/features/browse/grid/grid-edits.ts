import type {
  CellClassParams,
  ColDef,
  ICellRendererComp,
  ICellRendererParams,
  SuppressKeyboardEventParams,
  ValueSetterParams,
} from 'ag-grid-community';
import type { Schema } from '../../../core/api/api-client';
import { type PendingChange, type Values, sameValue } from '../../../core/changes/pending-changes';
import {
  type GridColumn,
  type GridRow,
  type TypeDto,
  cellText,
  indexOfColId,
} from './grid-columns';
import {
  type GridReference,
  type LinkSchema,
  type ReferenceChanges,
  referenceOf,
} from './grid-links';

export type Capabilities = Schema<'CapabilitiesDto'>;

/** A row of the grid: one that is there (read from the server), or a new one (by its temporary id). */
export interface EditRow extends GridRow {
  readonly tempId?: string;
}

/** What a row's change makes of it: new, to be deleted, changed, or none. */
export type RowState = 'new' | 'deleted' | 'changed' | null;

/** The id of the column that says each row's change. */
export const stateColId = 'gd-state';

/** Editing the rows: their changes, and changing them, as the grid that shows them does. */
export interface GridEdits {
  readonly capabilities: Capabilities;
  /** The row's change, if it has one. */
  changeOf(row: GridRow): PendingChange | null;
  /** Sets columns' values (by their places) of a row, with display values of the rows navigations then lead to. */
  set(row: GridRow, values: ReadonlyMap<number, unknown>, display?: Values): void;
  /** Says why a value given can't be (it isn't sent). */
  refused(row: GridRow, column: GridColumn, reason: string): void;
  /** Chooses the row a reference's columns refer to (by the reference's place). */
  pick(row: GridRow, reference: number): void;
  /** Deletes a row, or restores it when it is to be deleted (a new row is dropped). */
  toggleDelete(row: GridRow): void;
  /** Reverts a cell's change (a reference's, all its columns'), or restores the row when it is to be deleted. */
  revertCell(row: GridRow, index: number): void;
}

const absent = Symbol('absent');

/** Whether a row is a new one. */
export function isNew(row: GridRow | null | undefined): row is EditRow & { tempId: string } {
  return !!(row as EditRow | null | undefined)?.tempId;
}

/** A change's value for a column: by its name, or so but for case when that is one; `absent` when it has none. */
function changedValue(change: PendingChange | null, name: string): unknown {
  if (!change) {
    return absent;
  }
  const values = change.values;
  if (name in values) {
    return values[name];
  }
  const found = Object.keys(values).filter((key) => key.toLowerCase() === name.toLowerCase());
  return found.length === 1 ? values[found[0]] : absent;
}

/** Whether a change has a value for a column. */
export function hasValue(change: PendingChange | null, name: string): boolean {
  return changedValue(change, name) !== absent;
}

/** A change's original for a column (the value the row had when it was first changed); `undefined` when none. */
export function originalOf(change: PendingChange | null, name: string): unknown {
  if (!change) {
    return undefined;
  }
  if (name in change.original) {
    return change.original[name];
  }
  const found = Object.keys(change.original).filter(
    (key) => key.toLowerCase() === name.toLowerCase(),
  );
  return found.length === 1 ? change.original[found[0]] : undefined;
}

/**
 * A cell's value as it will be: its change's, else the row's as read; for a new row, its change's, else undefined
 * (the column's default).
 */
export function shownValue(
  column: GridColumn,
  index: number,
  row: GridRow,
  change: PendingChange | null,
): unknown {
  const changed = changedValue(change, column.name);
  if (changed !== absent) {
    return changed;
  }
  return isNew(row) ? undefined : row.v[index];
}

/**
 * Whether a row was changed elsewhere since a column of it was changed here (or since it was deleted here): the
 * value read now isn't the original kept. The commit would find so too, and change nothing.
 */
export function conflicts(
  column: GridColumn,
  index: number,
  row: GridRow,
  change: PendingChange | null,
): boolean {
  if (!change || isNew(row)) {
    return false;
  }
  const original = originalOf(change, column.name);
  return original !== undefined && !sameValue(original, row.v[index]);
}

/** What a row's change makes of it. */
export function rowStateOf(row: GridRow, change: PendingChange | null): RowState {
  if (isNew(row)) {
    return 'new';
  }
  switch (change?.kind) {
    case 'delete':
      return 'deleted';
    case 'update':
      return 'changed';
    default:
      return null;
  }
}

/** Whether a cell may be given a value: a new row's, or a row's that is there (not to be deleted), as allowed. */
export function cellEditable(
  column: GridColumn,
  row: GridRow | null | undefined,
  edits: GridEdits,
): boolean {
  if (!row) {
    return false;
  }
  if (isNew(row)) {
    return edits.capabilities.canInsert && column.insert !== 'never';
  }
  return (
    !!row.id &&
    edits.capabilities.canUpdate &&
    column.canUpdate &&
    edits.changeOf(row)?.kind !== 'delete'
  );
}

/** Whether the entity takes any change: new rows, changes to rows, or deletes. */
export function takesChanges(capabilities: Capabilities): boolean {
  return capabilities.canInsert || capabilities.canUpdate || capabilities.canDelete;
}

/** A value given for a column (typed, chosen, or none), as it is sent; or why it can't be. */
export type Parsed = { readonly value: unknown } | { readonly problem: string };

/**
 * A value given for a column, as it is sent: text as typed, but for numbers that are JSON's (whole numbers to
 * 32 bits, doubles), booleans, and date-times with a `T`. Nothing given (no text, or Delete) is NULL, but for text,
 * where empty text is a value. What isn't a value of the column's type is said, as the server would.
 */
export function parsedValue(given: unknown, column: GridColumn): Parsed {
  const type = column.type;
  if (typeof given === 'boolean') {
    return type.kind === 'boolean' ? { value: given } : parsedValue(String(given), column);
  }
  if (typeof given === 'number') {
    return parsedValue(String(given), column);
  }
  const text = typeof given === 'string' ? given : null;
  if (text === null || (text.trim() === '' && !isText(type))) {
    // Values of types the language hasn't may always be NULL, as the server takes them.
    return type.nullable || type.kind === 'unknown'
      ? { value: null }
      : { problem: `'${column.name}' can't be NULL` };
  }
  if (isText(type)) {
    return { value: text };
  }
  const trimmed = text.trim();
  const wrong = (what: string): Parsed => ({ problem: `${shortened(trimmed)} isn't ${what}` });
  switch (type.kind) {
    case 'boolean':
      return /^(true|false)$/i.test(trimmed)
        ? { value: trimmed.toLowerCase() === 'true' }
        : wrong('true or false');
    case 'int16':
    case 'int32': {
      const limit = type.kind === 'int16' ? 2 ** 15 : 2 ** 31;
      const value = /^[+-]?\d+$/.test(trimmed) ? Number(trimmed) : NaN;
      if (Number.isNaN(value)) {
        return wrong('a whole number');
      }
      return value < -limit || value >= limit
        ? { problem: `${shortened(trimmed)} is out of range (${-limit} to ${limit - 1})` }
        : { value };
    }
    case 'int64': {
      if (!/^[+-]?\d+$/.test(trimmed)) {
        return wrong('a whole number');
      }
      const value = BigInt(trimmed);
      return value < -(2n ** 63n) || value >= 2n ** 63n
        ? { problem: `${shortened(trimmed)} is out of range` }
        : { value: value.toString() };
    }
    case 'decimal':
      return decimalOf(trimmed, type) ?? wrong('a number');
    case 'single':
    case 'double':
      if (/^(NaN|-?Infinity)$/.test(trimmed)) {
        return { value: trimmed };
      }
      if (
        !/^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/.test(trimmed) ||
        !Number.isFinite(Number(trimmed))
      ) {
        return wrong('a number');
      }
      // A single holds less: past its range, it would be infinite.
      return type.kind === 'single' && Math.abs(Number(trimmed)) > singleLimit
        ? { problem: `${shortened(trimmed)} is out of range for a single` }
        : { value: Number(trimmed) };
    case 'date':
      return validDate(trimmed) ? { value: trimmed } : wrong('a date (yyyy-mm-dd)');
    case 'dateTime':
      return dateTimeOf(trimmed, false) ?? wrong('a date-time (yyyy-mm-dd hh:mm:ss)');
    case 'dateTimeOffset':
      return (
        dateTimeOf(trimmed, true) ?? wrong('a date-time with an offset (yyyy-mm-dd hh:mm:ss+02:00)')
      );
    case 'time':
      return /^([01]\d|2[0-3]):[0-5]\d(:[0-5]\d(\.\d{1,7})?)?$/.test(trimmed)
        ? { value: trimmed }
        : wrong('a time (hh:mm:ss)');
    case 'interval':
      // As .NET reads intervals: [-][d.]h:mm[:ss[.fffffff]], or days alone.
      return /^-?(\d+\.)?([01]?\d|2[0-3]):[0-5]?\d(:[0-5]?\d(\.\d{1,7})?)?$|^-?\d+$/.test(trimmed)
        ? { value: trimmed }
        : wrong('an interval (d.hh:mm:ss)');
    case 'guid':
      return guidPattern.test(trimmed) ? { value: trimmed } : wrong('a guid');
    default:
      return { value: text };
  }
}

/** The largest single. */
const singleLimit = 3.4028235e38;

/** Guids as the server reads them: with or without hyphens, in braces or brackets. */
const guidPattern =
  /^(\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}|\([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)|[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12})$/i;

/** Whether a column's values are text as typed: text, JSON, and values of types the language hasn't. */
function isText(type: TypeDto): boolean {
  return type.kind === 'string' || type.kind === 'json' || type.kind === 'unknown';
}

function shortened(text: string): string {
  return text.length > 40 ? `${text.slice(0, 37)}...` : text;
}

/** A decimal as text (no thousands separators), within the column's precision and scale when they are known. */
function decimalOf(text: string, type: TypeDto): Parsed | null {
  const match = /^([+-]?)(\d*)(?:\.(\d*))?$/.exec(text);
  if (!match || (match[2] === '' && (match[3] ?? '') === '')) {
    return null;
  }
  const whole = match[2].replace(/^0+(?=\d)/, '');
  const fraction = (match[3] ?? '').replace(/0+$/, '');
  const { precision, scale } = type;
  if (scale !== null && scale !== undefined && fraction.length > scale) {
    return {
      problem: `${shortened(text)} has more than ${scale} ${scale === 1 ? 'place' : 'places'} after the point`,
    };
  }
  if (
    precision !== null &&
    precision !== undefined &&
    whole.replace(/^0$/, '').length > precision - (scale ?? 0)
  ) {
    return { problem: `${shortened(text)} is too large for ${type.text.replace(/\?$/, '')}` };
  }
  return { value: text };
}

function validDate(text: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (!match) {
    return false;
  }
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(2000, month - 1, day));
  date.setUTCFullYear(year);
  return (
    year >= 1 &&
    date.getUTCFullYear() === year &&
    date.getUTCMonth() === month - 1 &&
    date.getUTCDate() === day
  );
}

/** A date-time as ISO text (`T` between the date and the time), the seconds and an offset as given. */
function dateTimeOf(text: string, offset: boolean): Parsed | null {
  const zone = offset ? '(Z|[+-](0\\d|1[0-4]):[0-5]\\d)?' : '';
  const match = new RegExp(
    `^(\\d{4}-\\d{2}-\\d{2})(?:[ T]([01]\\d|2[0-3]):([0-5]\\d)(?::([0-5]\\d)(?:\\.(\\d{1,7}))?)?${zone})?$`,
  ).exec(text);
  if (!match || !validDate(match[1])) {
    return null;
  }
  return { value: text.replace(' ', 'T') };
}

/** The cell editor a column's values are edited with: a choice for booleans, a date's, else text. */
function editorOf(column: GridColumn): Partial<ColDef<GridRow>> {
  switch (column.type.kind) {
    case 'boolean':
      return {
        cellEditor: 'agSelectCellEditor',
        cellEditorParams: {
          values: column.type.nullable ? [true, false, null] : [true, false],
          formatValue: (value: unknown) => (value === null ? 'NULL' : String(value)),
        },
      };
    case 'date':
      return { cellEditor: 'agDateStringCellEditor' };
    case 'string':
      // Long text, or text with lines, in a larger editor over the grid.
      return {
        cellEditorSelector: ({ value }) =>
          typeof value === 'string' && (value.length > 80 || value.includes('\n'))
            ? {
                component: 'agLargeTextCellEditor',
                popup: true,
                params: { maxLength: 1_000_000, rows: 8, cols: 60 },
              }
            : { component: 'agTextCellEditor' },
      };
    default:
      return { cellEditor: 'agTextCellEditor' };
  }
}

/** What a cell says when pointed at: why it is marked (changed, changed elsewhere, needing a value). */
function tooltipOf(
  column: GridColumn,
  index: number,
  row: GridRow,
  change: PendingChange | null,
): string | undefined {
  if (isNew(row)) {
    if (!hasValue(change, column.name)) {
      return column.insert === 'required'
        ? 'Needs a value'
        : column.insert === 'never'
          ? 'The database gives it a value'
          : "The column's default";
    }
    return undefined;
  }
  if (conflicts(column, index, row, change)) {
    return `Changed elsewhere since it was changed here: it was ${cellText(originalOf(change, column.name), column.type)}, it is ${cellText(row.v[index], column.type)}`;
  }
  if (hasValue(change, column.name)) {
    return `Changed from ${cellText(originalOf(change, column.name), column.type)}`;
  }
  return undefined;
}

/**
 * The grid's columns with their changes: values as they will be (a change's, else the row's), marked when changed,
 * changed elsewhere since, or needed in a new row; edited by their types' editors, as the entity and its columns
 * allow (a reference's by choosing the row it refers to, F2); and a column before them saying each row's change.
 * Keys: Ctrl+Delete deletes the row (or restores it, or drops a new one), Ctrl+Z reverts the cell's change (or
 * restores the row), Delete sets the cell to NULL.
 */
export function editedColumnDefsOf(
  schema: LinkSchema,
  defs: readonly ColDef<GridRow>[],
  edits: GridEdits,
): ColDef<GridRow>[] {
  const columns = defs.map((def): ColDef<GridRow> => {
    const index = indexOfColId(def.colId ?? '');
    const column = schema.columns[index];
    const keys = editingKeys(schema, index, edits);
    const following = def.suppressKeyboardEvent;
    const suppressKeyboardEvent = (params: SuppressKeyboardEventParams<GridRow>) =>
      keys(params) || (following?.(params) ?? false);
    if (!column) {
      return { ...def, suppressKeyboardEvent };
    }
    const reference = referenceOf(schema, index);
    const changeOf = (row: GridRow | undefined) => (row ? edits.changeOf(row) : null);
    return {
      ...def,
      valueGetter: ({ data }) =>
        data ? shownValue(column, index, data, edits.changeOf(data)) : undefined,
      valueFormatter: ({ value, data }) =>
        isNew(data) && value === undefined
          ? column.insert === 'required'
            ? ''
            : 'DEFAULT'
          : cellText(value, column.type),
      editable: ({ data }) => reference === null && cellEditable(column, data, edits),
      valueSetter: (params: ValueSetterParams<GridRow>) => setValue(params, column, index, edits),
      ...editorOf(column),
      cellClassRules: {
        ...def.cellClassRules,
        'gd-null': ({ value }: CellClassParams<GridRow>) => value === null,
        'gd-default': ({ value, data }: CellClassParams<GridRow>) =>
          isNew(data) && value === undefined && column.insert !== 'required',
        'gd-dirty': ({ data }: CellClassParams<GridRow>) => hasValue(changeOf(data), column.name),
        'gd-conflict': ({ data }: CellClassParams<GridRow>) =>
          !!data && conflicts(column, index, data, changeOf(data)),
        'gd-invalid': ({ data }: CellClassParams<GridRow>) =>
          isNew(data) && column.insert === 'required' && !hasValue(changeOf(data), column.name),
      },
      tooltip: ({ data }) =>
        data ? tooltipOf(column, index, data, edits.changeOf(data)) : undefined,
      suppressKeyboardEvent,
    };
  });
  return [stateColumnDef(edits), ...columns];
}

/** The references changed in rows: those whose columns have values not committed, with the display value given. */
export function referenceChangesOf(schema: LinkSchema, edits: GridEdits): ReferenceChanges {
  return (row, reference) => {
    const change = edits.changeOf(row);
    const changed = reference.columns.some((index) =>
      hasValue(change, schema.columns[index]?.name ?? ''),
    );
    return changed ? { display: change?.display[reference.navigation] ?? null } : null;
  };
}

/** The rows' class rules: rows to be deleted, and new rows (a fresh object, so the grid applies them again). */
export function rowClassRulesOf(edits: GridEdits) {
  return {
    'gd-deleted': ({ data }: { data?: GridRow }) =>
      !!data && edits.changeOf(data)?.kind === 'delete',
    'gd-inserted': ({ data }: { data?: GridRow }) => isNew(data),
  };
}

/** Sets a cell's value from its editor: refused, and said, when it isn't one of its type's. */
function setValue(
  params: ValueSetterParams<GridRow>,
  column: GridColumn,
  index: number,
  edits: GridEdits,
): boolean {
  const row = params.data;
  if (!row) {
    return false;
  }
  const parsed = parsedValue(params.newValue, column);
  if ('problem' in parsed) {
    edits.refused(row, column, parsed.problem);
    return false;
  }
  if (sameValue(parsed.value, params.oldValue) && params.oldValue !== undefined) {
    return false;
  }
  edits.set(row, new Map([[index, parsed.value]]));
  return true;
}

/** The keys that change rows, on a cell of the column at `index` (-1 for others). */
function editingKeys(
  schema: LinkSchema,
  index: number,
  edits: GridEdits,
): (params: SuppressKeyboardEventParams<GridRow>) => boolean {
  const reference = referenceOf(schema, index);
  return ({ event, editing, data }) => {
    if (editing || event.type !== 'keydown' || !data) {
      return false;
    }
    const command = (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey;
    const plain = !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey;
    if (command && (event.key === 'Delete' || event.key === 'Backspace')) {
      event.preventDefault();
      edits.toggleDelete(data);
      return true;
    }
    if (command && event.key.toLowerCase() === 'z' && index >= 0) {
      event.preventDefault();
      edits.revertCell(data, index);
      return true;
    }
    // Delete clears a reference; Backspace too, as it does other cells on macOS.
    const clears = event.key === 'Delete' || event.key === 'Backspace';
    if (reference && plain && (event.key === 'F2' || clears)) {
      const at = schema.references.indexOf(reference);
      if (!referenceEditable(schema, reference, data, edits)) {
        return false;
      }
      event.preventDefault();
      if (event.key === 'F2') {
        edits.pick(data, at);
      } else {
        clearReference(schema, reference, data, edits);
      }
      return true;
    }
    return false;
  };
}

/** A reference set to refer to no row: its columns NULL, as each may be. */
function clearReference(
  schema: LinkSchema,
  reference: GridReference,
  row: GridRow,
  edits: GridEdits,
): void {
  const notNull = reference.columns
    .map((index) => schema.columns[index])
    .find((column) => !column.type.nullable);
  if (notNull) {
    edits.refused(row, notNull, `'${notNull.name}' can't be NULL`);
    return;
  }
  edits.set(row, new Map(reference.columns.map((index) => [index, null])), {
    [reference.navigation]: null,
  });
}

/**
 * Whether a row's reference may be set (by choosing the row it refers to, or none): all the columns that hold it
 * are the grid's, and each may be given a value.
 */
export function referenceEditable(
  schema: LinkSchema,
  reference: GridReference,
  row: GridRow | null | undefined,
  edits: GridEdits,
): boolean {
  return (
    reference.complete &&
    reference.columns.every((column) => cellEditable(schema.columns[column], row, edits))
  );
}

const stateLabels: Readonly<Record<Exclude<RowState, null>, string>> = {
  new: 'New row',
  deleted: 'To be deleted',
  changed: 'Changed',
};

const stateIcons: Readonly<Record<Exclude<RowState, null>, string>> = {
  new: 'add',
  deleted: 'delete',
  changed: 'edit',
};

/** The column that says each row's change: new, to be deleted, or changed. */
function stateColumnDef(edits: GridEdits): ColDef<GridRow> {
  return {
    colId: stateColId,
    headerName: '',
    headerTooltip: "The row's change",
    pinned: 'left',
    lockPinned: true,
    lockPosition: 'left',
    suppressMovable: true,
    resizable: false,
    sortable: false,
    filter: false,
    width: 44,
    minWidth: 44,
    valueGetter: ({ data }) => (data ? rowStateOf(data, edits.changeOf(data)) : undefined),
    tooltip: ({ value }) => (value ? stateLabels[value as Exclude<RowState, null>] : undefined),
    cellRenderer: StateCell,
    suppressKeyboardEvent: editingKeys({ columns: [], references: [], collections: [] }, -1, edits),
  };
}

/** A row's change, drawn (an icon) and said (its words, for screen readers). */
export class StateCell implements ICellRendererComp<GridRow> {
  private element!: HTMLElement;

  init(params: ICellRendererParams<GridRow>): void {
    this.element = params.eGridCell.ownerDocument.createElement('span');
    this.element.className = 'gd-state';
    this.show(params);
  }

  getGui(): HTMLElement {
    return this.element;
  }

  refresh(params: ICellRendererParams<GridRow>): boolean {
    this.show(params);
    return true;
  }

  private show(params: ICellRendererParams<GridRow>): void {
    const state = params.value as RowState | undefined;
    const document = this.element.ownerDocument;
    this.element.replaceChildren();
    if (!state) {
      return;
    }
    const icon = document.createElement('span');
    icon.className = `gd-state-icon gd-state-${state}`;
    icon.setAttribute('aria-hidden', 'true');
    icon.textContent = stateIcons[state];
    const label = document.createElement('span');
    label.className = 'cdk-visually-hidden';
    label.textContent = stateLabels[state];
    this.element.append(icon, label);
  }
}
