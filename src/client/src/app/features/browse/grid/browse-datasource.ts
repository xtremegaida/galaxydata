import type { IDatasource, IGetRowsParams } from 'ag-grid-community';
import { type Observable, type Subscription, share } from 'rxjs';
import type { ApiClient, Schema } from '../../../core/api/api-client';
import { type Problem, problemOf } from '../../../core/api/problem';
import type { GridFilter, GridSort } from '../../../core/browse/browse-url';
import type { GridRow } from './grid-columns';

export type BrowseSource = Schema<'BrowseSourceDto'>;
export type BrowsePage = Schema<'BrowsePageDto'>;

/** What a grid asks for: the rows of a source, filtered and sorted. Its pages are the grid's to ask for. */
export interface BrowseQuery {
  readonly source: BrowseSource;
  readonly filters: readonly GridFilter[];
  readonly where: string | null;
  readonly sort: readonly GridSort[];
}

/** A page fetched ahead of the grid (with the schema, to make its columns), which its first ask for it takes. */
export interface PrimedPage {
  readonly query: BrowseQuery;
  readonly page: BrowsePage;
  readonly limit: number;
}

/** What a datasource tells of its pages. */
export interface DatasourceEvents {
  /** A page came, with what is known of the rows' count. */
  loaded(datasource: BrowseDatasource, count: RowCount): void;
  /** A page couldn't be fetched. */
  failed(datasource: BrowseDatasource, problem: Problem): void;
  /** A page past the rows' end came, and where they end isn't known (they weren't counted). */
  pastEnd(datasource: BrowseDatasource): void;
}

/** Pages fetched ahead that a grid's first ask took: each answers one ask only, of whichever grid. */
const taken = new WeakSet<PrimedPage>();

/** How many rows there are: counted, at least so many (more follow), or not known yet. */
export type RowCount =
  | { readonly kind: 'counted'; readonly rows: number }
  | { readonly kind: 'atLeast'; readonly rows: number };

/** Whether two queries ask for the same rows in the same order. */
export function sameQuery(a: BrowseQuery, b: BrowseQuery): boolean {
  return JSON.stringify(normal(a)) === JSON.stringify(normal(b));
}

function normal(query: BrowseQuery) {
  return {
    source: {
      entity: query.source.entity ?? null,
      from: query.source.from ?? null,
      navigation: query.source.navigation ?? null,
    },
    filters: query.filters.map((filter) => ({
      column: filter.column,
      any: filter.conditions.length > 1 && filter.any,
      conditions: filter.conditions.map((condition) => [
        condition.op,
        condition.value ?? null,
        condition.valueTo ?? null,
      ]),
    })),
    where: query.where?.trim() ? query.where : null,
    sort: query.sort.map((key) => [key.column, key.desc]),
  };
}

/**
 * The grid's pages of one query, from the API. A query changed is a datasource of its own, so the grid's pages of
 * the one before go: asks of a datasource retired fail at once (without fetching), and those under way are let go,
 * so the grid's loader, which counts asks until they end, doesn't wait for them. The rows are counted once, with the
 * first page fetched.
 */
export class BrowseDatasource implements IDatasource {
  private retired = false;
  private primed: PrimedPage | null;
  private counted: RowCount | null = null;
  /** Whether the count has been asked for (with the first page, or the page fetched ahead). */
  private countAsked = false;
  private readonly fetching = new Map<string, Observable<BrowsePage>>();
  private readonly asks = new Set<{ subscription: Subscription; params: IGetRowsParams }>();

  constructor(
    private readonly api: ApiClient,
    readonly query: BrowseQuery,
    private readonly events: DatasourceEvents,
    primed: PrimedPage | null = null,
  ) {
    this.primed = primed && sameQuery(primed.query, query) ? primed : null;
  }

  getRows(params: IGetRowsParams<GridRow>): void {
    if (this.retired) {
      params.failCallback();
      return;
    }
    const offset = params.startRow;
    const limit = params.endRow - params.startRow;
    const primed = this.primed;
    if (primed && !taken.has(primed) && primed.page.offset === offset && primed.limit === limit) {
      taken.add(primed);
      this.primed = null;
      this.countAsked = true;
      this.answer(params, primed.page);
      return;
    }
    const key = `${offset}:${limit}`;
    let fetched = this.fetching.get(key);
    const includeCount = !this.countAsked;
    if (!fetched) {
      this.countAsked = true;
      fetched = this.api
        .post('/api/browse/page', {
          body: {
            source: this.query.source,
            grid: {
              filters: this.query.filters.map((filter) => ({
                ...filter,
                conditions: [...filter.conditions],
              })),
              where: this.query.where,
              sort: [...this.query.sort],
              offset,
              limit,
            },
            includeSchema: false,
            includeCount,
          },
        })
        .pipe(share());
      this.fetching.set(key, fetched);
    }
    const ask = { params, subscription: null as unknown as Subscription };
    ask.subscription = fetched.subscribe({
      next: (page) => {
        this.fetching.delete(key);
        this.asks.delete(ask);
        this.answer(params, page);
      },
      error: (error: unknown) => {
        this.fetching.delete(key);
        this.asks.delete(ask);
        // Said first, so the grid says why as it shows nothing. Nothing read yet: no rows (not a page of blank
        // ones), and the count is asked for again with the next page; a later page fails, for the grid to ask for
        // again.
        const first = this.counted === null;
        if (includeCount) {
          this.countAsked = false;
        }
        this.events.failed(this, problemOf(error));
        if (first) {
          params.successCallback([], 0);
        } else {
          params.failCallback();
        }
      },
    });
    if (!ask.subscription.closed) {
      this.asks.add(ask);
    }
  }

  /** What is known of the rows' count; null until a page comes. */
  get count(): RowCount | null {
    return this.counted;
  }

  /** The grid let go of this datasource (another query, or the grid gone). */
  destroy(): void {
    this.retire();
  }

  /** No more pages: asks under way are let go, and fail for the grid. */
  retire(): void {
    if (this.retired) {
      return;
    }
    this.retired = true;
    for (const ask of this.asks) {
      ask.subscription.unsubscribe();
      ask.params.failCallback();
    }
    this.asks.clear();
    this.fetching.clear();
  }

  private answer(params: IGetRowsParams<GridRow>, page: BrowsePage): void {
    const end = page.offset + page.rows.length;
    const pastEnd = page.rows.length === 0 && page.offset > 0 && !page.hasMore;
    if (pastEnd && page.total === null) {
      // A page past the rows' end (an address's), and they weren't counted: where they end isn't known. The ask is
      // answered (the grid's loader waits for it), and the grid goes elsewhere.
      params.successCallback([], -1);
      this.events.pastEnd(this);
      return;
    }
    if (!page.hasMore && page.rows.length > 0) {
      // The last page: the rows end with it, whatever was counted before.
      this.counted = { kind: 'counted', rows: end };
    } else if (page.total !== null && (page.total > end || pastEnd)) {
      // Counted, past this page; or, a page past the rows' end, where they do.
      this.counted = { kind: 'counted', rows: page.total };
    } else if (!page.hasMore) {
      this.counted = { kind: 'counted', rows: end };
    } else if (this.counted?.kind !== 'counted' || this.counted.rows <= end) {
      // Not counted (in time), or more rows than were counted: at least those up to this page's, and one more.
      this.counted = { kind: 'atLeast', rows: Math.max(end + 1, this.counted?.rows ?? 0) };
    }
    const count = this.counted;
    params.successCallback(page.rows, count.kind === 'counted' ? count.rows : -1);
    this.events.loaded(this, count);
  }
}
