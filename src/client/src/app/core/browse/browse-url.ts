import { type Params, UrlSegment, UrlSegmentGroup, UrlTree } from '@angular/router';
import type { Schema } from '../api/api-client';

/**
 * Browsing's addresses: the only place their grammar lives.
 *
 * `/browse/shop.customers;f=country:eq:ZA;row=42/orders;sort=-placed_at;row=1001/lines?at=1`
 *
 * The first segment names an entity, each after it a navigation from the row chosen in the one before: a path
 * through the data, each step a crumb. Each crumb's segment holds its grid's state as matrix parameters:
 *
 * - `f`: filters, `,` between them. A filter is its column, then its conditions: an operation and its values, `:`
 *   between each. Conditions all of which a row must meet follow one another; those any of which it must meet have
 *   `or` between them: `f=total:ge:10:le:100,status:eq:open:or:eq:held`.
 * - `w`: a condition in the query language, as written.
 * - `sort`: columns, `,` between them, `-` before those sorted from the last: `sort=-placed_at,id`.
 * - `page`: the page, from 1.
 * - `row`: the key of the row chosen, `~` between its values.
 *
 * Names and values that are empty, hold `,`, `:`, `~` or `'`, or (in a sort) start with `-`, are quoted: `'a,b'`,
 * a quote in them doubled. `?at=` says which crumb is shown, counting from 0, when it isn't the last.
 */

export type GridOp = Schema<'GridOp'>;

/** A condition on a column's values, its values as text, as the address holds them (the server reads them so). */
export interface GridCondition {
  readonly op: GridOp;
  readonly value?: string;
  readonly valueTo?: string;
}

/** Conditions on one column, all of which (or with `any`, any of which) a row must meet. */
export interface GridFilter {
  readonly column: string;
  readonly conditions: readonly GridCondition[];
  readonly any: boolean;
}

export interface GridSort {
  readonly column: string;
  readonly desc: boolean;
}

/** A step of a path through the data: what it browses, and its grid's state. */
export interface BrowseCrumb {
  /** The first crumb's entity, as queries name it; a navigation from the row chosen in the crumb before. */
  readonly name: string;
  readonly filters: readonly GridFilter[];
  /** A condition in the query language, or null. */
  readonly where: string | null;
  readonly sort: readonly GridSort[];
  /** The page shown, from 0. */
  readonly page: number;
  /** The key of the row chosen, its values as text; null when none is. */
  readonly row: readonly string[] | null;
}

/** Where browsing is: a path through the data (at least one crumb), and the crumb shown. */
export interface BrowseLocation {
  readonly crumbs: readonly BrowseCrumb[];
  readonly at: number;
}

/** An address read: where it leads, and what in it couldn't be read (and was left out). */
export interface ReadLocation {
  readonly location: BrowseLocation;
  readonly problems: readonly string[];
}

/** How many values each operation takes. */
export const opValues: Readonly<Record<GridOp, 0 | 1 | 2>> = {
  eq: 1,
  ne: 1,
  lt: 1,
  le: 1,
  gt: 1,
  ge: 1,
  between: 2,
  contains: 1,
  notContains: 1,
  startsWith: 1,
  endsWith: 1,
  blank: 0,
  notBlank: 0,
};

/** Where browsing lives. */
export const browsePath = 'browse';

/** A crumb with no filters, condition, sort or row, on its first page. */
export function crumbOf(
  name: string,
  changes: Partial<Omit<BrowseCrumb, 'name'>> = {},
): BrowseCrumb {
  return { name, filters: [], where: null, sort: [], page: 0, row: null, ...changes };
}

/** Browsing an entity's rows, from the first page. */
export function entityLocation(entity: string): BrowseLocation {
  return { crumbs: [crumbOf(entity)], at: 0 };
}

/** Entities' addresses made, so a link's address is the same each time it is asked for. */
const entityUrls = new Map<string, UrlTree>();

/** The address of an entity's rows, from the first page: where links into browsing go. */
export function entityUrl(entity: string): UrlTree {
  let url = entityUrls.get(entity);
  if (!url) {
    if (entityUrls.size >= 1000) {
      entityUrls.clear();
    }
    url = browseUrlTree(entityLocation(entity));
    entityUrls.set(entity, url);
  }
  return url;
}

/** The crumb shown. */
export function activeCrumb(location: BrowseLocation): BrowseCrumb {
  return location.crumbs[location.at];
}

/** The address of a location, as the router takes it. */
export function browseUrlTree(location: BrowseLocation): UrlTree {
  const segments = [
    new UrlSegment(browsePath, {}),
    ...location.crumbs.map((crumb) => new UrlSegment(crumb.name, crumbParams(crumb))),
  ];
  const queryParams: Params =
    location.at === location.crumbs.length - 1 ? {} : { at: String(location.at) };
  return new UrlTree(
    new UrlSegmentGroup([], { primary: new UrlSegmentGroup(segments, {}) }),
    queryParams,
  );
}

/**
 * Where an address under browsing leads: its segments after `/browse` (each with its matrix parameters) and its
 * query. Null without segments (the start of browsing). What can't be read is left out, and said in `problems`.
 */
export function readBrowseLocation(
  segments: readonly UrlSegment[],
  queryParams: Params,
): ReadLocation | null {
  if (segments.length === 0) {
    return null;
  }
  const problems: string[] = [];
  const crumbs = segments.map((segment, index) => readCrumb(segment, index, problems));
  const last = crumbs.length - 1;
  let at = last;
  const given: unknown = queryParams['at'];
  if (given !== undefined) {
    const number = typeof given === 'string' && /^\d{1,9}$/.test(given) ? Number(given) : NaN;
    if (Number.isInteger(number) && number <= last) {
      at = number;
    } else {
      problems.push(
        `The crumb shown (at=${String(given)}) isn't one of the address's; the last is shown.`,
      );
    }
  }
  return { location: { crumbs, at }, problems };
}

function crumbParams(crumb: BrowseCrumb): Record<string, string> {
  const params: Record<string, string> = {};
  if (crumb.filters.length > 0) {
    params['f'] = crumb.filters.map(writeFilter).join(',');
  }
  if (crumb.where !== null) {
    params['w'] = crumb.where;
  }
  if (crumb.sort.length > 0) {
    params['sort'] = crumb.sort
      .map((key) => (key.desc ? '-' : '') + quote(key.column, true))
      .join(',');
  }
  if (crumb.page > 0) {
    params['page'] = String(crumb.page + 1);
  }
  if (crumb.row !== null) {
    params['row'] = crumb.row.map((value) => quote(value)).join('~');
  }
  return params;
}

function writeFilter(filter: GridFilter): string {
  const conditions = filter.conditions.map((condition) =>
    [condition.op, condition.value, condition.valueTo]
      .slice(0, opValues[condition.op] + 1)
      .map((part, index) => (index === 0 ? part : quote(part ?? '')))
      .join(':'),
  );
  return [quote(filter.column), conditions.join(filter.any ? ':or:' : ':')].join(':');
}

/** A name or value as the address holds it: bare when it can be, else quoted. */
function quote(text: string, sorted = false): string {
  const bare = text !== '' && !/[,:~']/.test(text) && !(sorted && text.startsWith('-'));
  return bare ? text : `'${text.replaceAll("'", "''")}'`;
}

function readCrumb(segment: UrlSegment, index: number, problems: string[]): BrowseCrumb {
  const params = segment.parameters;
  const what = index === 0 ? segment.path : `the crumb ${segment.path}`;
  const read = <T>(name: string, reader: (text: string) => T, otherwise: T, says: string): T => {
    const text = params[name];
    if (text === undefined) {
      return otherwise;
    }
    try {
      return reader(text);
    } catch (problem) {
      const are = says.startsWith('filters') ? 'are' : 'is';
      problems.push(
        `The ${says} of ${what} couldn't be read, and ${are} left out: ${(problem as Error).message}.`,
      );
      return otherwise;
    }
  };
  const where = params['w'] === undefined || params['w'].trim() === '' ? null : params['w'];
  return {
    name: segment.path,
    filters: read('f', readFilters, [], 'filters (f)'),
    where,
    sort: read('sort', readSort, [], 'sort (sort)'),
    page: read('page', readPage, 0, 'page (page)'),
    row: read('row', readRow, null, 'row chosen (row)'),
  };
}

function readFilters(text: string): GridFilter[] {
  const scanner = new Scanner(text);
  const filters: GridFilter[] = [];
  do {
    const column = scanner.name('a column');
    const conditions: GridCondition[] = [];
    let any: boolean | null = null;
    do {
      scanner.expect(':');
      let op = scanner.name('an operation');
      if (conditions.length > 0) {
        const joined = op === 'or';
        if (any !== null && any !== joined) {
          throw new Error(`the filter on ${column} mixes conditions joined by or with others`);
        }
        any = joined;
        if (joined) {
          scanner.expect(':');
          op = scanner.name('an operation');
        }
      }
      if (!Object.hasOwn(opValues, op)) {
        throw new Error(`${op} isn't an operation`);
      }
      const gridOp = op as GridOp;
      const values: string[] = [];
      for (let i = 0; i < opValues[gridOp]; i++) {
        scanner.expect(':');
        values.push(scanner.token());
      }
      conditions.push(conditionOf(gridOp, values));
    } while (scanner.peek() === ':');
    filters.push({ column, conditions, any: any ?? false });
  } while (scanner.accept(','));
  scanner.end();
  return filters;
}

function conditionOf(op: GridOp, values: readonly string[]): GridCondition {
  switch (values.length) {
    case 0:
      return { op };
    case 1:
      return { op, value: values[0] };
    default:
      return { op, value: values[0], valueTo: values[1] };
  }
}

function readSort(text: string): GridSort[] {
  const scanner = new Scanner(text);
  const sort: GridSort[] = [];
  do {
    const desc = scanner.accept('-');
    sort.push({ column: scanner.name('a column'), desc });
  } while (scanner.accept(','));
  scanner.end();
  return sort;
}

function readPage(text: string): number {
  if (!/^[1-9]\d{0,9}$/.test(text)) {
    throw new Error(`${text} isn't a page, which counts from 1`);
  }
  return Number(text) - 1;
}

function readRow(text: string): string[] {
  const scanner = new Scanner(text);
  const row: string[] = [];
  do {
    row.push(scanner.token());
  } while (scanner.accept('~'));
  scanner.end();
  return row;
}

/** Reads names and values, bare or quoted, and the marks between them. */
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

  /** A name, which isn't empty. */
  name(what: string): string {
    const start = this.at;
    const name = this.token();
    if (name === '') {
      throw new Error(`${what} was expected at ${start + 1}`);
    }
    return name;
  }

  token(): string {
    if (this.text[this.at] !== "'") {
      const start = this.at;
      while (this.at < this.text.length && !",:~'".includes(this.text[this.at])) {
        this.at++;
      }
      return this.text.slice(start, this.at);
    }
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
