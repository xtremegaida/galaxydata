import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { APP_BASE_HREF } from '@angular/common';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { AgGridAngular } from 'ag-grid-angular';
import {
  answerPage,
  columnOf,
  gridCells,
  gridHeaders,
  linkedPageOf,
  linkedRows,
  orderColumns,
  orderRows,
  pageOf,
  pageRequest,
  pagesFetched,
  rowOf,
} from '../../../../testing/browse';
import { problemBody } from '../../../../testing/auth';
import { settle } from '../../../../testing/http';
import { alertsOf, clickButton, textOf } from '../../../../testing/pages';
import { type BrowseCrumb, crumbOf } from '../../../core/browse/browse-url';
import { CatalogVersion, catalogVersionInterceptor } from '../../../core/catalog/catalog-version';
import type { BrowseSource } from './browse-datasource';
import { BROWSE_PAGE_SIZE, BrowseGrid, type LinkTo, whereProblemsOf } from './browse-grid';
import { keyOf } from './grid-columns';

/** The grid as browsing has it: its state the address's, which follows what the grid says. */
@Component({
  imports: [BrowseGrid],
  template: `<gd-browse-grid
    [source]="source()"
    [crumb]="crumb()"
    [linkTo]="linkTo()"
    [focusChosen]="focusChosen()"
    (crumbChange)="said($event)"
  />`,
})
class Host {
  readonly source = signal<BrowseSource>({ entity: 'shop.orders' });
  readonly crumb = signal<BrowseCrumb>(crumbOf('shop.orders'));
  readonly linkTo = signal<LinkTo | null>(null);
  readonly focusChosen = signal(false);
  readonly says: BrowseCrumb[] = [];

  said(crumb: BrowseCrumb): void {
    this.says.push(crumb);
    this.crumb.set(crumb);
  }
}

/** What is asked and not answered: each request's body. */
function pending(http: HttpTestingController): unknown[] {
  return http.match(() => true).map((request) => request.request.body);
}

describe('BrowseGrid', () => {
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

  /** The grid at an address's state, its first page asked for (not answered); its links lead to `linkTo`. */
  async function open(crumb = crumbOf('shop.orders'), linkTo: LinkTo | null = null) {
    const fixture = TestBed.createComponent(Host);
    const host = fixture.componentInstance;
    host.crumb.set(crumb);
    host.linkTo.set(linkTo);
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
    const answer = async (
      ...args: Parameters<typeof answerPage> extends [unknown, ...infer R] ? R : never
    ) => {
      const body = await answerPage(http, ...args);
      await shown();
      return body;
    };
    const grid = () =>
      fixture.debugElement.query(By.directive(AgGridAngular))?.componentInstance as AgGridAngular;
    return { fixture, host, http, element, shown, answer, grid };
  }

  /** The grid shown with the first page of shop.orders' rows: 7 rows, 3 a page. */
  async function opened(crumb = crumbOf('shop.orders')) {
    const it = await open(crumb);
    const body = await it.answer(
      pageOf(
        orderRows(3, crumb.page * 3),
        { offset: crumb.page * 3, total: 7, hasMore: true },
        orderColumns(),
      ),
    );
    return { ...it, body };
  }

  it("asks for the address's rows with their schema and count, and shows them without asking again", async () => {
    const { element, body, http, host } = await opened(
      crumbOf('shop.orders', {
        filters: [{ column: 'status', conditions: [{ op: 'eq', value: 'open' }], any: false }],
        where: 'total > 1',
        sort: [{ column: 'total', desc: true }],
        page: 1,
      }),
    );
    expect(body).toEqual({
      source: { entity: 'shop.orders' },
      grid: {
        filters: [{ column: 'status', conditions: [{ op: 'eq', value: 'open' }], any: false }],
        where: 'total > 1',
        sort: [{ column: 'total', desc: true }],
        offset: 3,
        limit: 3,
      },
      includeSchema: true,
      includeCount: true,
    });
    expect(gridHeaders(element)).toEqual(['id', 'status', 'total']);
    expect(gridCells(element)).toEqual([
      ['1004', 'NULL', '3.50'],
      ['1005', 'open', '4.50'],
      ['1006', 'NULL', '5.50'],
    ]);
    expect(textOf(element.querySelector('.count'))).toBe('7 rows');
    expect(textOf(element.querySelector('.ag-paging-panel'))).toContain('4 to 6 of 7');
    expect(element.querySelector('.ag-header-cell[col-id=c0]')?.classList).toContain(
      'gd-key-column',
    );
    expect(element.querySelector('.ag-row [col-id=c1]')?.classList).toContain('gd-null');
    expect(element.querySelector('.ag-row [col-id=c2]')?.classList).toContain('gd-number');
    expect(host.says).toEqual([]);
    expect(pending(http)).toEqual([]);
  });

  it('sorts by a header: the first page of the rows so, said for the address', async () => {
    const { element, host, http, answer } = await opened();
    element.querySelector<HTMLElement>('.ag-header-cell[col-id=c2] .ag-header-cell-label')!.click();
    const body = await answer(pageOf(orderRows(3), { total: 7, hasMore: true }));
    expect(body).toMatchObject({
      grid: { sort: [{ column: 'total', desc: false }], offset: 0, limit: 3 },
      includeSchema: false,
      includeCount: true,
    });
    expect(host.says).toEqual([
      crumbOf('shop.orders', { sort: [{ column: 'total', desc: false }] }),
    ]);
    expect(pending(http)).toEqual([]);
  });

  it("filters by the columns' filters, from the first page", async () => {
    const { host, grid, answer } = await opened(crumbOf('shop.orders', { page: 1 }));
    grid().api.setFilterModel({ c1: { filterType: 'text', type: 'startsWith', filter: 'op' } });
    const body = await answer(pageOf(orderRows(1), { total: 1 }));
    expect(body.grid).toEqual({
      filters: [{ column: 'status', conditions: [{ op: 'startsWith', value: 'op' }], any: false }],
      where: null,
      sort: [],
      offset: 0,
      limit: 3,
    });
    expect(host.crumb()).toEqual(
      crumbOf('shop.orders', {
        filters: [
          { column: 'status', conditions: [{ op: 'startsWith', value: 'op' }], any: false },
        ],
      }),
    );
  });

  it('pages, counting once, and says the page for the address', async () => {
    const { element, host, answer, http } = await opened();
    element.querySelector<HTMLElement>('.ag-paging-button[aria-label="Next Page"]')!.click();
    const body = await answer(pageOf(orderRows(3, 3), { offset: 3, total: null, hasMore: true }));
    expect(body).toMatchObject({ grid: { offset: 3, limit: 3 }, includeCount: false });
    expect(host.crumb().page).toBe(1);
    expect(gridCells(element)[0]).toEqual(['1004', 'NULL', '3.50']);
    expect(pending(http)).toEqual([]);
  });

  it("goes to the rows' last page when the address's page is past them, and says so", async () => {
    const { element, host, answer } = await open(crumbOf('shop.orders', { page: 4 }));
    const first = await answer(
      pageOf([], { offset: 12, total: 7, hasMore: false }, orderColumns()),
    );
    expect(first.grid).toMatchObject({ offset: 12 });
    const last = await answer(pageOf(orderRows(1, 6), { offset: 6, total: null }));
    expect(last.grid).toMatchObject({ offset: 6 });
    expect(host.crumb().page).toBe(2);
    expect(gridCells(element)).toEqual([['1007', 'NULL', '6.50']]);
  });

  it("says when the rows couldn't be counted in time: at least those seen", async () => {
    const { element, answer } = await open();
    await answer(pageOf(orderRows(3), { total: null, hasMore: true }, orderColumns()));
    expect(textOf(element.querySelector('.count'))).toBe('At least 4 rows');
    expect(textOf(element.querySelector('.ag-paging-panel'))).toContain('1 to 3 of more');
  });

  it('chooses a row: its key, for the address; the address chooses it too', async () => {
    const { element, host, fixture, shown } = await opened();
    element.querySelector<HTMLElement>('.ag-row[row-index="1"] [col-id=c1]')!.click();
    await shown();
    expect(host.crumb().row).toEqual(['1002']);
    host.crumb.set({ ...host.crumb(), row: ['1003'] });
    fixture.detectChanges();
    await shown();
    expect(
      [...element.querySelectorAll('.ag-row-selected')].map((row) => row.getAttribute('row-index')),
    ).toEqual(['2']);
    expect(host.says.length).toBe(1);
  });

  it('chooses the row the keyboard is on with Space', async () => {
    const { element, host, grid, shown } = await opened();
    grid().api.setFocusedCell(2, 'c1');
    await shown();
    element
      .querySelector('.ag-cell-focus')!
      .dispatchEvent(
        new KeyboardEvent('keydown', { key: ' ', code: 'Space', bubbles: true, cancelable: true }),
      );
    await shown();
    expect(host.crumb().row).toEqual(['1003']);
  });

  it('chooses the row the address says when its page comes, in view, the keyboard left where it was', async () => {
    const { element } = await opened(crumbOf('shop.orders', { row: ['1002'] }));
    expect(
      [...element.querySelectorAll('.ag-row-selected')].map((row) => row.getAttribute('row-index')),
    ).toEqual(['1']);
    // Scrolled to: the second row is below the top.
    expect([...element.querySelectorAll('*')].some((part) => part.scrollTop > 0)).toBe(true);
    expect(element.contains(document.activeElement)).toBe(false);
  });

  it('follows the address to another page in place, and to other filters with a grid made for them', async () => {
    const { host, fixture, answer, shown, grid, http } = await opened();
    const first = grid();
    host.crumb.set({ ...host.crumb(), page: 2 });
    fixture.detectChanges();
    await shown();
    expect((await answer(pageOf(orderRows(1, 6), { offset: 6, total: null }))).grid).toMatchObject({
      offset: 6,
    });
    expect(grid()).toBe(first);
    host.crumb.set(crumbOf('shop.orders', { where: 'total > 2', page: 1 }));
    fixture.detectChanges();
    await shown();
    const body = await answer(pageOf(orderRows(2, 3), { offset: 3, total: 5 }));
    expect(body).toMatchObject({ grid: { where: 'total > 2', offset: 3 }, includeCount: true });
    expect(grid()).not.toBe(first);
    expect(host.says).toEqual([]);
    expect(pending(http)).toEqual([]);
  });

  it('filters by a condition in the query language, applied on Enter, and cleared', async () => {
    const { element, host, answer, shown, http } = await opened();
    const field = element.querySelector<HTMLInputElement>('.where-field input')!;
    field.value = "status == 'open'";
    field.dispatchEvent(new Event('input'));
    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    const body = await answer(pageOf(orderRows(1), { total: 1 }));
    expect(body.grid).toMatchObject({ where: "status == 'open'", offset: 0 });
    expect(host.crumb().where).toBe("status == 'open'");
    const apply = [...element.querySelectorAll('button')].find(
      (button) => textOf(button) === 'Apply',
    )!;
    expect(apply.getAttribute('aria-disabled')).toBe('true');
    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await shown();
    expect(pending(http)).toEqual([]);
    clickButton(element, 'close');
    await shown();
    expect(
      (await answer(pageOf(orderRows(3), { total: 7, hasMore: true }))).grid?.where,
    ).toBeNull();
    expect(host.crumb().where).toBeNull();
    expect(field.value).toBe('');
    expect(document.activeElement).toBe(field);
  });

  it("says what is wrong with the condition, placed in it, and that the rows can't be shown", async () => {
    const { element, http, shown } = await opened();
    const field = element.querySelector<HTMLInputElement>('.where-field input')!;
    field.value = 'stauts == 1';
    field.dispatchEvent(new Event('input'));
    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    (await pageRequest(http)).flush(
      problemBody('query-invalid', 'The query is not valid', {
        field: 'grid.where',
        diagnostics: [
          {
            code: 'GDQ2001',
            severity: 'error',
            message: "There is no 'stauts' here",
            start: 0,
            end: 6,
          },
        ],
      }),
      { status: 422, statusText: 'Unprocessable' },
    );
    await shown();
    expect(alertsOf(element)).toBe(
      "error The condition can't be used: There is no 'stauts' here (at stauts, from character 1)",
    );
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(textOf(element.querySelector('.ag-overlay'))).toBe("The condition can't be used");
    expect(textOf(element.querySelector('.count'))).toBe('');
  });

  it("says why the rows couldn't be read, and asks again", async () => {
    const { element, http, answer, shown } = await opened();
    element.querySelector<HTMLElement>('.ag-header-cell[col-id=c0] .ag-header-cell-label')!.click();
    await shown();
    (await pageRequest(http)).flush(problemBody('source-unavailable', "Couldn't reach shop"), {
      status: 502,
      statusText: 'Bad Gateway',
    });
    await shown();
    expect(alertsOf(element)).toBe("error Couldn't read the rows: Couldn't reach shop. Try again");
    expect(textOf(element.querySelector('.ag-overlay'))).toBe("Couldn't read the rows");
    clickButton(element, 'Try again');
    await shown();
    const body = await answer(pageOf(orderRows(3), { total: 7, hasMore: true }));
    expect(body).toMatchObject({
      grid: { sort: [{ column: 'id', desc: false }], offset: 0 },
      includeCount: true,
    });
    expect(alertsOf(element)).toBe('');
    expect(element.querySelector('.ag-overlay')?.textContent ?? '').not.toContain("Couldn't");
  });

  it("asks for the schema alone when the address's state can't be asked for, and the grid says why", async () => {
    const { element, http, answer, shown } = await open(
      crumbOf('shop.orders', { where: 'nonsense ==' }),
    );
    (await pageRequest(http)).flush(
      problemBody('invalid-request', 'The request is not valid', {
        errors: { 'grid.where': ['Expected an expression (at 12)'] },
      }),
      { status: 400, statusText: 'Bad Request' },
    );
    await shown();
    const schemaOnly = await answer(pageOf([], {}, orderColumns()));
    expect(schemaOnly).toEqual({
      source: { entity: 'shop.orders' },
      grid: { limit: 1 },
      includeSchema: true,
      includeCount: false,
    });
    (await pageRequest(http)).flush(
      problemBody('invalid-request', 'The request is not valid', {
        errors: { 'grid.where': ['Expected an expression (at 12)'] },
      }),
      { status: 400, statusText: 'Bad Request' },
    );
    await shown();
    expect(alertsOf(element)).toBe(
      "error The condition can't be used: Expected an expression (at 12)",
    );
    expect(element.querySelector<HTMLInputElement>('.where-field input')?.value).toBe(
      'nonsense ==',
    );
  });

  it("says why it couldn't read the rows' schema, and reads it again", async () => {
    const { element, http, answer, shown } = await open();
    (await pageRequest(http)).flush(problemBody('not-found', 'There is no such entity'), {
      status: 404,
      statusText: 'Not Found',
    });
    await shown();
    expect(alertsOf(element)).toBe(
      "error Couldn't read the rows: There is no such entity. Try again",
    );
    expect(element.querySelector('ag-grid-angular')).toBeNull();
    clickButton(element, 'Try again');
    await shown();
    await answer(pageOf(orderRows(3), {}, orderColumns()));
    expect(gridCells(element).length).toBe(3);
  });

  it("says what of the address the grid can't hold, and says what it shows", async () => {
    const { element, host, answer } = await opened(
      crumbOf('shop.orders', {
        filters: [
          { column: 'gone', conditions: [{ op: 'eq', value: '1' }], any: false },
          { column: 'STATUS', conditions: [{ op: 'eq', value: 'open' }], any: false },
        ],
        sort: [{ column: 'nothing', desc: false }],
      }),
    );
    const warning = element.querySelector('gd-message.kind-warning');
    expect([...(warning?.querySelectorAll('li') ?? [])].map((item) => textOf(item))).toEqual([
      'The filter on gone is left out: the rows have no such column.',
      'The sort by nothing is left out: the rows have no such column.',
    ]);
    expect(host.says).toEqual([
      crumbOf('shop.orders', {
        filters: [{ column: 'status', conditions: [{ op: 'eq', value: 'open' }], any: false }],
      }),
    ]);
    // The page asked for ahead was of what the address said: the grid asks for what it shows.
    expect((await answer(pageOf(orderRows(1), { total: 1 }))).grid).toMatchObject({
      filters: [{ column: 'status', conditions: [{ op: 'eq', value: 'open' }], any: false }],
      sort: [],
    });
  });

  it('reads the rows and their count again when the catalog changes, keeping the grid for the same columns', async () => {
    const { element, http, answer, grid } = await opened(crumbOf('shop.orders', { page: 1 }));
    const first = grid();
    TestBed.inject(CatalogVersion).seen('v2');
    const again = await answer(
      pageOf(orderRows(2, 3), { offset: 3, total: 5, hasMore: false }, orderColumns()),
      'v2',
    );
    expect(again).toMatchObject({ includeSchema: true, includeCount: true, grid: { offset: 3 } });
    expect(grid()).toBe(first);
    expect(textOf(element.querySelector('.count'))).toBe('5 rows');
    expect(gridCells(element).map((cells) => cells[0])).toEqual(['1004', '1005']);
    expect(pending(http)).toEqual([]);
  });

  it('lets go of a page under way when the rows are asked for otherwise', async () => {
    const { element, http, shown, answer } = await opened();
    element.querySelector<HTMLElement>('.ag-paging-button[aria-label="Next Page"]')!.click();
    await shown();
    const paging = await pageRequest(http);
    element.querySelector<HTMLElement>('.ag-header-cell[col-id=c1] .ag-header-cell-label')!.click();
    await shown();
    expect(paging.cancelled).toBe(true);
    const body = await answer(pageOf(orderRows(3), { total: 7, hasMore: true }));
    expect(body.grid).toMatchObject({ sort: [{ column: 'status', desc: false }], offset: 0 });
  });

  it("shows what the cell the keyboard is on holds, and where its column's values come from", async () => {
    const { element, grid, shown } = await opened();
    expect(textOf(element.querySelector('gd-grid-inspector'))).toBe(
      "Choose a cell to see what it holds, and where its column's values come from.",
    );
    grid().api.setFocusedCell(1, 'c1');
    await shown();
    const inspector = element.querySelector('gd-grid-inspector')!;
    expect(
      ['h2', '.type', '.value', '.aside', 'section:last-child p', '.sources'].map((selector) =>
        textOf(inspector.querySelector(selector)),
      ),
    ).toEqual([
      'status',
      'string?',
      'open',
      '4 characters',
      'Read from a column',
      'shop.orders.status',
    ]);
    grid().api.setFocusedCell(0, 'c1');
    await shown();
    expect(textOf(element.querySelector('gd-grid-inspector .value'))).toBe('NULL');
  });

  it('hides the inspector, and keeps it so', async () => {
    const { element, shown } = await opened();
    const toggle = element.querySelector<HTMLButtonElement>('[aria-controls=gd-inspector]')!;
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    toggle.click();
    await shown();
    expect(element.querySelector('#gd-inspector')?.hasAttribute('hidden')).toBe(true);
    expect(toggle.getAttribute('aria-pressed')).toBe('false');
    expect(localStorage.getItem(BrowseGrid.inspectorKey)).toBe('hidden');
  });

  it('asks again for the page that failed, and keeps the address at it', async () => {
    const { element, host, http, answer, shown } = await opened(
      crumbOf('shop.orders', { page: 1 }),
    );
    element.querySelector<HTMLElement>('.ag-paging-button[aria-label="Next Page"]')!.click();
    await shown();
    (await pageRequest(http)).flush(problemBody('source-unavailable', "Couldn't reach shop"), {
      status: 502,
      statusText: 'Bad Gateway',
    });
    await shown();
    expect(host.crumb().page).toBe(2);
    clickButton(element, 'Try again');
    await shown();
    const body = await answer(pageOf(orderRows(1, 6), { offset: 6, total: 7 }));
    expect(body).toMatchObject({ grid: { offset: 6 }, includeCount: true });
    expect(host.crumb().page).toBe(2);
    expect(gridCells(element)).toEqual([['1007', 'NULL', '6.50']]);
  });

  it("keeps the address's page when a grid's first page fails, and asks for it again", async () => {
    const { element, host, fixture, http, answer, shown } = await opened();
    host.crumb.set(crumbOf('shop.orders', { sort: [{ column: 'id', desc: true }], page: 2 }));
    fixture.detectChanges();
    await shown();
    const failed = await pageRequest(http);
    expect(failed.request.body).toMatchObject({ grid: { offset: 6 } });
    failed.flush(problemBody('source-unavailable', "Couldn't reach shop"), {
      status: 502,
      statusText: 'Bad Gateway',
    });
    await shown();
    expect(host.says).toEqual([]);
    clickButton(element, 'Try again');
    await shown();
    const body = await answer(pageOf(orderRows(1, 6), { offset: 6, total: 7 }));
    expect(body.grid).toMatchObject({ sort: [{ column: 'id', desc: true }], offset: 6 });
    expect(host.crumb().page).toBe(2);
    expect(host.says).toEqual([]);
  });

  it("goes to the first page when the address's page is past the rows' end and they weren't counted", async () => {
    const { host, answer } = await open(crumbOf('shop.orders', { page: 6 }));
    await answer(pageOf([], { offset: 18, total: null, hasMore: false }, orderColumns()));
    const first = await answer(pageOf(orderRows(3), { total: 7, hasMore: true }));
    expect(first).toMatchObject({ grid: { offset: 0 }, includeCount: true });
    expect(host.crumb().page).toBe(0);
  });

  it('pages another query from its own count, not the page the grid was made at', async () => {
    const { element, grid, answer } = await opened(crumbOf('shop.orders', { page: 4 }));
    element.querySelector<HTMLElement>('.ag-header-cell[col-id=c1] .ag-header-cell-label')!.click();
    await answer(pageOf(orderRows(3), { total: null, hasMore: true }));
    expect(textOf(element.querySelector('.count'))).toBe('At least 4 rows');
    expect(grid().api.paginationGetTotalPages()).toBe(2);
  });

  it('asks again for the first page when the address comes back to it', async () => {
    const { host, fixture, answer, shown } = await opened();
    host.crumb.set(crumbOf('shop.orders', { where: 'total > 1' }));
    fixture.detectChanges();
    await shown();
    await answer(pageOf(orderRows(1), { total: 1 }));
    host.crumb.set(crumbOf('shop.orders'));
    fixture.detectChanges();
    await shown();
    expect((await answer(pageOf(orderRows(3), { total: 7, hasMore: true }))).grid).toMatchObject({
      where: null,
      offset: 0,
    });
  });

  it('puts the keyboard on the row the address chooses, once, when asked to: as it is found, or once asked', async () => {
    const it = await opened(crumbOf('shop.orders', { row: ['1002'] }));
    const row = () => it.element.querySelector('.ag-row[row-index="1"]')!;
    expect(row().classList).toContain('ag-row-selected');
    expect(it.element.contains(document.activeElement)).toBe(false);
    // Found before: the keyboard goes there once asked.
    it.host.focusChosen.set(true);
    await it.shown();
    expect(row().contains(document.activeElement)).toBe(true);

    // Once: another row chosen by the address takes it no more.
    (document.activeElement as HTMLElement).blur();
    it.host.crumb.set({ ...it.host.crumb(), row: ['1003'] });
    await it.shown();
    expect(it.element.querySelector('.ag-row[row-index="2"]')!.classList).toContain(
      'ag-row-selected',
    );
    expect(it.element.contains(document.activeElement)).toBe(false);
    // Nor do rows loaded later (another page, with the row the address chooses).
    it.host.crumb.set({ ...it.host.crumb(), page: 1, row: ['1005'] });
    await it.shown();
    await it.answer(pageOf(orderRows(3, 3), { offset: 3, total: 7, hasMore: true }));
    expect(it.element.querySelector('.ag-row[row-index="4"]')!.classList).toContain(
      'ag-row-selected',
    );
    expect(it.element.contains(document.activeElement)).toBe(false);
  });

  it('puts the keyboard on the row once its rows come, when asked as the grid was made', async () => {
    const it = await open(crumbOf('shop.orders', { row: ['1002'] }));
    it.host.focusChosen.set(true);
    await it.shown();
    expect(it.element.contains(document.activeElement)).toBe(false);
    await it.answer(pageOf(orderRows(3), { total: 3 }, orderColumns()));
    expect(
      it.element.querySelector('.ag-row[row-index="1"]')!.contains(document.activeElement),
    ).toBe(true);
  });

  it("chooses no row when the address's row isn't among those loaded", async () => {
    const { element, host, fixture, shown } = await opened();
    element.querySelector<HTMLElement>('.ag-row[row-index="1"] [col-id=c1]')!.click();
    await shown();
    host.crumb.set({ ...host.crumb(), row: ['9999'] });
    fixture.detectChanges();
    await shown();
    expect(element.querySelector('.ag-row-selected')).toBeNull();
  });

  it('goes where the address went as the grid was made', async () => {
    const { element, host, fixture, http, answer, shown } = await open();
    const prime = await pageRequest(http);
    prime.flush(pageOf(orderRows(3), { total: 7, hasMore: true }, orderColumns()));
    // The schema is read (in a few microtasks); the grid is made, and before it says it is ready (a microtask
    // later), the address goes elsewhere.
    for (let turn = 0; turn < 5; turn++) {
      await Promise.resolve();
    }
    fixture.detectChanges();
    expect(element.querySelector('ag-grid-angular')).not.toBeNull();
    host.crumb.set(crumbOf('shop.orders', { sort: [{ column: 'total', desc: true }] }));
    fixture.detectChanges();
    await shown();
    const body = await answer(pageOf(orderRows(3), { total: 7, hasMore: true }));
    expect(body.grid).toMatchObject({ sort: [{ column: 'total', desc: true }] });
    expect(host.crumb().sort).toEqual([{ column: 'total', desc: true }]);
    expect(host.says).toEqual([]);
  });

  it('leaves the keyboard in a grid made anew for the address', async () => {
    const { element, host, fixture, answer, shown } = await opened();
    element.querySelector<HTMLElement>('.ag-header-cell[col-id=c1]')!.focus();
    host.crumb.set(crumbOf('shop.orders', { where: 'total > 1' }));
    fixture.detectChanges();
    await shown();
    await answer(pageOf(orderRows(1), { total: 1 }));
    expect(document.activeElement?.closest('.ag-header-cell')?.getAttribute('col-id')).toBe('c0');
  });

  it('chooses no rows of an entity without a key', async () => {
    const { element, host, answer, shown } = await open();
    await answer(pageOf([rowOf(['a'], null), rowOf(['b'], null)], {}, [columnOf('name')], null));
    element.querySelector<HTMLElement>('.ag-row[row-index="1"] [col-id=c0]')!.click();
    await shown();
    expect(host.says).toEqual([]);
    expect(element.querySelector('.ag-row-selected')).toBeNull();
  });
});

describe("the grid's links", () => {
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

  /** Links that lead to `/<prefix>/<navigation>/<row>`. */
  function linksTo(prefix = 'to'): LinkTo {
    const router = TestBed.inject(Router);
    return (row, navigation) => router.parseUrl(`/${prefix}/${navigation}/${row.join('~')}`);
  }

  /** shop.orders' rows with their customers and lines (unless other rows are given), linked, shown. */
  async function linked(linkTo: LinkTo | null = linksTo(), page = linkedPageOf(linkedRows(3))) {
    const fixture = TestBed.createComponent(Host);
    const host = fixture.componentInstance;
    host.linkTo.set(linkTo);
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
    await answerPage(http, page);
    await shown();
    const grid = () =>
      fixture.debugElement.query(By.directive(AgGridAngular)).componentInstance as AgGridAngular;
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const hrefs = (colId: string) =>
      [...element.querySelectorAll(`.ag-row [col-id=${colId}] a`)].map((link) =>
        link.getAttribute('href'),
      );
    const navigated = () => navigate.mock.calls.map(([url]) => url.toString());
    /** A key pressed in the cell the keyboard is on; whether the event was let be. */
    const keyDown = (key: string, init: KeyboardEventInit = {}) =>
      element
        .querySelector('.ag-cell-focus')!
        .dispatchEvent(
          new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }),
        );
    const inspector = () => element.querySelector('gd-grid-inspector')!;
    return { fixture, host, element, shown, grid, navigated, hrefs, keyDown, inspector };
  }

  it("shows the rows a column's values refer to, linked, and a column for each collection of rows that refer to them", async () => {
    const { element, hrefs } = await linked();
    expect(gridHeaders(element)).toEqual(['id', 'customer_id', 'status', 'order_lines']);
    expect(gridCells(element)).toEqual([
      ['1001', 'Acme42', 'open', 'order_lines ›'],
      ['1002', '43', 'open', 'order_lines ›'],
      ['1003', 'NULL', 'open', 'order_lines ›'],
    ]);
    expect(hrefs('c1')).toEqual(['/to/customer/1001', '/to/customer/1002']);
    expect(hrefs('n0')).toEqual([
      '/to/order_lines/1001',
      '/to/order_lines/1002',
      '/to/order_lines/1003',
    ]);
    expect(element.querySelector('.ag-row [col-id=c1] a')?.getAttribute('tabindex')).toBe('-1');
  });

  it('follows a link clicked, without choosing its row', async () => {
    const { element, host, shown, navigated } = await linked();
    element.querySelector<HTMLElement>('.ag-row[row-index="1"] [col-id=n0] a')!.click();
    element.querySelector<HTMLElement>('.ag-row[row-index="0"] [col-id=c1] a')!.click();
    await shown();
    expect(navigated()).toEqual(['/to/order_lines/1002', '/to/customer/1001']);
    expect(host.says).toEqual([]);
    expect(element.querySelector('.ag-row-selected')).toBeNull();
  });

  it('follows the link of the cell the keyboard is on with Enter', async () => {
    const { grid, shown, navigated, keyDown } = await linked();
    grid().api.setFocusedCell(1, 'n0');
    await shown();
    expect(keyDown('Enter')).toBe(false);
    expect(keyDown('Enter', { ctrlKey: true })).toBe(true);
    grid().api.setFocusedCell(2, 'c1');
    await shown();
    keyDown('Enter');
    grid().api.setFocusedCell(0, 'c2');
    await shown();
    keyDown('Enter');
    expect(navigated()).toEqual(['/to/order_lines/1002']);
  });

  it('says where links lead as the address changes, in place: the keyboard on a link stays', async () => {
    const { element, fixture, host, shown, hrefs } = await linked();
    const link = element.querySelector<HTMLElement>('.ag-row[row-index="0"] [col-id=c1] a')!;
    link.focus();
    host.linkTo.set(linksTo('elsewhere'));
    fixture.detectChanges();
    await shown();
    expect(hrefs('c1')).toEqual(['/elsewhere/customer/1001', '/elsewhere/customer/1002']);
    expect(hrefs('n0')[0]).toBe('/elsewhere/order_lines/1001');
    expect(element.querySelector('.ag-row[row-index="0"] [col-id=c1] a')).toBe(link);
    expect(document.activeElement).toBe(link);
  });

  it("leads under the application's base", async () => {
    TestBed.configureTestingModule({ providers: [{ provide: APP_BASE_HREF, useValue: '/app/' }] });
    const { hrefs } = await linked();
    expect(hrefs('c1')).toEqual(['/app/to/customer/1001', '/app/to/customer/1002']);
  });

  it('shows no collections of rows without a key, whose references lead nowhere', async () => {
    const keyless = linkedRows(2).map((row) => ({ ...row, id: null, k: null }));
    const page = linkedPageOf(keyless);
    const { element, grid, shown, inspector } = await linked(linksTo(), {
      ...page,
      schema: page.schema && { ...page.schema, key: null },
    });
    expect(gridHeaders(element)).toEqual(['id', 'customer_id', 'status']);
    expect(gridCells(element)[0]).toEqual(['1001', 'Acme42', 'open']);
    expect(element.querySelector('.ag-row a')).toBeNull();
    grid().api.setFocusedCell(0, 'c1');
    await shown();
    expect(
      textOf(inspector().querySelector('[aria-labelledby=gd-inspector-reference] .aside')),
    ).toBe('Rows without a key lead nowhere.');
  });

  it('shows what rows refer to without links, and no collections, when nothing says where they lead', async () => {
    const { element, grid, shown, navigated, keyDown, inspector } = await linked(null);
    expect(gridHeaders(element)).toEqual(['id', 'customer_id', 'status']);
    expect(gridCells(element)[0]).toEqual(['1001', 'Acme42', 'open']);
    expect(element.querySelector('.ag-row a')).toBeNull();
    grid().api.setFocusedCell(0, 'c1');
    await shown();
    expect(keyDown('Enter')).toBe(true);
    expect(navigated()).toEqual([]);
    expect(textOf(inspector().querySelector('[aria-labelledby=gd-inspector-reference]'))).toBe(
      'Refers to A row of shop.customers, through customer. Acme',
    );
  });

  it('says what the row a cell refers to is, and what rows a collection leads to', async () => {
    const { grid, shown, inspector } = await linked();
    grid().api.setFocusedCell(0, 'c1');
    await shown();
    const reference = () => inspector().querySelector('[aria-labelledby=gd-inspector-reference]');
    expect([...reference()!.children].map((child) => textOf(child))).toEqual([
      'Refers to',
      'A row of shop.customers, through customer.',
      'Acme',
      'Enter, or a click on the link, shows the row.',
    ]);
    expect(textOf(inspector().querySelector('.value'))).toBe('42');
    grid().api.setFocusedCell(2, 'c1');
    await shown();
    expect(textOf(reference())).toBe(
      'Refers to A row of shop.customers, through customer. Its value is NULL: it refers to no row.',
    );
    grid().api.setFocusedCell(1, 'n0');
    await shown();
    expect(
      [...inspector().querySelectorAll('h2, .type, h3, p')].map((part) => textOf(part)),
    ).toEqual([
      'order_lines',
      'Rows of shop.order_lines',
      'Leads to',
      'The rows of shop.order_lines that refer to this row.',
      'Enter, or a click on the link, shows them.',
    ]);
  });
});

describe('the words of the grid', () => {
  it("reads a row's key from its id", () => {
    expect(keyOf('["1",2,true]')).toEqual(['1', '2', 'true']);
    expect(keyOf('not json')).toEqual(['not json']);
    expect(keyOf('{"a":1}')).toEqual(['{"a":1}']);
  });

  it("finds the condition's problems in a problem", () => {
    expect(
      whereProblemsOf({
        status: 400,
        code: 'invalid-request',
        title: 'x',
        errors: { 'grid.where': ['Bad'] },
      }),
    ).toEqual([{ message: 'Bad', start: null, end: null }]);
    expect(
      whereProblemsOf({
        status: 422,
        code: 'query-invalid',
        title: 'x',
        body: {
          field: 'grid.where',
          diagnostics: [{ message: 'No', start: 2, end: 4 }, { message: 'Odd' }],
        },
      }),
    ).toEqual([
      { message: 'No', start: 2, end: 4 },
      { message: 'Odd', start: null, end: null },
    ]);
    expect(
      whereProblemsOf({
        status: 422,
        code: 'query-invalid',
        title: 'x',
        body: { diagnostics: [] },
      }),
    ).toEqual([]);
  });
});
