import type {
  ColDef,
  ColumnState,
  FilterModel,
  IFilterOptionDef,
  SortModelItem,
} from 'ag-grid-community';
import type { Schema } from '../../../core/api/api-client';
import {
  type GridCondition,
  type GridFilter,
  type GridOp,
  type GridSort,
  opValues,
} from '../../../core/browse/browse-url';

export type GridColumn = Schema<'GridColumnDto'>;
export type GridRow = Schema<'GridRowDto'>;
export type TypeDto = Schema<'TypeDto'>;
export type Lineage = Schema<'LineageDto'>;

/**
 * How a column is filtered, by its type: text (with what it holds, starts and ends with); numbers the browser holds
 * exactly; whole numbers past 2^53 and decimals, as text; dates (a date stands for its day in a date-time); times and
 * intervals, compared as text; booleans; guids, equal or not; and the rest, present or not.
 */
export type FilterKind =
  | 'text'
  | 'number'
  | 'bigint'
  | 'decimal'
  | 'date'
  | 'ordered'
  | 'boolean'
  | 'equality'
  | 'presence';

export function filterKindOf(type: TypeDto): FilterKind {
  switch (type.kind) {
    case 'string':
      return 'text';
    case 'int16':
    case 'int32':
    case 'single':
    case 'double':
      return 'number';
    case 'int64':
      return 'bigint';
    case 'decimal':
      return 'decimal';
    case 'date':
    case 'dateTime':
    case 'dateTimeOffset':
      return 'date';
    case 'time':
    case 'interval':
      return 'ordered';
    case 'boolean':
      return 'boolean';
    case 'guid':
      return 'equality';
    default:
      return 'presence';
  }
}

/** Whether rows may be sorted by a column: not by binary, JSON or unknown values, which databases may not order. */
export function sortable(type: TypeDto): boolean {
  return !['binary', 'json', 'unknown'].includes(type.kind);
}

/** The grid's filter of each kind. */
const filters: Readonly<Record<FilterKind, string>> = {
  text: 'agTextColumnFilter',
  number: 'agNumberColumnFilter',
  bigint: 'agBigIntColumnFilter',
  decimal: 'agTextColumnFilter',
  date: 'agDateColumnFilter',
  ordered: 'agTextColumnFilter',
  boolean: 'agTextColumnFilter',
  equality: 'agTextColumnFilter',
  presence: 'agTextColumnFilter',
};

/** What the grid writes in its filter models, for each kind (its text filter's, for those it filters as text). */
const filterTypes: Readonly<Record<FilterKind, 'text' | 'number' | 'bigint' | 'date'>> = {
  text: 'text',
  number: 'number',
  bigint: 'bigint',
  decimal: 'text',
  date: 'date',
  ordered: 'text',
  boolean: 'text',
  equality: 'text',
  presence: 'text',
};

/**
 * An option of our own. The server filters, so its predicate (which the grid wants, to filter rows it holds) is
 * never asked.
 */
function option(
  displayKey: string,
  displayName: string,
  numberOfInputs: 0 | 1 | 2,
): IFilterOptionDef {
  return { displayKey, displayName, numberOfInputs, predicate: () => true };
}

/** Options of our own, for comparisons the grid's text filter hasn't: decimals, times and intervals. */
const compared: readonly IFilterOptionDef[] = [
  option('gdLt', 'Less than', 1),
  option('gdLe', 'Less than or equal to', 1),
  option('gdGt', 'Greater than', 1),
  option('gdGe', 'Greater than or equal to', 1),
  option('gdBetween', 'Between', 2),
];

const truth: readonly IFilterOptionDef[] = [
  option('gdTrue', 'True', 0),
  option('gdFalse', 'False', 0),
];

const presence = ['blank', 'notBlank'];
const scalar = [
  'equals',
  'notEqual',
  'lessThan',
  'lessThanOrEqual',
  'greaterThan',
  'greaterThanOrEqual',
  'inRange',
  ...presence,
];

/** The options each kind's filter offers, in order. */
const options: Readonly<Record<FilterKind, readonly (string | IFilterOptionDef)[]>> = {
  text: ['contains', 'notContains', 'equals', 'notEqual', 'startsWith', 'endsWith', ...presence],
  number: scalar,
  bigint: scalar,
  decimal: ['equals', 'notEqual', ...compared, ...presence],
  date: scalar,
  ordered: ['equals', 'notEqual', ...compared, ...presence],
  boolean: [...truth, ...presence],
  equality: ['equals', 'notEqual', ...presence],
  presence,
};

/** The operation each of the grid's options (and ours) is. */
const opsOfOptions: Readonly<Record<string, GridOp>> = {
  equals: 'eq',
  notEqual: 'ne',
  lessThan: 'lt',
  lessThanOrEqual: 'le',
  greaterThan: 'gt',
  greaterThanOrEqual: 'ge',
  inRange: 'between',
  contains: 'contains',
  notContains: 'notContains',
  startsWith: 'startsWith',
  endsWith: 'endsWith',
  blank: 'blank',
  notBlank: 'notBlank',
  gdLt: 'lt',
  gdLe: 'le',
  gdGt: 'gt',
  gdGe: 'ge',
  gdBetween: 'between',
};

/** How many conditions a column's filter holds. */
export const conditionsPerFilter = 2;

/** The id of a column in the grid, by its place: names may be any text. */
export function colIdOf(index: number): string {
  return `c${index}`;
}

/** The place of a column the grid names by its id. */
export function indexOfColId(colId: string): number {
  return /^c\d+$/.test(colId) ? Number(colId.slice(1)) : -1;
}

/** The column named so, or the one named so but for case (as the server finds them); -1 when there is none. */
export function indexOfColumn(columns: readonly GridColumn[], name: string): number {
  const exact = columns.findIndex((column) => column.name === name);
  if (exact >= 0) {
    return exact;
  }
  const lower = name.toLowerCase();
  const found = columns
    .map((column, index) => (column.name.toLowerCase() === lower ? index : -1))
    .filter((index) => index >= 0);
  return found.length === 1 ? found[0] : -1;
}

/** The grid's columns: each by its place, its values from the row's, filtered and sorted as its type allows. */
export function columnDefsOf(columns: readonly GridColumn[]): ColDef<GridRow>[] {
  return columns.map((column, index) => {
    const kind = filterKindOf(column.type);
    return {
      colId: colIdOf(index),
      headerName: column.name,
      headerTooltip: headerTooltipOf(column),
      headerClass: column.isKey ? 'gd-key-column' : undefined,
      valueGetter: ({ data }) => (data ? data.v[index] : undefined),
      valueFormatter: ({ value }) => cellText(value, column.type),
      cellClass: alignedRight(column.type) ? 'gd-number' : undefined,
      cellClassRules: { 'gd-null': ({ value }) => value === null },
      sortable: sortable(column.type),
      filter: filters[kind],
      filterParams: {
        filterOptions: options[kind],
        maxNumConditions: conditionsPerFilter,
        buttons: ['reset', 'apply'],
        closeOnApply: true,
        ...(kind === 'date'
          ? { browserDatePicker: true, minValidYear: 1, maxValidYear: 9999 }
          : {}),
        ...(kind === 'ordered'
          ? { filterPlaceholder: column.type.kind === 'time' ? 'hh:mm:ss' : 'd.hh:mm:ss' }
          : {}),
      },
      width: widthOf(column.type),
    } satisfies ColDef<GridRow>;
  });
}

/** What a column's header says when pointed at or focused: its name, type and where its values come from. */
export function headerTooltipOf(column: GridColumn): string {
  const type = column.isKey ? `${column.type.text}, the key` : column.type.text;
  return `${column.name}: ${type}. ${lineageText(column.lineage)}`;
}

/** Where a column's values come from, in a sentence. */
export function lineageText(lineage: Lineage): string {
  const sources = lineage.sources
    .map((source) => (source.path ? `${source.column} (through ${source.path})` : source.column))
    .join(', ');
  switch (lineage.kind) {
    case 'direct':
      return `From ${sources}.`;
    case 'computed':
      return workedOut('Worked out', lineage.expression, sources);
    case 'aggregated':
      return workedOut('Aggregated', lineage.expression, sources);
    case 'constant':
      return lineage.expression ? `A constant: ${lineage.expression}.` : 'A constant.';
    case 'union':
      return `From ${sources}, put together.`;
    default:
      return "Where its values come from isn't known.";
  }
}

/** A value worked out: by what expression, from which columns, as far as they are known. */
function workedOut(how: string, expression: string | null, sources: string): string {
  if (expression && sources) {
    return `${how}: ${expression}, from ${sources}.`;
  }
  return expression || sources ? `${how}: ${expression || sources}.` : `${how}.`;
}

function alignedRight(type: TypeDto): boolean {
  return ['int16', 'int32', 'int64', 'decimal', 'single', 'double'].includes(type.kind);
}

function widthOf(type: TypeDto): number {
  switch (filterKindOf(type)) {
    case 'boolean':
      return 110;
    case 'number':
    case 'bigint':
    case 'decimal':
    case 'ordered':
      return 130;
    case 'date':
      return type.kind === 'date' ? 130 : 200;
    case 'equality':
      return 300;
    default:
      return 200;
  }
}

/** A value as a cell shows it: NULL for null, nothing for a row not loaded yet. */
export function cellText(value: unknown, type: TypeDto): string {
  if (value === undefined) {
    return '';
  }
  if (value === null) {
    return 'NULL';
  }
  switch (type.kind) {
    case 'boolean':
      return value ? 'true' : 'false';
    case 'dateTime':
    case 'dateTimeOffset':
      return String(value).replace('T', ' ');
    case 'binary':
      return binaryText(String(value), 16);
    default:
      return typeof value === 'string' ? value : JSON.stringify(value);
  }
}

/** Binary values (base64) as hexadecimal, the first `shown` bytes, and how many there are when there are more. */
export function binaryText(base64: string, shown: number): string {
  let bytes: string;
  try {
    bytes = atob(base64);
  } catch {
    return base64;
  }
  const hex = [...bytes.slice(0, shown)]
    .map((byte) => byte.charCodeAt(0).toString(16).padStart(2, '0'))
    .join('');
  return bytes.length > shown ? `0x${hex}… (${bytes.length.toLocaleString()} bytes)` : `0x${hex}`;
}

/** A row's key from its id (its values as JSON), the values as text, as the address holds them. */
export function keyOf(id: string): string[] {
  try {
    const values: unknown = JSON.parse(id);
    return Array.isArray(values)
      ? values.map((value) => (typeof value === 'string' ? value : JSON.stringify(value)))
      : [id];
  } catch {
    return [id];
  }
}

/** The grid's sort, as the address and the API name columns. */
export function sortOf(
  model: readonly SortModelItem[],
  columns: readonly GridColumn[],
): GridSort[] {
  return model
    .map((item) => ({
      column: columns[indexOfColId(item.colId)]?.name,
      desc: item.sort === 'desc',
    }))
    .filter((key): key is GridSort => key.column !== undefined);
}

/** A sort as the grid's columns hold it; keys on columns it hasn't (or can't sort by), or twice, are left out. */
export function columnStateOf(
  sort: readonly GridSort[],
  columns: readonly GridColumn[],
): { state: ColumnState[]; left: string[] } {
  const state: ColumnState[] = [];
  const left: string[] = [];
  for (const key of sort) {
    const index = indexOfColumn(columns, key.column);
    const colId = colIdOf(index);
    if (index < 0) {
      left.push(`The sort by ${key.column} is left out: the rows have no such column.`);
    } else if (!sortable(columns[index].type)) {
      left.push(`The sort by ${key.column} is left out: its values aren't sorted.`);
    } else if (!state.some((column) => column.colId === colId)) {
      state.push({ colId, sort: key.desc ? 'desc' : 'asc', sortIndex: state.length });
    }
  }
  return { state, left };
}

interface SimpleModel {
  filterType?: string;
  type?: string | null;
  filter?: string | number | null;
  filterTo?: string | number | null;
  dateFrom?: string | null;
  dateTo?: string | null;
}

interface CombinedModel {
  filterType?: string;
  operator: 'AND' | 'OR';
  conditions: SimpleModel[];
}

/** The grid's filters, as the address and the API write them (values as text). */
export function filtersOf(model: FilterModel, columns: readonly GridColumn[]): GridFilter[] {
  const found: GridFilter[] = [];
  for (const [colId, given] of Object.entries(model)) {
    const column = columns[indexOfColId(colId)];
    if (!column || !given) {
      continue;
    }
    const combined = 'conditions' in given ? (given as CombinedModel) : null;
    const models = combined ? combined.conditions : [given as SimpleModel];
    const conditions = models
      .map((condition) => conditionOf(condition))
      .filter((condition) => condition !== null);
    if (conditions.length > 0) {
      found.push({ column: column.name, conditions, any: combined?.operator === 'OR' });
    }
  }
  return found;
}

function conditionOf(model: SimpleModel): GridCondition | null {
  if (model.type === 'gdTrue' || model.type === 'gdFalse') {
    return { op: 'eq', value: model.type === 'gdTrue' ? 'true' : 'false' };
  }
  const op = model.type ? opsOfOptions[model.type] : undefined;
  if (!op) {
    return null;
  }
  const values =
    model.filterType === 'date'
      ? [model.dateFrom, model.dateTo].map((date) => (date ? date.slice(0, 10) : date))
      : [model.filter, model.filterTo];
  const [value, valueTo] = values.map((part) =>
    part === null || part === undefined ? undefined : String(part),
  );
  switch (op) {
    case 'blank':
    case 'notBlank':
      return { op };
    case 'between':
      return value === undefined || valueTo === undefined ? null : { op, value, valueTo };
    default:
      return value === undefined ? null : { op, value };
  }
}

/**
 * Filters as the grid's columns hold them. Those it can't hold are left out, and said: on columns it hasn't, with
 * operations or values its filter doesn't take, a second on a column, conditions past what a filter holds.
 */
export function filterModelOf(
  given: readonly GridFilter[],
  columns: readonly GridColumn[],
): { model: FilterModel; left: string[] } {
  const model: FilterModel = {};
  const left: string[] = [];
  for (const filter of given) {
    const index = indexOfColumn(columns, filter.column);
    if (index < 0) {
      left.push(`The filter on ${filter.column} is left out: the rows have no such column.`);
      continue;
    }
    const colId = colIdOf(index);
    if (model[colId]) {
      left.push(`A second filter on ${filter.column} is left out: a column has one.`);
      continue;
    }
    const kind = filterKindOf(columns[index].type);
    const conditions: SimpleModel[] = [];
    for (const condition of filter.conditions) {
      const held = modelOf(kind, condition);
      if (typeof held === 'string') {
        left.push(`A condition on ${filter.column} is left out: ${held}.`);
      } else if (conditions.length === conditionsPerFilter) {
        left.push(
          `A condition on ${filter.column} is left out: a filter holds ${conditionsPerFilter}.`,
        );
      } else {
        conditions.push(held);
      }
    }
    if (conditions.length === 1) {
      model[colId] = conditions[0];
    } else if (conditions.length > 1) {
      model[colId] = {
        filterType: filterTypes[kind],
        operator: filter.any ? 'OR' : 'AND',
        conditions,
      } satisfies CombinedModel;
    }
  }
  return { model, left };
}

/** A condition as the grid's filter of the kind holds it, or why it can't. */
function modelOf(kind: FilterKind, condition: GridCondition): SimpleModel | string {
  const filterType = filterTypes[kind];
  if (kind === 'boolean') {
    if (condition.op === 'eq' && /^(true|false)$/i.test(condition.value ?? '')) {
      return { filterType, type: condition.value?.toLowerCase() === 'true' ? 'gdTrue' : 'gdFalse' };
    }
    if (condition.op !== 'blank' && condition.op !== 'notBlank') {
      return `true or false is all its filter takes`;
    }
  }
  const option = options[kind]
    .map((item) => (typeof item === 'string' ? item : item.displayKey))
    .find((key) => opsOfOptions[key] === condition.op);
  if (!option) {
    return `its filter doesn't take ${condition.op}`;
  }
  const values = [condition.value, condition.valueTo].slice(0, opValues[condition.op]);
  const problem = values.map((value) => valueProblem(kind, value)).find((found) => found !== null);
  if (problem) {
    return problem;
  }
  if (filterType === 'date') {
    return { filterType, type: option, dateFrom: values[0] ?? null, dateTo: values[1] ?? null };
  }
  const [filter, filterTo] =
    filterType === 'number'
      ? values.map((value) => (value === undefined ? null : Number(value)))
      : values;
  return { filterType, type: option, filter: filter ?? null, filterTo: filterTo ?? null };
}

function valueProblem(kind: FilterKind, value: string | undefined): string | null {
  if (value === undefined) {
    return 'a value is missing';
  }
  switch (kind) {
    case 'number':
      return /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/.test(value.trim())
        ? null
        : `${value} isn't a number`;
    case 'bigint':
      return /^[+-]?\d+$/.test(value.trim()) ? null : `${value} isn't a whole number`;
    case 'date':
      return /^\d{4}-\d{2}-\d{2}$/.test(value) ? null : `${value} isn't a date (yyyy-mm-dd)`;
    default:
      return null;
  }
}
