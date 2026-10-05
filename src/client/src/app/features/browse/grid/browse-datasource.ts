import type { Observable } from 'rxjs';
import type { ApiClient, Schema } from '../../../core/api/api-client';
import type { GridFilter, GridSort } from '../../../core/browse/browse-url';
import {
  PagedDatasource,
  type PagesEvents,
  type PrimedRows,
  type RowCount,
} from './paged-datasource';

export type BrowseSource = Schema<'BrowseSourceDto'>;
export type BrowsePage = Schema<'BrowsePageDto'>;
export type { RowCount };

/** What a grid asks for: the rows of a source, filtered and sorted. Its pages are the grid's to ask for. */
export interface BrowseQuery {
  readonly source: BrowseSource;
  readonly filters: readonly GridFilter[];
  readonly where: string | null;
  readonly sort: readonly GridSort[];
}

/** A page fetched ahead of the grid (with the schema, to make its columns), which its first ask for it takes. */
export type PrimedPage = PrimedRows<BrowseQuery, BrowsePage>;

/** What a datasource tells of its pages. */
export type DatasourceEvents = PagesEvents<PagedDatasource<BrowseQuery, BrowsePage>>;

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

/** The grid's pages of a source's rows, from the API's browsing (see `PagedDatasource`). */
export class BrowseDatasource extends PagedDatasource<BrowseQuery, BrowsePage> {
  constructor(
    private readonly api: ApiClient,
    query: BrowseQuery,
    events: DatasourceEvents,
    primed: PrimedPage | null = null,
  ) {
    super(query, events, primed, sameQuery);
  }

  protected fetch(offset: number, limit: number, includeCount: boolean): Observable<BrowsePage> {
    return this.api.post('/api/browse/page', {
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
    });
  }
}
