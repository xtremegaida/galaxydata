import { Observable, defer, finalize } from 'rxjs';
import type { ApiClient, Schema } from '../../../core/api/api-client';
import type { DashboardState, Definition, WidgetData } from '../model/definition';
import { sliceOf } from '../model/slice';

export type WidgetPage = Schema<'WidgetPage'>;
export type FilterValues = Schema<'FilterValuesDto'>;
export type WidgetQuery = Schema<'WidgetQueryDto'>;

/**
 * Where a dashboard's widgets get their rows: the published copy (by its hash), a definition sent as it is (the
 * editor's, the owner's working copy), or a public link. What the viewer may see besides (the queries, the
 * underlying rows) is the host's to say. Pages provide it; the view never asks the session.
 */
export interface DashboardHost {
  readonly kind: 'app' | 'embed' | 'editor';
  readonly canViewQueries: boolean;
  readonly canViewUnderlying: boolean;
  data(
    widget: string,
    state: DashboardState,
    page: WidgetPage | null,
    refresh: boolean,
  ): Observable<WidgetData>;
  values(filter: string, state: DashboardState, search: string | null): Observable<FilterValues>;
  query(widget: string, state: DashboardState): Observable<WidgetQuery> | null;
  /** What a widget's rows depend on in the definition: they are asked for again when it changes, and only then. */
  /**
   * What a widget's rows depend on in the definition (with what reaches it): they are asked for again when it
   * changes, and only then.
   */
  basis(widget: string, definition: Definition, state: DashboardState): unknown;
}

/**
 * Requests a page has in flight at most: the server runs a user's queries a few at a time (and refuses many
 * waiting), so a page of many widgets asks for some, then the rest as those end.
 */
export class Lanes {
  private running = 0;
  private readonly waiting: (() => void)[] = [];

  constructor(private readonly lanes = 4) {}

  run<T>(request: () => Observable<T>): Observable<T> {
    return defer(
      () =>
        new Observable<T>((subscriber) => {
          let inner: { unsubscribe(): void } | null = null;
          let done = false;
          const start = () => {
            if (done) {
              this.next();
              return;
            }
            inner = request()
              .pipe(finalize(() => this.next()))
              .subscribe(subscriber);
          };
          if (this.running < this.lanes) {
            this.running++;
            start();
          } else {
            this.waiting.push(() => start());
          }
          return () => {
            done = true;
            inner?.unsubscribe();
          };
        }),
    );
  }

  private next(): void {
    const waiting = this.waiting.shift();
    if (waiting) {
      waiting();
    } else {
      this.running--;
    }
  }
}

/** A dashboard's published copy, as someone signed in sees it. */
export class PublishedHost implements DashboardHost {
  readonly kind = 'app' as const;
  readonly canViewQueries = true;
  readonly canViewUnderlying = true;
  private readonly lanes = new Lanes();

  constructor(
    private readonly api: ApiClient,
    private readonly id: number,
    private readonly hash: string,
  ) {}

  data(
    widget: string,
    state: DashboardState,
    page: WidgetPage | null,
    refresh: boolean,
  ): Observable<WidgetData> {
    return this.lanes.run(() =>
      this.api.post('/api/dashboards/{id}/widgets/{widget}/data', {
        path: { id: this.id, widget },
        body: { hash: this.hash, state, page, refresh },
      }),
    );
  }

  values(filter: string, state: DashboardState, search: string | null): Observable<FilterValues> {
    return this.lanes.run(() =>
      this.api.post('/api/dashboards/{id}/filters/{filter}/values', {
        path: { id: this.id, filter },
        body: { hash: this.hash, state, search },
      }),
    );
  }

  query(widget: string, state: DashboardState): Observable<WidgetQuery> {
    return this.api.post('/api/dashboards/{id}/widgets/{widget}/query', {
      path: { id: this.id, widget },
      body: { hash: this.hash, state, page: null, refresh: false },
    });
  }

  basis(): string {
    return this.hash;
  }
}

/**
 * A definition sent as it is: the editor's (unsaved), or the owner's working copy. A widget's rows are asked for
 * with its slice (what they depend on), so what changes elsewhere in the definition asks for nothing.
 */
export class InlineHost implements DashboardHost {
  readonly canViewQueries = true;
  readonly canViewUnderlying = true;
  private readonly lanes = new Lanes();

  constructor(
    private readonly api: ApiClient,
    private readonly definition: () => Definition,
    readonly kind: 'app' | 'editor' = 'app',
  ) {}

  data(
    widget: string,
    state: DashboardState,
    page: WidgetPage | null,
    refresh: boolean,
  ): Observable<WidgetData> {
    return this.lanes.run(() =>
      this.api.post('/api/dashboards/data', {
        body: {
          slice: sliceOf(this.definition(), widget, state.selections ?? {}),
          widget,
          state,
          page,
          refresh,
        },
      }),
    );
  }

  values(filter: string, state: DashboardState, search: string | null): Observable<FilterValues> {
    return this.lanes.run(() =>
      this.api.post('/api/dashboards/filter-values', {
        body: { slice: this.definition(), filter, state, search },
      }),
    );
  }

  query(widget: string, state: DashboardState): Observable<WidgetQuery> {
    return this.api.post('/api/dashboards/query', {
      body: {
        slice: sliceOf(this.definition(), widget, state.selections ?? {}),
        widget,
        state,
        page: null,
        refresh: false,
      },
    });
  }

  basis(widget: string, definition: Definition, state: DashboardState): Definition {
    return sliceOf(definition, widget, state.selections ?? {});
  }
}

/** A public dashboard, by its link: no queries to see, no underlying rows. The state goes in the address, as base64url JSON. */
export class PublicHost implements DashboardHost {
  readonly kind = 'embed' as const;
  readonly canViewQueries = false;
  readonly canViewUnderlying = false;
  private readonly lanes = new Lanes();

  constructor(
    private readonly api: ApiClient,
    private readonly token: string,
  ) {}

  data(widget: string, state: DashboardState, page: WidgetPage | null): Observable<WidgetData> {
    return this.lanes.run(() =>
      this.api.get('/api/public/dashboards/{token}/widgets/{widget}/data', {
        path: { token: this.token, widget },
        query: {
          s: encodeState(state),
          offset: page?.offset,
          limit: page?.limit ?? undefined,
          count: page?.count || undefined,
        },
      }),
    );
  }

  values(filter: string, state: DashboardState, search: string | null): Observable<FilterValues> {
    return this.lanes.run(() =>
      this.api.get('/api/public/dashboards/{token}/filters/{filter}/values', {
        path: { token: this.token, filter },
        query: { s: encodeState(state), text: search ?? undefined },
      }),
    );
  }

  query(): null {
    return null;
  }

  basis(): string {
    return this.token;
  }
}

/** A state as a public request gives it: base64url JSON (none when there is nothing in it). */
export function encodeState(state: DashboardState): string | undefined {
  const empty =
    Object.keys(state.filters ?? {}).length === 0 &&
    Object.keys(state.selections ?? {}).length === 0;
  if (empty) {
    return undefined;
  }
  const bytes = new TextEncoder().encode(JSON.stringify(state));
  let binary = '';
  bytes.forEach((b) => (binary += String.fromCharCode(b)));
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
