import type { IDatasource, IGetRowsParams } from 'ag-grid-community';
import { type Observable, type Subscription, share } from 'rxjs';
import { type Problem, problemOf } from '../../../core/api/problem';
import type { GridRow } from './grid-columns';

/** A page of rows, as the API gives them: from an offset, whether more follow, and how many there are when counted. */
export interface RowsPage {
  readonly offset: number;
  readonly rows: readonly GridRow[];
  readonly hasMore: boolean;
  readonly total: number | null;
}

/** A page fetched ahead of the grid (with the schema, to make its columns), which its first ask for it takes. */
export interface PrimedRows<Q, P extends RowsPage> {
  readonly query: Q;
  readonly page: P;
  readonly limit: number;
}

/** How many rows there are: counted, at least so many (more follow), or not known yet. */
export type RowCount =
  | { readonly kind: 'counted'; readonly rows: number }
  | { readonly kind: 'atLeast'; readonly rows: number };

/** What a datasource tells of its pages. */
export interface PagesEvents<D> {
  /** A page came, with what is known of the rows' count. */
  loaded(datasource: D, count: RowCount): void;
  /** A page couldn't be fetched. */
  failed(datasource: D, problem: Problem): void;
  /** A page past the rows' end came, and where they end isn't known (they weren't counted). */
  pastEnd(datasource: D): void;
}

/** Pages fetched ahead that a grid's first ask took: each answers one ask only, of whichever grid. */
const taken = new WeakSet<object>();

/**
 * The grid's pages of one query, from the API (AG Grid's infinite row model). A query changed is a datasource of its
 * own, so the grid's pages of the one before go: asks of a datasource retired fail at once (without fetching), and
 * those under way are let go, so the grid's loader, which counts asks until they end, doesn't wait for them. The
 * rows are counted once, with the first page fetched. Asks of a page under way share its request.
 */
export abstract class PagedDatasource<Q, P extends RowsPage> implements IDatasource {
  private retired = false;
  private primed: PrimedRows<Q, P> | null;
  private counted: RowCount | null = null;
  /** Whether the count has been asked for (with the first page, or the page fetched ahead). */
  private countAsked = false;
  private readonly fetching = new Map<string, Observable<P>>();
  private readonly asks = new Set<{ subscription: Subscription; params: IGetRowsParams }>();

  protected constructor(
    readonly query: Q,
    private readonly events: PagesEvents<PagedDatasource<Q, P>>,
    primed: PrimedRows<Q, P> | null,
    same: (a: Q, b: Q) => boolean,
  ) {
    this.primed = primed && same(primed.query, query) ? primed : null;
  }

  /** A page of the query's rows: from `offset`, `limit` of them, counted when asked. */
  protected abstract fetch(offset: number, limit: number, includeCount: boolean): Observable<P>;

  /** A page came: before the grid is given its rows. */
  protected took(page: P): void {
    void page;
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
      fetched = this.fetch(offset, limit, includeCount).pipe(share());
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

  private answer(params: IGetRowsParams<GridRow>, page: P): void {
    this.took(page);
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
    params.successCallback(page.rows as GridRow[], count.kind === 'counted' ? count.rows : -1);
    this.events.loaded(this, count);
  }
}
