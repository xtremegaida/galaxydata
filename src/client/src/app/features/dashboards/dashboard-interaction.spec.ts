import { Location } from '@angular/common';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import type { TestRequest } from '@angular/common/http/testing';
import { dashboardOf, rowsOf, salesDefinition, widgetDataUrl } from '../../../testing/dashboards';
import { FakeECharts, fakeEChartsProviders } from '../../../testing/echarts';
import { requestTo, settle } from '../../../testing/http';
import { FakeMonaco, fakeMonacoProviders } from '../../../testing/monaco';
import { openPage, pageProviders, textOf, wordsOf } from '../../../testing/pages';
import { explainOf, queryPageOf, orderResultRows } from '../../../testing/query';
import { FakeResizeObserver } from '../../../testing/resize';
import { dashboardRoutes } from './dashboards.routes';
import type { Filter } from './model/definition';
import { DASHBOARD_WAITS } from './state/waits';

@Component({ selector: 'gd-blank', template: '' })
class Blank {}

function filterOf(id: string, source: string, extra: Partial<Filter> = {}): Filter {
  return {
    id,
    label: id[0].toUpperCase() + id.slice(1),
    field: { source, path: [], column: id },
    kind: 'values',
    value: null,
    visible: true,
    editable: true,
    multiple: true,
    except: [],
    ...extra,
  };
}

/** The sales dashboard with filters: Status to change, City fixed, Tenant hidden. */
const filtered = salesDefinition({
  filters: [
    filterOf('status', 'orders'),
    filterOf('city', 'customers', { editable: false, value: { op: 'in', values: ['Cape Town'] } }),
    filterOf('tenant', 'customers', {
      visible: false,
      editable: false,
      value: { op: 'in', values: ['t1'] },
    }),
  ],
});

const text = { kind: 'string', nullable: false, text: 'string' } as const;

/** A table's body, cell by cell. */
function cellsOf(table: Element): string[][] {
  return [...table.querySelectorAll('tbody tr')].map((row) =>
    [...row.querySelectorAll('td')].map((cell) => textOf(cell)),
  );
}

describe('choosing and filtering on a dashboard', () => {
  let charts: FakeECharts;
  let monaco: FakeMonaco;

  beforeEach(() => {
    FakeResizeObserver.install();
    charts = new FakeECharts();
    monaco = new FakeMonaco();
    TestBed.configureTestingModule({
      providers: [
        pageProviders([
          { path: 'dashboards', children: dashboardRoutes },
          { path: 'query', component: Blank },
        ]),
        fakeEChartsProviders(charts),
        fakeMonacoProviders(monaco),
        { provide: DASHBOARD_WAITS, useValue: { preview: 1, filterTyping: 1 } },
      ],
    });
  });

  afterEach(() => {
    FakeResizeObserver.uninstall();
    TestBed.resetTestingModule();
  });

  /** Opens the dashboard at `url`, answering it and its widgets' first rows; gives those requests too. */
  async function open(url = '/dashboards/1', definition = filtered) {
    const page = await openPage(url);
    (await requestTo(page.http, '/api/dashboards/1')).flush(dashboardOf(definition));
    const status = await requestTo(page.http, widgetDataUrl(1, 'by-status'), 'POST');
    const city = await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST');
    const first = { status: status.request.body, city: city.request.body };
    status.flush(
      rowsOf('Status', [
        ['open', '2'],
        ['shipped', '1'],
      ]),
    );
    city.flush(rowsOf('City', [['Cape Town', '1']]));
    for (
      let turn = 0;
      turn < 50 &&
      !(charts.charts.length === 2 && charts.charts.every((c) => c.options.length > 0));
      turn++
    ) {
      await settle(1);
      page.harness.detectChanges();
    }
    await page.harness.fixture.whenStable();
    const bars = charts.chartIn(page.page.querySelector('[data-gd-cell="0,1,6,6"]')!)!;
    return { ...page, first, bars, router: TestBed.inject(Router) };
  }

  /** The next request for a widget's rows, its state's selections and filters. */
  async function next(http: Parameters<typeof requestTo>[0], widget: string) {
    const request = await requestTo(http, widgetDataUrl(1, widget), 'POST');
    return request;
  }

  it('keeps what is chosen in the address, and reads it from there', async () => {
    const { page, http, first, router, harness } = await open('/dashboards/1?s.by-status=open');
    expect(first.status.state.selections).toEqual({});
    expect(first.city.state.selections).toEqual({
      'by-status': { mode: 'include', keys: [['open']] },
    });
    const chips = page.querySelector('gd-selection-chips')!;
    expect(wordsOf(chips)).toBe('Status: open');
    chips.querySelector<HTMLButtonElement>('[aria-label="Clear Status: open"]')!.click();
    harness.detectChanges();
    const city = await next(http, 'by-city');
    expect(city.request.body.state.selections).toEqual({});
    city.flush(rowsOf('City', []));
    await settle();
    expect(router.url).toBe('/dashboards/1');
    expect(page.querySelector('gd-selection-chips')).toBeNull();
  });

  it('writes what is chosen into the address, letting go of what was asked before', async () => {
    const { http, bars, router } = await open();
    bars.emit('click', { key: ['open'] });
    await settle();
    const before = await next(http, 'by-city');
    bars.emit('click', { key: ['shipped'] }, { ctrlKey: true });
    await settle();
    expect(before.cancelled).toBe(true);
    const city = await next(http, 'by-city');
    expect(city.request.body.state.selections['by-status']).toEqual({
      mode: 'include',
      keys: [['open'], ['shipped']],
    });
    http.expectNone(widgetDataUrl(1, 'by-status'));
    city.flush(rowsOf('City', []));
    await settle();
    expect(router.url).toBe('/dashboards/1?s.by-status=open,shipped');
    // Alt on a slice chosen takes it out.
    bars.emit('click', { key: ['open'] }, { altKey: true });
    await settle();
    (await next(http, 'by-city')).flush(rowsOf('City', []));
    await settle();
    expect(router.url).toBe('/dashboards/1?s.by-status=shipped');
  });

  it('follows the address as it changes: links, back and forward', async () => {
    const { page, http, router, harness } = await open();
    // As the application's start does: the browser's back and forward are the router's to follow.
    router.setUpLocationChangeListener();
    await router.navigateByUrl('/dashboards/1?s.by-status=!shipped');
    harness.detectChanges();
    let city: TestRequest = await next(http, 'by-city');
    expect(city.request.body.state.selections).toEqual({
      'by-status': { mode: 'exclude', keys: [['shipped']] },
    });
    city.flush(rowsOf('City', []));
    await settle();
    harness.detectChanges();
    expect(wordsOf(page.querySelector('gd-selection-chips'))).toBe('Status: not shipped');
    TestBed.inject(Location).back();
    await settle(5);
    harness.detectChanges();
    city = await next(http, 'by-city');
    expect(city.request.body.state.selections).toEqual({});
    city.flush(rowsOf('City', []));
    await settle();
    expect(router.url).toBe('/dashboards/1');
  });

  it('says what of the address it left out, and leaves it out', async () => {
    const { page, router, harness } = await open('/dashboards/1?s.ghost=a&f.city=Durban&copy=x');
    await settle();
    harness.detectChanges();
    expect(textOf(page.querySelector('[role=status] gd-message'))).toContain(
      "Some of the address was left out. There's no widget ghost that chooses slices. The filter City can't be changed.",
    );
    expect(router.url).toBe('/dashboards/1?copy=x');
    [...page.querySelectorAll<HTMLButtonElement>('[role=status] button')]
      .find((b) => textOf(b) === 'Dismiss')!
      .click();
    harness.detectChanges();
    expect(page.querySelector('[role=status] gd-message')).toBeNull();
  });

  it('shows the filters viewers see: those they may change, and those fixed', async () => {
    const { page } = await open();
    const bar = page.querySelector('gd-filter-bar')!;
    expect(textOf(bar.querySelector('gd-values-filter mat-label'))).toBe('Status');
    expect([...bar.querySelectorAll('.locked')].map((l) => wordsOf(l))).toEqual([
      'City: Cape Town',
    ]);
    expect(textOf(bar)).not.toContain('Tenant');
  });

  it("asks for a filter's values as typing pauses (under the other filters only), and filters by those chosen", async () => {
    // A slice chosen in a widget doesn't narrow a filter's values: a filter of the field chosen from would offer only it.
    const { page, http, router, harness } = await open('/dashboards/1?s.by-city=Cape Town');
    const input = page.querySelector<HTMLInputElement>('gd-values-filter input')!;
    input.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    input.dispatchEvent(new FocusEvent('focus'));
    harness.detectChanges();
    const values = await requestTo(http, '/api/dashboards/1/filters/status/values', 'POST');
    expect(values.request.body).toEqual({
      hash: 'p'.repeat(64),
      state: { filters: {}, selections: {} },
      search: null,
    });
    values.flush({
      type: text,
      values: [
        { value: 'open', rows: 2 },
        { value: 'shipped', rows: 1 },
      ],
      more: false,
    });
    // Two keys typed in a row: one request, for both.
    for (const typed of ['s', 'sh']) {
      input.value = typed;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      harness.detectChanges();
    }
    await settle(5);
    const searched = http.match((r) => r.url === '/api/dashboards/1/filters/status/values');
    expect(searched.map((r) => r.request.body.search)).toEqual(['sh']);
    searched[0].flush({ type: text, values: [{ value: 'shipped', rows: 1 }], more: false });
    await settle();
    harness.detectChanges();
    const options = [...document.querySelectorAll<HTMLElement>('mat-option')];
    expect(options.map((o) => wordsOf(o))).toEqual(['shipped 1 row']);
    options[0].click();
    harness.detectChanges();
    const status = await next(http, 'by-status');
    const city = await next(http, 'by-city');
    for (const request of [status, city]) {
      expect(request.request.body.state.filters).toEqual({
        status: { op: 'in', values: ['shipped'] },
      });
    }
    status.flush(rowsOf('Status', [['shipped', '1']]));
    city.flush(rowsOf('City', []));
    await settle();
    harness.detectChanges();
    expect(decodeURIComponent(router.url)).toBe(
      '/dashboards/1?f.status=shipped&s.by-city=Cape Town',
    );
    expect(wordsOf(page.querySelector('gd-values-filter mat-chip-row'))).toBe('shipped');
    // Without values, the filter is the dashboard's again: none.
    page.querySelector<HTMLButtonElement>('[aria-label="Remove shipped"]')!.click();
    harness.detectChanges();
    const again = await next(http, 'by-status');
    expect(again.request.body.state.filters).toEqual({});
    again.flush(rowsOf('Status', []));
    (await next(http, 'by-city')).flush(rowsOf('City', []));
    await settle();
    expect(decodeURIComponent(router.url)).toBe('/dashboards/1?s.by-city=Cape Town');
  });

  it("chooses from a menu at the slice, too (for touch, and where Alt+click is the system's)", async () => {
    const { http, bars, harness } = await open();
    bars.emit('contextmenu', { key: ['open'] });
    await new Promise((resolve) => requestAnimationFrame(resolve));
    harness.detectChanges();
    const items = [...document.querySelectorAll<HTMLButtonElement>('.mat-mdc-menu-panel button')];
    expect(items.map((item) => wordsOf(item))).toEqual([
      'Choose only this',
      'Add to those chosen',
      'Leave out',
    ]);
    items[2].click();
    harness.detectChanges();
    const city = await next(http, 'by-city');
    expect(city.request.body.state.selections).toEqual({
      'by-status': { mode: 'exclude', keys: [['open']] },
    });
    city.flush(rowsOf('City', []));
  });

  it('reads rows fresh when refreshed, and not what is chosen after', async () => {
    const { page, http, bars, harness } = await open();
    [...page.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => textOf(b).endsWith('Refresh'))!
      .click();
    harness.detectChanges();
    const status = await next(http, 'by-status');
    const city = await next(http, 'by-city');
    expect([status.request.body.refresh, city.request.body.refresh]).toEqual([true, true]);
    status.flush(rowsOf('Status', [['open', '2']]));
    city.flush(rowsOf('City', []));
    await settle();
    bars.emit('click', { key: ['open'] });
    await settle();
    const chosen = await next(http, 'by-city');
    expect(chosen.request.body.refresh).toBe(false);
    chosen.flush(rowsOf('City', []));
  });

  /** Opens a widget's menu and chooses an item of it. */
  async function fromMenu(
    page: HTMLElement,
    harness: { detectChanges(): void },
    widget: string,
    item: string,
  ) {
    page.querySelector<HTMLButtonElement>(`[aria-label="Actions of ${widget}"]`)!.click();
    harness.detectChanges();
    await settle();
    [...document.querySelectorAll<HTMLButtonElement>('.mat-mdc-menu-panel button')]
      .find((b) => wordsOf(b) === item)!
      .click();
    harness.detectChanges();
    for (let turn = 0; turn < 20 && !document.querySelector('mat-dialog-container'); turn++) {
      await settle(1);
      harness.detectChanges();
    }
    return document.querySelector('mat-dialog-container')!;
  }

  it("shows a widget's rows, and those they were worked out from", async () => {
    const { page, http, harness } = await open('/dashboards/1?s.by-city=' + "'Cape Town'");
    const dialog = await fromMenu(page, harness, 'Orders by status', 'Data');
    expect(textOf(dialog.querySelector('h2'))).toBe('Data of Orders by status');
    expect(cellsOf(dialog.querySelector('gd-rows-table')!)).toEqual([
      ['open', '2'],
      ['shipped', '1'],
    ]);
    http.expectNone(widgetDataUrl(1, 'by-status'));
    [...dialog.querySelectorAll<HTMLElement>('[role=tab]')]
      .find((t) => textOf(t) === 'Underlying rows')!
      .click();
    harness.detectChanges();
    const query = await requestTo(http, '/api/dashboards/1/widgets/by-status/query', 'POST');
    expect(query.request.body.state.selections).toEqual({
      'by-city': { mode: 'include', keys: [['Cape Town']] },
    });
    const underlying = {
      text: 'shop.orders.where(r0 => r0.customer.city in [$s1])',
      parameters: [{ name: 's1', type: 'string', value: 'Cape Town' }],
    };
    query.flush({ main: underlying, categories: null, total: null, underlying });
    const rows = await requestTo(http, '/api/query/execute', 'POST');
    expect([rows.request.body.text, rows.request.body.parameters]).toEqual([
      underlying.text,
      underlying.parameters,
    ]);
    rows.flush(queryPageOf(orderResultRows(2)));
    await settle();
    harness.detectChanges();
    expect(dialog.querySelector('gd-query-results')).not.toBeNull();
    expect(dialog.querySelector('[aria-label=Inspector]')).toBeNull();
  });

  it("shows a widget's query and its SQL, and opens it in the query editor", async () => {
    const { page, http, harness, router } = await open();
    const dialog = await fromMenu(page, harness, 'Customers by city', 'View query');
    const main = {
      text: 'shop.customers.where(r0 => r0.city in [$f1]).groupBy(d0: city)',
      parameters: [{ name: 'f1', type: 'string', value: 'Cape Town' }],
    };
    (await requestTo(http, '/api/dashboards/1/widgets/by-city/query', 'POST')).flush({
      main,
      categories: null,
      total: { text: 'shop.customers.groupBy().select(m0: count())', parameters: [] },
      underlying: main,
    });
    for (let turn = 0; turn < 20 && monaco.editors.length === 0; turn++) {
      await settle(1);
      harness.detectChanges();
    }
    expect(monaco.editors.at(-1)!.model.getValue()).toBe(main.text);
    expect(cellsOf(dialog.querySelector('.parameters')!)).toEqual([['$f1', 'string', 'Cape Town']]);
    expect([...dialog.querySelectorAll('mat-button-toggle')].map((t) => textOf(t))).toEqual([
      'Its rows',
      'Its total',
    ]);
    [...dialog.querySelectorAll<HTMLElement>('[role=tab]')]
      .find((t) => textOf(t) === 'SQL')!
      .click();
    harness.detectChanges();
    const explain = await requestTo(http, '/api/query/explain', 'POST');
    expect(explain.request.body).toEqual({
      text: main.text,
      parameters: main.parameters,
      verbose: false,
    });
    explain.flush(explainOf());
    await settle();
    harness.detectChanges();
    expect(dialog.querySelector('gd-query-sql')).not.toBeNull();
    [...dialog.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => textOf(b) === 'Open in the query editor')!
      .click();
    await settle(5);
    expect(router.url.startsWith('/query#')).toBe(true);
    expect(decodeURIComponent(router.url)).toContain(main.text);
  });
});
