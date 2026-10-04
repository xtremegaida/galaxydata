import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import type { IGetRowsParams } from 'ag-grid-community';
import { orderRows, pageOf, pageUrl } from '../../../../testing/browse';
import { problemBody } from '../../../../testing/auth';
import { ApiClient } from '../../../core/api/api-client';
import type { Problem } from '../../../core/api/problem';
import {
  BrowseDatasource,
  type BrowseQuery,
  type PrimedPage,
  type RowCount,
  sameQuery,
} from './browse-datasource';

describe('BrowseDatasource', () => {
  const query: BrowseQuery = {
    source: { entity: 'shop.orders' },
    filters: [{ column: 'status', conditions: [{ op: 'eq', value: 'open' }], any: false }],
    where: null,
    sort: [{ column: 'id', desc: true }],
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
  });

  /** A datasource of `query`, what it says, and the grid's asks of it. */
  function datasourceOf(primed: PrimedPage | null = null, of = query) {
    const http = TestBed.inject(HttpTestingController);
    const said: (RowCount | Problem | 'past the end')[] = [];
    const datasource = new BrowseDatasource(
      TestBed.inject(ApiClient),
      of,
      {
        loaded: (_, count) => said.push(count),
        failed: (_, problem) => said.push(problem),
        pastEnd: () => said.push('past the end'),
      },
      primed,
    );
    const ask = (startRow: number, endRow = startRow + 3) => {
      const answers: unknown[] = [];
      const params = {
        startRow,
        endRow,
        sortModel: [],
        filterModel: {},
        successCallback: (rows: unknown[], lastRow?: number) =>
          answers.push(['rows', rows.length, lastRow]),
        failCallback: () => answers.push('failed'),
      } as unknown as IGetRowsParams;
      datasource.getRows(params);
      return answers;
    };
    return { http, said, datasource, ask };
  }

  it('asks for a page of the query, counting the rows with the first', () => {
    const { http, said, ask } = datasourceOf();
    const first = ask(0);
    const request = http.expectOne(pageUrl);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      source: { entity: 'shop.orders' },
      grid: { filters: query.filters, where: null, sort: query.sort, offset: 0, limit: 3 },
      includeSchema: false,
      includeCount: true,
    });
    request.flush(pageOf(orderRows(3), { total: 8, hasMore: true }));
    expect(first).toEqual([['rows', 3, 8]]);
    const second = ask(3);
    const next = http.expectOne(pageUrl);
    expect(next.request.body).toMatchObject({ grid: { offset: 3, limit: 3 }, includeCount: false });
    next.flush(pageOf(orderRows(3, 3), { offset: 3, total: null, hasMore: true }));
    expect(second).toEqual([['rows', 3, 8]]);
    expect(said).toEqual([
      { kind: 'counted', rows: 8 },
      { kind: 'counted', rows: 8 },
    ]);
  });

  it("says the rows go on when they weren't counted, and how many when the last page comes", () => {
    const { http, said, ask } = datasourceOf();
    expect(ask(0)).toEqual([]);
    http.expectOne(pageUrl).flush(pageOf(orderRows(3), { total: null, hasMore: true }));
    const last = ask(3);
    http
      .expectOne(pageUrl)
      .flush(pageOf(orderRows(2, 3), { offset: 3, total: null, hasMore: false }));
    expect(last).toEqual([['rows', 2, 5]]);
    expect(said).toEqual([
      { kind: 'atLeast', rows: 4 },
      { kind: 'counted', rows: 5 },
    ]);
  });

  it('counts the rows from their total on a page past their end, and from the last page otherwise', () => {
    const { http, said, ask } = datasourceOf();
    expect(ask(12)).toEqual([]);
    http.expectOne(pageUrl).flush(pageOf([], { offset: 12, total: 7, hasMore: false }));
    const last = ask(3);
    http
      .expectOne(pageUrl)
      .flush(pageOf(orderRows(2, 3), { offset: 3, total: null, hasMore: false }));
    expect(last).toEqual([['rows', 2, 5]]);
    expect(said).toEqual([
      { kind: 'counted', rows: 7 },
      { kind: 'counted', rows: 5 },
    ]);
  });

  it("says a page is past the rows' end when they weren't counted, answering the grid", () => {
    const { http, said, ask, datasource } = datasourceOf();
    const past = ask(12);
    http.expectOne(pageUrl).flush(pageOf([], { offset: 12, total: null, hasMore: false }));
    expect(past).toEqual([['rows', 0, -1]]);
    expect(said).toEqual(['past the end']);
    expect(datasource.count).toBeNull();
  });

  it('asks for the count again when the page it was asked with failed', () => {
    const { http, ask } = datasourceOf();
    ask(0);
    http
      .expectOne(pageUrl)
      .flush(problemBody('query-timeout', 'Too long'), { status: 504, statusText: 'Timeout' });
    ask(0);
    expect(http.expectOne(pageUrl).request.body).toMatchObject({ includeCount: true });
  });

  it('takes the rows going on past their count for more than were counted', () => {
    const { http, said, ask } = datasourceOf();
    ask(0);
    http.expectOne(pageUrl).flush(pageOf(orderRows(3), { total: 3, hasMore: true }));
    expect(said).toEqual([{ kind: 'atLeast', rows: 4 }]);
  });

  it('answers the first ask for the page fetched ahead of it, once, without counting again', () => {
    const primed: PrimedPage = {
      query,
      page: pageOf(orderRows(3, 3), { offset: 3, total: 9, hasMore: true }),
      limit: 3,
    };
    const { http, ask } = datasourceOf(primed);
    expect(ask(3)).toEqual([['rows', 3, 9]]);
    http.expectNone(pageUrl);
    ask(3);
    expect(http.expectOne(pageUrl).request.body).toMatchObject({ includeCount: false });
    // Another grid's datasource doesn't take it again: it is as old as the grid that took it.
    const another = datasourceOf(primed);
    another.ask(3);
    expect(another.http.expectOne(pageUrl).request.body).toMatchObject({ includeCount: true });
  });

  it("doesn't take a page fetched ahead for another query, or another page", () => {
    const primed: PrimedPage = {
      query: { ...query, where: 'x > 1' },
      page: pageOf(orderRows(3)),
      limit: 3,
    };
    const { http, ask } = datasourceOf(primed);
    ask(0);
    http.expectOne(pageUrl);
    const other = datasourceOf({ query, page: pageOf(orderRows(3)), limit: 3 });
    other.ask(0, 6);
    expect(other.http.match(pageUrl).length).toBe(1);
  });

  it('asks once for a page asked for twice while it is fetched', () => {
    const { http, ask } = datasourceOf();
    const first = ask(0);
    const second = ask(0);
    http.expectOne(pageUrl).flush(pageOf(orderRows(3)));
    expect([first, second]).toEqual([[['rows', 3, 3]], [['rows', 3, 3]]]);
  });

  it('lets go of asks under way when retired, failing them for the grid, and fails later ones without asking', () => {
    const { http, datasource, ask } = datasourceOf();
    const asked = ask(0);
    const request = http.expectOne(pageUrl);
    datasource.destroy();
    expect(request.cancelled).toBe(true);
    expect(asked).toEqual(['failed']);
    expect(ask(3)).toEqual(['failed']);
    http.expectNone(pageUrl);
    datasource.retire();
    expect(asked).toEqual(['failed']);
  });

  it('says why a page failed: nothing shown when nothing was read, a later page failed', () => {
    const { http, said, ask } = datasourceOf();
    const first = ask(0);
    http
      .expectOne(pageUrl)
      .flush(problemBody('query-failed', 'No'), { status: 422, statusText: 'No' });
    expect(first).toEqual([['rows', 0, 0]]);
    expect(said).toEqual([expect.objectContaining({ status: 422, code: 'query-failed' })]);
    ask(0);
    const again = http.expectOne(pageUrl);
    expect(again.request.body).toMatchObject({ includeCount: true });
    again.flush(pageOf(orderRows(3), { total: 6, hasMore: true }));
    const later = ask(3);
    http.expectOne(pageUrl).flush(null, { status: 0, statusText: 'Unknown Error' });
    expect(later).toEqual(['failed']);
  });

  it('tells queries apart by what they ask for, not how they write it', () => {
    expect(sameQuery(query, { ...query, source: { entity: 'shop.orders', from: null } })).toBe(
      true,
    );
    expect(sameQuery({ ...query, where: '  ' }, query)).toBe(true);
    expect(sameQuery({ ...query, filters: [{ ...query.filters[0], any: true }] }, query)).toBe(
      true,
    );
    expect(sameQuery({ ...query, where: 'a > 1' }, query)).toBe(false);
    expect(sameQuery({ ...query, sort: [] }, query)).toBe(false);
    expect(
      sameQuery(
        { ...query, filters: [{ ...query.filters[0], conditions: [{ op: 'ne', value: 'open' }] }] },
        query,
      ),
    ).toBe(false);
    expect(sameQuery({ ...query, source: { entity: 'shop.customers' } }, query)).toBe(false);
  });
});
