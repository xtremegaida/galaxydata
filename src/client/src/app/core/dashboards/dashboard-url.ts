import type { Params } from '@angular/router';
import type { Schema } from '../api/api-client';
import { sameJson } from '../api/same-json';

/**
 * A dashboard's state in its address: the only place its grammar lives.
 *
 * `/dashboards/7?f.status=open,shipped&f.placed=last.3.month&s.by-city=!'Cape Town'&s.by-month=2026-01-01|open`
 *
 * - `f.<filter>`: the value of a filter the viewer may change, when it isn't the dashboard's own. By the filter's
 *   kind: values `a,b` (`!` first for those left out: `!a,b`); a range `from~to` (either side empty: from or up
 *   to); a relative period `<mode>.<count>.<unit>` (`last.7.day`, `this.1.quarter`, `previous.1.month`,
 *   `toDate.1.year`); text as it is; a boolean `true` or `false`. Empty, the filter is cleared (its default, if
 *   it has one, doesn't hold).
 * - `s.<widget>`: the slices chosen in a widget, `,` between them (`!` first when they are left out instead); a
 *   slice of two parts (a series' bar) has `|` between them.
 *
 * Values keep their JSON types: bare `null`, `true`, `false` and numbers are those; anything else bare is text.
 * Text that would read as one of them, is empty, holds `,`, `|`, `~` or `'`, or starts with `!`, is quoted:
 * `'null'`, `'42'`, `'a,b'`, a quote in it doubled. Ids are `[a-z0-9-]{1,32}`, so names need no quoting.
 */

export type ConditionValue = Schema<'ConditionValue'>;
export type SelectionState = Schema<'SelectionState'>;
type Definition = Schema<'DashboardDefinition'>;
type Filter = Schema<'DashboardFilter'>;
type RelativeRange = Schema<'RelativeRange'>;

/** What the address holds: the values of filters the viewer set (null: cleared), and the slices chosen by widget. */
export interface DashboardUrlState {
  readonly filters: Readonly<Record<string, ConditionValue | null>>;
  readonly selections: Readonly<Record<string, SelectionState>>;
}

/** An address read: its state, and what in it couldn't be read (and was left out). */
export interface ReadDashboardState {
  readonly state: DashboardUrlState;
  readonly problems: readonly string[];
}

const filterPrefix = 'f.';
const selectionPrefix = 's.';
const literal = /^(null|true|false|-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?)$/;
const modes: readonly RelativeRange['mode'][] = ['last', 'this', 'previous', 'toDate'];
const units: readonly RelativeRange['unit'][] = ['day', 'week', 'month', 'quarter', 'year'];

/** Whether a query parameter is one of a dashboard's state (a filter's or a widget's). */
export function isDashboardParam(name: string): boolean {
  return name.startsWith(filterPrefix) || name.startsWith(selectionPrefix);
}

/**
 * The query parameters of a dashboard's state: the filters' values unlike their defaults (and that can be written:
 * ranges from, up to, or between), and the slices chosen.
 */
export function dashboardParams(definition: Definition, state: DashboardUrlState): Params {
  const params: Record<string, string> = {};
  for (const filter of definition.filters) {
    if (!(filter.id in state.filters)) {
      continue;
    }
    const value = state.filters[filter.id];
    if (sameJson(value ?? null, filter.value ?? null)) {
      continue;
    }
    const text = value === null ? '' : writeFilterValue(filter, value);
    if (text !== null) {
      params[filterPrefix + filter.id] = text;
    }
  }
  for (const [widget, selection] of Object.entries(state.selections)) {
    const keys = selection.keys.map((key) => key.map(writeScalar));
    if (keys.length > 0 && keys.every((key) => key.length > 0 && key.every((p) => p !== null))) {
      params[selectionPrefix + widget] =
        (selection.mode === 'exclude' ? '!' : '') + keys.map((key) => key.join('|')).join(',');
    }
  }
  return params;
}

/**
 * A dashboard's state from its address. Filters that aren't there, the viewer may not change or aren't shown,
 * widgets that aren't there or don't choose slices, and values that can't be read are problems, and left out; of a
 * selection longer than `maxKeys`, its first are kept.
 */
export function readDashboardParams(
  definition: Definition,
  params: Params,
  maxKeys: number,
): ReadDashboardState {
  const problems: string[] = [];
  const filters: Record<string, ConditionValue | null> = {};
  const selections: Record<string, SelectionState> = {};
  for (const [name, raw] of Object.entries(params)) {
    if (!isDashboardParam(name)) {
      continue;
    }
    const text = Array.isArray(raw) ? String(raw.at(-1) ?? '') : String(raw ?? '');
    if (name.startsWith(filterPrefix)) {
      const id = name.slice(filterPrefix.length);
      const filter = definition.filters.find((f) => f.id === id);
      if (!filter) {
        problems.push(`There's no filter ${id}.`);
      } else if (!filter.visible || !filter.editable) {
        problems.push(`The filter ${filter.label} can't be changed.`);
      } else {
        try {
          filters[id] = text === '' ? null : readFilterValue(filter, text);
        } catch (error) {
          problems.push(
            `The filter ${filter.label} couldn't be read: ${(error as Error).message}.`,
          );
        }
      }
    } else {
      const id = name.slice(selectionPrefix.length);
      const widget = definition.widgets.find((w) => w.id === id);
      const config = widget?.config as { kind: string; emits?: boolean } | undefined;
      if (!widget || !config || config.kind === 'text' || !config.emits) {
        problems.push(`There's no widget ${id} that chooses slices.`);
        continue;
      }
      try {
        const selection = readSelection(text);
        if (selection.keys.length > maxKeys) {
          problems.push(
            `Only the first ${maxKeys} slices chosen in ${widget.title ?? id} are kept.`,
          );
        }
        selections[id] = { mode: selection.mode, keys: selection.keys.slice(0, maxKeys) };
      } catch (error) {
        problems.push(
          `The slices chosen in ${widget.title ?? id} couldn't be read: ${(error as Error).message}.`,
        );
      }
    }
  }
  return { state: { filters, selections }, problems };
}

/** A filter's value as its kind writes it; null for one that can't be (a range above or below, not from or to). */
function writeFilterValue(filter: Filter, value: ConditionValue): string | null {
  switch (filter.kind) {
    case 'values': {
      const values = (value.values ?? []).map(writeScalar);
      if (values.some((v) => v === null) || values.length === 0) {
        return null;
      }
      return (value.op === 'notIn' ? '!' : '') + values.join(',');
    }
    case 'range': {
      // `between` has both bounds; `ge` and `le` one, in `value`.
      const bound =
        value.value === undefined || value.value === null ? null : writeScalar(value.value);
      const upTo =
        value.valueTo === undefined || value.valueTo === null ? null : writeScalar(value.valueTo);
      if (bound === null) {
        return null;
      }
      switch (value.op) {
        case 'between':
          return upTo === null ? null : `${bound}~${upTo}`;
        case 'ge':
          return `${bound}~`;
        case 'le':
          return `~${bound}`;
        default:
          return null;
      }
    }
    case 'relative':
      return value.relative
        ? `${value.relative.mode}.${value.relative.count}.${value.relative.unit}`
        : null;
    case 'text':
      return writeScalar(String(value.value ?? ''));
    case 'boolean':
      return typeof value.value === 'boolean' ? String(value.value) : null;
  }
}

function readFilterValue(filter: Filter, text: string): ConditionValue {
  switch (filter.kind) {
    case 'values': {
      const scanner = new Scanner(text);
      const notIn = scanner.accept('!');
      const values: unknown[] = [];
      do {
        values.push(scanner.scalar());
      } while (scanner.accept(','));
      scanner.end();
      if (!filter.multiple && values.length > 1) {
        throw new Error('it takes one value');
      }
      return { op: notIn ? 'notIn' : 'in', values };
    }
    case 'range': {
      const scanner = new Scanner(text);
      const from = scanner.peek() === '~' ? undefined : scanner.scalar();
      scanner.expect('~');
      const to = scanner.peek() === undefined ? undefined : scanner.scalar();
      scanner.end();
      if (from === undefined && to === undefined) {
        throw new Error('a value from or to was expected');
      }
      if (from !== undefined && to !== undefined) {
        return { op: 'between', value: from, valueTo: to };
      }
      return from !== undefined ? { op: 'ge', value: from } : { op: 'le', value: to };
    }
    case 'relative': {
      const [mode, count, unit, ...rest] = text.split('.');
      const n = Number(count);
      if (
        rest.length > 0 ||
        !modes.includes(mode as RelativeRange['mode']) ||
        !units.includes(unit as RelativeRange['unit']) ||
        !/^\d+$/.test(count ?? '') ||
        n < 1 ||
        n > 1000
      ) {
        throw new Error(`'${text}' isn't a period such as last.7.day`);
      }
      return {
        op: 'relative',
        relative: {
          mode: mode as RelativeRange['mode'],
          count: n,
          unit: unit as RelativeRange['unit'],
        },
      };
    }
    case 'text': {
      const scanner = new Scanner(text);
      const value = scanner.scalar();
      scanner.end();
      return {
        op: filter.value?.op === 'startsWith' ? 'startsWith' : 'contains',
        value: String(value),
      };
    }
    case 'boolean':
      if (text !== 'true' && text !== 'false') {
        throw new Error(`'${text}' isn't true or false`);
      }
      return { op: 'eq', value: text === 'true' };
  }
}

function readSelection(text: string): SelectionState {
  const scanner = new Scanner(text);
  const exclude = scanner.accept('!');
  const keys: unknown[][] = [];
  do {
    const key: unknown[] = [];
    do {
      key.push(scanner.scalar());
    } while (scanner.accept('|'));
    keys.push(key);
  } while (scanner.accept(','));
  scanner.end();
  return { mode: exclude ? 'exclude' : 'include', keys };
}

/** A JSON value as the address holds it: bare when it reads back the same, else quoted; null for one it can't hold. */
function writeScalar(value: unknown): string | null {
  if (value === null || typeof value === 'boolean') {
    return String(value);
  }
  if (typeof value === 'number') {
    return Number.isFinite(value) ? String(value) : null;
  }
  if (typeof value !== 'string') {
    return null;
  }
  const bare =
    value !== '' && !/[,|~']/.test(value) && !value.startsWith('!') && !literal.test(value);
  return bare ? value : `'${value.replaceAll("'", "''")}'`;
}

/** Reads values, bare or quoted, and the marks between them. */
class Scanner {
  private at = 0;

  constructor(private readonly text: string) {}

  peek(): string | undefined {
    return this.text[this.at];
  }

  accept(mark: string): boolean {
    if (this.text[this.at] !== mark) {
      return false;
    }
    this.at++;
    return true;
  }

  expect(mark: string): void {
    if (!this.accept(mark)) {
      throw new Error(`'${mark}' was expected at ${this.at + 1}`);
    }
  }

  end(): void {
    if (this.at < this.text.length) {
      throw new Error(`'${this.text[this.at]}' wasn't expected at ${this.at + 1}`);
    }
  }

  /** A value: quoted text, or bare (a literal, or text), which isn't empty. */
  scalar(): unknown {
    const start = this.at;
    if (this.text[this.at] === "'") {
      return this.quoted();
    }
    while (this.at < this.text.length && !",|~'".includes(this.text[this.at])) {
      this.at++;
    }
    const token = this.text.slice(start, this.at);
    if (token === '') {
      throw new Error(`a value was expected at ${start + 1}`);
    }
    if (!literal.test(token)) {
      return token;
    }
    return token === 'null'
      ? null
      : token === 'true'
        ? true
        : token === 'false'
          ? false
          : Number(token);
  }

  private quoted(): string {
    let token = '';
    for (let i = this.at + 1; i < this.text.length; i++) {
      if (this.text[i] !== "'") {
        token += this.text[i];
      } else if (this.text[i + 1] === "'") {
        token += "'";
        i++;
      } else {
        this.at = i + 1;
        return token;
      }
    }
    throw new Error(`the quote at ${this.at + 1} isn't closed`);
  }
}
