import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { By } from '@angular/platform-browser';
import { DefaultUrlSerializer, provideRouter } from '@angular/router';
import { AgGridAngular } from 'ag-grid-angular';
import { problemBody } from '../../../testing/auth';
import { gridCells, gridHeaders, pagesFetched } from '../../../testing/browse';
import { requestTo, settle } from '../../../testing/http';
import { alertsOf, clickButton, textOf } from '../../../testing/pages';
import {
  executeUrl,
  linkUrl,
  orderResultRows,
  queryPageOf,
  resultColumnOf,
  type QueryPageDto,
} from '../../../testing/query';
import {
  catalogVersionHeader,
  catalogVersionInterceptor,
} from '../../core/catalog/catalog-version';
import { BROWSE_PAGE_SIZE } from '../browse/grid/grid-settings';
import {
  type FollowedLink,
  type QueryRun,
  QueryResults,
  type RunOutcome,
  followedOf,
} from './query-results';

@Component({
  imports: [QueryResults],
  template: `<gd-query-results
    [run]="run()"
    (followed)="followed.push($event)"
    (outcome)="outcomes.push($event)"
  />`,
})
class Host {
  readonly run = signal<QueryRun>({
    text: 'shop.orders.select(id, customer, total)',
    parameters: [{ name: 'min', type: 'decimal', value: '50' }],
    serial: 1,
  });
  readonly followed: FollowedLink[] = [];
  readonly outcomes: RunOutcome[] = [];
}

const serializer = new DefaultUrlSerializer();

describe('QueryResults', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([catalogVersionInterceptor])),
        provideHttpClientTesting(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: BROWSE_PAGE_SIZE, useValue: 3 },
        provideRouter([]),
      ],
    });
  });

  afterEach(async () => {
    await pagesFetched();
    try {
      TestBed.inject(HttpTestingController).verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  async function open() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const element = fixture.nativeElement as HTMLElement;
    const shown = async () => {
      for (let turn = 0; turn < 3; turn++) {
        await settle();
        fixture.detectChanges();
      }
      await pagesFetched();
      fixture.detectChanges();
    };
    /** Answers the next request for rows; its body. */
    const answer = async (page: QueryPageDto, version = 'v1') => {
      const request = await requestTo(http, executeUrl, 'POST');
      request.flush(page, { headers: { [catalogVersionHeader]: version } });
      await shown();
      return request.request.body as Record<string, unknown>;
    };
    const grid = () =>
      fixture.debugElement.query(By.directive(AgGridAngular))?.componentInstance as AgGridAngular;
    return { fixture, host: fixture.componentInstance, http, element, shown, answer, grid };
  }

  /** The run's rows shown: 5 of them, 3 a page. */
  async function opened() {
    const it = await open();
    const body = await it.answer(
      queryPageOf(orderResultRows(3), { total: 5, hasMore: true, warnings: [] }),
    );
    return { ...it, body };
  }

  it('runs the query for its first page, with its columns and count, and shows them (but hidden ones)', async () => {
    const { element, body, host } = await opened();
    expect(body).toEqual({
      text: 'shop.orders.select(id, customer, total)',
      parameters: [{ name: 'min', type: 'decimal', value: '50' }],
      grid: { offset: 0, limit: 3 },
      includeSchema: true,
      includeCount: true,
    });
    expect(gridHeaders(element)).toEqual(['id', 'customer', 'total']);
    expect(gridCells(element)).toEqual([
      ['1001', 'Acme', '0.50'],
      ['1002', 'NULL', '1.50'],
      ['1003', 'Acme', '2.50'],
    ]);
    expect(textOf(element.querySelector('.count'))).toBe('5 rows in 13 ms');
    expect(host.outcomes).toEqual([
      {
        kind: 'read',
        run: host.run(),
        stats: { elapsedMs: 12.5, fetchedRows: 3, keysSent: 0, fragments: [] },
        warnings: [],
      },
    ]);
    // Values that lead somewhere are links; a NULL leads nowhere.
    const customers = [...element.querySelectorAll('[col-id=c1] .ag-cell-value > span')];
    expect(customers.map((cell) => [cell.className, cell.getAttribute('role')])).toEqual([
      ['gd-link', 'link'],
      ['', null],
      ['gd-link', 'link'],
    ]);
  });

  it('asks for the rows sorted and filtered in the grid, and for later pages', async () => {
    const { grid, answer, element, host } = await opened();
    grid().api.applyColumnState({ state: [{ colId: 'c2', sort: 'desc' }] });
    const sorted = await answer(queryPageOf(orderResultRows(3), { total: 5, hasMore: true }, null));
    expect(sorted).toEqual({
      text: 'shop.orders.select(id, customer, total)',
      parameters: [{ name: 'min', type: 'decimal', value: '50' }],
      grid: { filters: [], sort: [{ column: 'total', desc: true }], offset: 0, limit: 3 },
      includeSchema: false,
      includeCount: true,
    });
    expect(host.outcomes.at(-1)).toMatchObject({ kind: 'read' });

    grid().api.paginationGoToNextPage();
    const next = await answer(
      queryPageOf(orderResultRows(2, 3), { offset: 3, total: null, hasMore: false }, null),
    );
    expect(next['grid']).toMatchObject({ offset: 3, limit: 3 });
    expect(next['includeCount']).toBe(false);
    expect(gridCells(element)).toEqual([
      ['1004', 'NULL', '3.50'],
      ['1005', 'Acme', '4.50'],
    ]);
  });

  it("says why the query couldn't run, or a page couldn't be read (that page asked for again)", async () => {
    const it = await open();
    const request = await requestTo(it.http, executeUrl, 'POST');
    request.flush(
      problemBody('query-failed', 'The query failed', { detail: 'no such table: orders' }),
      {
        status: 422,
        statusText: 'Unprocessable',
      },
    );
    await it.shown();
    expect(alertsOf(it.element)).toContain(
      "The query couldn't run: The query failed. no such table: orders.",
    );
    expect(it.host.outcomes).toMatchObject([{ kind: 'failed', problem: { code: 'query-failed' } }]);
  });

  it('asks a page that failed for again', async () => {
    const { grid, http, element, answer, shown } = await opened();
    grid().api.paginationGoToNextPage();
    const failed = await requestTo(http, executeUrl, 'POST');
    failed.flush(null, { status: 502, statusText: 'Bad gateway' });
    await shown();
    expect(alertsOf(element)).toContain("Couldn't read the rows");
    clickButton(element, 'Try again');
    const again = await answer(
      queryPageOf(orderResultRows(2, 3), { offset: 3, hasMore: false, total: null }, null),
    );
    expect(again['grid']).toMatchObject({ offset: 3 });
    expect(alertsOf(element)).toBe('');
  });

  it('stops a run before its rows come', async () => {
    const it = await open();
    const request = await requestTo(it.http, executeUrl, 'POST');
    press(it.element, 'Stop');
    await it.shown();
    expect(request.cancelled).toBe(true);
    expect(it.host.outcomes).toEqual([{ kind: 'stopped', run: it.host.run() }]);
    expect(textOf(it.element.querySelector('[role=status] gd-message'))).toContain(
      'Stopped before its rows came. Run it again to see them.',
    );
    // A run anew isn't stopped.
    it.host.run.set({ ...it.host.run(), serial: 2 });
    it.fixture.detectChanges();
    await it.answer(queryPageOf(orderResultRows(1)));
    expect(it.element.querySelector('[role=status] gd-message')).toBeNull();
  });

  it('follows a link of a row: where it leads, the server says', async () => {
    const { element, http, host, shown } = await opened();
    element.querySelector<HTMLElement>('[col-id=c1] .gd-link')!.click();
    const request = await requestTo(http, linkUrl, 'POST');
    expect(request.request.body).toEqual({
      text: 'shop.orders.select(id, customer, total)',
      parameters: [],
      row: ['1001', 'Acme', '0.50', '42'],
      column: 1,
      related: null,
      catalogVersion: 'v1',
    });
    request.flush({
      queryText: 'shop.customers.where(id == $key1)',
      parameters: [{ name: 'key1', type: 'int64', value: '42' }],
      browse: { entity: 'shop.customers' },
      key: ['42'],
    });
    await shown();
    expect(host.followed.map(shownOf)).toEqual(['browse /browse/shop.customers;row=42']);

    // Enter on the cell follows it too.
    const cell = element.querySelector<HTMLElement>('.ag-row[row-index="2"] [col-id=c1]')!;
    cell.focus();
    cell.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    const again = await requestTo(http, linkUrl, 'POST');
    expect(again.request.body).toMatchObject({ column: 1, row: ['1003', 'Acme', '2.50', '42'] });
    again.flush({ queryText: null, parameters: [], browse: null, key: null });
    await shown();
    expect(alertsOf(element)).toContain('It leads to no rows. Dismiss');
  });

  it("follows a link from the page shown, once at a time; asks nothing anew for the grid's state unchanged", async () => {
    const { element, http, grid, answer, shown } = await opened();
    grid().api.applyColumnState({ state: [{ colId: 'c2', sort: 'desc' }] });
    await answer(
      queryPageOf(
        orderResultRows(3),
        {
          queryText: '(shop.orders.select(id, customer, total)).orderBy(desc(total))',
          total: 5,
          hasMore: true,
        },
        null,
      ),
    );
    const link = element.querySelector<HTMLElement>('[col-id=c1] .gd-link')!;
    link.click();
    link.click();
    const asked = await requestTo(http, linkUrl, 'POST');
    expect(asked.request.body).toMatchObject({
      text: '(shop.orders.select(id, customer, total)).orderBy(desc(total))',
    });
    asked.flush({ queryText: null, parameters: [], browse: null, key: null });
    await shown();
    // The filters said to have changed, but the same: the grid's own ask is answered by the same query, not one
    // counted anew.
    grid().api.onFilterChanged();
    await shown();
    const again = await answer(queryPageOf(orderResultRows(3), { total: 5, hasMore: true }, null));
    expect(again['includeCount']).toBe(false);
  });

  it('follows no link answered for rows no longer shown', async () => {
    const { element, http, host, shown, grid, answer } = await opened();
    element.querySelector<HTMLElement>('[col-id=c1] .gd-link')!.click();
    const link = await requestTo(http, linkUrl, 'POST');
    grid().api.applyColumnState({ state: [{ colId: 'c2', sort: 'desc' }] });
    await answer(queryPageOf(orderResultRows(3), { total: 5, hasMore: true }, null));
    link.flush({
      queryText: 'x',
      parameters: [],
      browse: { entity: 'shop.customers' },
      key: ['42'],
    });
    await shown();
    expect(host.followed).toEqual([]);
    expect(alertsOf(element)).toBe('');
  });

  it('says nothing of the run before as another runs', async () => {
    const { element, host, fixture, http, shown, answer } = await opened();
    expect(textOf(element.querySelector('.count'))).toBe('5 rows in 13 ms');
    element.querySelector<HTMLElement>('[col-id=c1] .gd-link')!.click();
    (await requestTo(http, linkUrl, 'POST')).flush({
      queryText: null,
      parameters: [],
      browse: null,
      key: null,
    });
    await shown();
    expect(alertsOf(element)).toContain('It leads to no rows.');
    host.run.set({ ...host.run(), serial: 2 });
    fixture.detectChanges();
    await shown();
    expect(textOf(element.querySelector('.count'))).toBe('');
    expect(alertsOf(element)).toBe('');
    await answer(queryPageOf(orderResultRows(1)));
    expect(textOf(element.querySelector('.count'))).toBe('1 row in 13 ms');
  });

  it("says when the catalog changed since the rows were read, so links can't be followed", async () => {
    const { element, http, shown } = await opened();
    element.querySelector<HTMLElement>('[col-id=c1] .gd-link')!.click();
    const request = await requestTo(http, linkUrl, 'POST');
    request.flush(problemBody('concurrency-conflict', 'The catalog changed'), {
      status: 409,
      statusText: 'Conflict',
    });
    await shown();
    expect(alertsOf(element)).toContain('The catalog changed since the rows were read');
    clickButton(element, 'Dismiss');
    await shown();
    expect(alertsOf(element)).toBe('');
  });

  it("leads to the rows that refer to an entity's rows, and inspects cells", async () => {
    const it = await open();
    await it.answer({
      ...queryPageOf([{ id: '["1001"]', v: ['1001', 'open'] }]),
      schema: {
        columns: [resultColumnOf('id', 0, 'int64'), resultColumnOf('status', 1)],
        rowIdentity: {
          entity: 'shop.orders',
          keyOrdinals: [0],
          related: [
            {
              kind: 'collection',
              ordinals: [0],
              target: 'shop.order_lines',
              navigation: 'order_lines',
              multiplicity: 'many',
            },
          ],
          capabilities: { canInsert: false, canUpdate: false, canDelete: false },
        },
      },
    });
    expect(gridHeaders(it.element)).toEqual(['id', 'status', 'order_lines']);
    expect(gridCells(it.element)).toEqual([['1001', 'open', 'order_lines ›']]);
    expect(it.element.querySelector('.ag-header-cell[col-id=c0]')?.classList).toContain(
      'gd-key-column',
    );
    it.element.querySelector<HTMLElement>('[col-id=n0] .gd-link')!.click();
    const request = await requestTo(it.http, linkUrl, 'POST');
    expect(request.request.body).toMatchObject({ column: null, related: 0, row: ['1001', 'open'] });
    request.flush({
      queryText: 'shop.order_lines.where(order_id == $key1)',
      parameters: [],
      browse: { from: { entity: 'shop.orders', key: ['1001'] }, navigation: 'order_lines' },
      key: null,
    });
    await it.shown();
    expect(it.host.followed.map(shownOf)).toEqual([
      'browse /browse/shop.orders;row=1001/order_lines',
    ]);

    it.grid().api.setFocusedCell(0, 'n0');
    await it.shown();
    expect(textOf(it.element.querySelector('gd-grid-inspector'))).toContain(
      'The rows of shop.order_lines that refer to this row.',
    );
  });

  it('inspects where a value leads', async () => {
    const { grid, element, shown } = await opened();
    grid().api.setFocusedCell(0, 'c1');
    await shown();
    const inspector = element.querySelector('gd-grid-inspector')!;
    expect(partsOf(inspector.querySelector('[aria-labelledby=gd-inspector-leads]')!)).toEqual([
      'Leads to',
      'The row of shop.customers it refers to, through customer.',
      'Enter, or a click on the link, shows them.',
    ]);
    grid().api.setFocusedCell(1, 'c1');
    await shown();
    expect(partsOf(inspector.querySelector('[aria-labelledby=gd-inspector-leads]')!)).toEqual([
      'Leads to',
      'The row of shop.customers it refers to, through customer.',
      'A NULL leads nowhere.',
    ]);
  });

  it('goes where links lead: rows to browse, else a query of them', () => {
    expect(
      shownOf(
        followedOf({
          queryText: 'x',
          parameters: [],
          browse: { entity: 'shop.customers' },
          key: [42, 'x'],
        })!,
      ),
    ).toBe('browse /browse/shop.customers;row=42~x');
    expect(
      followedOf({
        queryText: '(shop.orders).where(status == $key1)',
        parameters: [
          { name: 'key1', type: 'string', value: 'open' },
          { name: 'min', type: 'decimal(10,2)', value: '50.00' },
          { name: 'n', type: 'int32', value: 5 },
          { name: 'none', type: 'string', value: null },
        ],
        browse: null,
        key: null,
      }),
    ).toEqual({
      kind: 'query',
      text: '(shop.orders).where(status == $key1)',
      values: { key1: 'open', min: '50.00', n: '5', none: null },
    });
    expect(followedOf({ queryText: null, parameters: [], browse: null, key: null })).toBeNull();
  });
});

/** The texts of an element's parts. */
function partsOf(element: Element): string[] {
  return [...element.children].map((part) => textOf(part));
}

/** Clicks the button whose text (its icon's aside) is `text`. */
function press(container: ParentNode, text: string): void {
  const button = [...container.querySelectorAll<HTMLButtonElement>('button')].find((candidate) =>
    textOf(candidate).endsWith(text),
  );
  if (!button) {
    throw new Error(`No button "${text}"`);
  }
  button.click();
}

/** Where a link followed leads, as text. */
function shownOf(link: FollowedLink): string {
  return link.kind === 'browse'
    ? `browse ${serializer.serialize(link.url)}`
    : `query ${link.text} ${JSON.stringify(link.values)}`;
}
