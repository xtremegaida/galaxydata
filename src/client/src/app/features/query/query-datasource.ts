import { type Observable, map } from 'rxjs';
import type { ApiClient, Schema } from '../../core/api/api-client';
import type { GridFilter, GridSort } from '../../core/browse/browse-url';
import type { GridRow } from '../browse/grid/grid-columns';
import {
  PagedDatasource,
  type PagesEvents,
  type PrimedRows,
  type RowsPage,
} from '../browse/grid/paged-datasource';

export type QueryParameterInput = Schema<'QueryParameterInput'>;
export type QueryPageDto = Schema<'QueryPageDto'>;
export type ResultSchema = Schema<'ResultSchemaDto'>;
export type ResultColumn = Schema<'ResultColumnDto'>;
export type ColumnLink = Schema<'ColumnLinkDto'>;
export type QueryStats = Schema<'QueryStatsDto'>;
export type Diagnostic = Schema<'DiagnosticDto'>;
export type QueryParameter = Schema<'QueryParameterDto'>;

/** What a grid of a query's rows asks for: the query, its parameters' values, and the grid's filters and sort. */
export interface ResultsQuery {
  readonly text: string;
  readonly parameters: readonly QueryParameterInput[];
  readonly filters: readonly GridFilter[];
  readonly sort: readonly GridSort[];
}

/** A page of a query's rows, as the grid holds them, with what running it took and what was wrong. */
export interface ResultsPage extends RowsPage {
  /** The query that gave the rows (the grid's filters and sort composed onto it), and its parameters. */
  readonly queryText: string;
  readonly parameters: readonly QueryParameter[];
  readonly schema: ResultSchema | null;
  readonly stats: QueryStats;
  readonly warnings: readonly Diagnostic[];
}

export type PrimedResults = PrimedRows<ResultsQuery, ResultsPage>;
export type ResultsEvents = PagesEvents<PagedDatasource<ResultsQuery, ResultsPage>>;

/** A page as the API gives it, its rows as the grid holds them (values by ordinal, hidden columns' too). */
export function resultsPageOf(page: QueryPageDto): ResultsPage {
  return {
    ...page,
    rows: page.rows.map((row): GridRow => ({ id: row.id, k: null, v: row.v, r: null })),
  };
}

/** Whether two queries ask for the same rows in the same order. */
export function sameResults(a: ResultsQuery, b: ResultsQuery): boolean {
  return JSON.stringify(normal(a)) === JSON.stringify(normal(b));
}

function normal(query: ResultsQuery) {
  return {
    text: query.text,
    parameters: query.parameters.map((parameter) => [
      parameter.name,
      parameter.type ?? null,
      parameter.value ?? null,
    ]),
    filters: query.filters.map((filter) => ({
      column: filter.column,
      any: filter.conditions.length > 1 && filter.any,
      conditions: filter.conditions.map((condition) => [
        condition.op,
        condition.value ?? null,
        condition.valueTo ?? null,
      ]),
    })),
    sort: query.sort.map((key) => [key.column, key.desc]),
  };
}

/** The grid's pages of a query's rows (see `PagedDatasource`); the page it gave last, for its links. */
export class QueryDatasource extends PagedDatasource<ResultsQuery, ResultsPage> {
  private lastPage: ResultsPage | null;

  constructor(
    private readonly api: ApiClient,
    query: ResultsQuery,
    events: ResultsEvents,
    primed: PrimedResults | null = null,
  ) {
    super(query, events, primed, sameResults);
    this.lastPage = primed && sameResults(primed.query, query) ? primed.page : null;
  }

  /** The page given last (or the one fetched ahead): the query its rows' links are followed from. */
  get last(): ResultsPage | null {
    return this.lastPage;
  }

  protected override took(page: ResultsPage): void {
    this.lastPage = page;
  }

  protected fetch(offset: number, limit: number, includeCount: boolean): Observable<ResultsPage> {
    return this.api
      .post('/api/query/execute', {
        body: {
          text: this.query.text,
          parameters: [...this.query.parameters],
          grid: {
            filters: this.query.filters.map((filter) => ({
              ...filter,
              conditions: [...filter.conditions],
            })),
            sort: [...this.query.sort],
            offset,
            limit,
          },
          includeSchema: false,
          includeCount,
        },
      })
      .pipe(map(resultsPageOf));
  }
}
