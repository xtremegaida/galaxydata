import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import {
  dashboardOf,
  rowsOf,
  salesDefinition,
  tabbedDefinition,
  widgetDataUrl,
} from '../../../testing/dashboards';
import { FakeECharts, fakeEChartsProviders } from '../../../testing/echarts';
import { eventually, requestTo, settle } from '../../../testing/http';
import { openPage, pageProviders, textOf } from '../../../testing/pages';
import { FakeResizeObserver } from '../../../testing/resize';
import { dashboardRoutes } from './dashboards.routes';

describe('a dashboard', () => {
  let charts: FakeECharts;

  beforeEach(() => {
    FakeResizeObserver.install();
    charts = new FakeECharts();
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'dashboards', children: dashboardRoutes }]),
        fakeEChartsProviders(charts),
      ],
    });
  });

  afterEach(() => {
    FakeResizeObserver.uninstall();
    TestBed.resetTestingModule();
  });

  /** Opens the dashboard, answering it and its widgets' rows. */
  async function open() {
    const page = await openPage('/dashboards/1');
    (await requestTo(page.http, '/api/dashboards/1')).flush(dashboardOf());
    const status = await requestTo(page.http, widgetDataUrl(1, 'by-status'), 'POST');
    const city = await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST');
    expect(status.request.body).toEqual({
      hash: 'p'.repeat(64),
      state: { filters: {}, selections: {} },
      page: null,
      refresh: false,
    });
    status.flush(
      rowsOf('Status', [
        ['open', '2'],
        ['shipped', '1'],
      ]),
    );
    city.flush(
      rowsOf('City', [
        ['Cape Town', '1'],
        [null, '1'],
      ]),
    );
    // The widgets' views and ECharts load as the first chart is shown.
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
    return page;
  }

  it('shows its widgets, each with its rows, and text as it is written', async () => {
    const { page, http } = await open();
    expect(page.querySelector('h1')?.textContent).toBe('Sales');
    expect(page.querySelector('gd-text-widget h2')?.textContent).toBe('Sales');
    expect([...page.querySelectorAll('.frame h2.title')].map((h) => textOf(h))).toEqual([
      'Orders by status',
      'Customers by city',
    ]);
    expect(charts.charts).toHaveLength(2);
    const bars = charts.chartIn(page.querySelector('[data-gd-cell="0,1,6,6"]')!)!;
    const series = bars.option.series as { data: { value: number; key: unknown[] }[] }[];
    expect(series[0].data.map((d) => [d.key, d.value])).toEqual([
      [['open'], 2],
      [['shipped'], 1],
    ]);
    http.expectNone((r) => r.url.includes('/widgets/title/'));
  });

  it('lays its widgets out for the width it has', async () => {
    const { page, harness } = await open();
    const grid = page.querySelector('gd-dashboard-grid')!;
    const cells = () =>
      [...grid.querySelectorAll('gd-widget-frame')].map((f) => f.getAttribute('data-gd-cell'));
    expect([grid.getAttribute('data-gd-breakpoint'), cells()]).toEqual([
      'wide',
      ['0,0,12,1', '0,1,6,6', '6,1,6,6'],
    ]);
    FakeResizeObserver.resize(grid, 800);
    harness.detectChanges();
    expect([grid.getAttribute('data-gd-breakpoint'), cells()]).toEqual([
      'medium',
      ['0,0,6,1', '0,1,3,6', '3,1,3,6'],
    ]);
    FakeResizeObserver.resize(grid, 400);
    harness.detectChanges();
    expect([grid.getAttribute('data-gd-breakpoint'), cells()]).toEqual([
      'narrow',
      ['0,0,1,1', '0,1,1,6', '0,7,1,6'],
    ]);
  });

  it('filters the widgets that listen by what is chosen in another, which only highlights it', async () => {
    const { page, http } = await open();
    const bars = charts.chartIn(page.querySelector('[data-gd-cell="0,1,6,6"]')!)!;
    bars.emit('click', { key: ['open'] });
    await settle();
    const city = await requestTo(http, widgetDataUrl(1, 'by-city'), 'POST');
    expect(city.request.body.state).toEqual({
      filters: {},
      selections: { 'by-status': { mode: 'include', keys: [['open']] } },
    });
    http.expectNone(widgetDataUrl(1, 'by-status'));
    city.flush(rowsOf('City', [['Cape Town', '1']]));
    await settle();
    const series = bars.option.series as { data: { itemStyle: { opacity?: number } }[] }[];
    expect(series[0].data.map((d) => d.itemStyle.opacity)).toEqual([undefined, 0.3]);
    // Ctrl adds to what is chosen; Alt on what is chosen takes it out again.
    bars.emit('click', { key: ['shipped'] }, { ctrlKey: true });
    await settle();
    (await requestTo(http, widgetDataUrl(1, 'by-city'), 'POST')).flush(rowsOf('City', []));
  });

  it('reads every widget again, fresh, when refreshed', async () => {
    const { page, http } = await open();
    [...page.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => textOf(b).endsWith('Refresh'))!
      .click();
    await settle();
    const status = await requestTo(http, widgetDataUrl(1, 'by-status'), 'POST');
    const city = await requestTo(http, widgetDataUrl(1, 'by-city'), 'POST');
    expect([status.request.body.refresh, city.request.body.refresh]).toEqual([true, true]);
    status.flush(rowsOf('Status', []));
    city.flush(rowsOf('City', []));
  });

  it('keeps the rows shown when reading them again fails, saying so', async () => {
    const { page, http } = await open();
    [...page.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => textOf(b).endsWith('Refresh'))!
      .click();
    await settle();
    (await requestTo(http, widgetDataUrl(1, 'by-status'), 'POST')).flush(
      { status: 504, code: 'query-timeout', title: 'The query took too long' },
      { status: 504, statusText: 'Timeout' },
    );
    (await requestTo(http, widgetDataUrl(1, 'by-city'), 'POST')).flush(rowsOf('City', []));
    await settle();
    const frame = page.querySelector('[data-gd-cell="0,1,6,6"]')!;
    expect(textOf(frame.querySelector('[role=alert]'))).toContain('The query took too long');
    const bars = charts.chartIn(frame)!;
    expect((bars.option.series as { data: unknown[] }[])[0].data).toHaveLength(2);
  });

  it("is drawn with its palettes, a chart's own giving way to the dashboard's when it isn't there", async () => {
    const { page, http, harness } = await openPage('/dashboards/1');
    const sales = salesDefinition();
    const definition = {
      ...sales,
      palette: 7,
      widgets: sales.widgets.map((w) =>
        w.config.kind === 'pie' ? { ...w, config: { ...w.config, palette: 8 } } : w,
      ),
    };
    const palette = (open: string) => ({
      id: 7,
      name: 'Statuses',
      owner: 'ada',
      hash: open,
      definition: {
        colors: [
          { light: '#111111', dark: null },
          { light: '#222222', dark: null },
        ],
        assign: 'order' as const,
        distinct: true,
        whenOut: 'repeat' as const,
        matching: {
          ignoreCase: true,
          ignoreWhitespace: false,
          ignoreBrackets: false,
          ignoreAccents: false,
        },
        overrides: [{ label: 'CAPE TOWN', color: { light: open, dark: null } }],
      },
    });
    (await requestTo(http, '/api/dashboards/1')).flush(
      dashboardOf(definition, { palettes: [palette('#abcdef')] }),
    );
    await settle();
    (await requestTo(http, widgetDataUrl(1, 'by-status'), 'POST')).flush(
      rowsOf('Status', [['open', '2']]),
    );
    const cityRows = rowsOf('City', [
      ['Cape Town', '1'],
      [null, '1'],
    ]);
    (await requestTo(http, widgetDataUrl(1, 'by-city'), 'POST')).flush(cityRows);
    type Series = { itemStyle: { color: string }; data: { itemStyle: { color?: string } }[] }[];
    const seriesIn = (cell: string) =>
      charts.chartIn(page.querySelector(`[data-gd-cell="${cell}"]`)!)!.option.series as Series;
    // The pie's slices, each its colour; the bars', their measure's.
    const slices = () => seriesIn('6,1,6,6')[0].data.map((d) => d.itemStyle.color);
    const bars = () => seriesIn('0,1,6,6').map((s) => s.itemStyle.color);
    const drawn = (colors: string[]) => JSON.stringify(slices()) === JSON.stringify(colors);
    await eventually(harness, () => drawn(['#abcdef', '#111111']) && bars().length === 1);
    expect(slices()).toEqual(['#abcdef', '#111111']);
    expect(bars()).toEqual(['#111111']);
    // Refreshed, it reads its palettes again: changed since, its charts follow.
    [...page.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => textOf(b).endsWith('Refresh'))!
      .click();
    await settle();
    (await requestTo(http, '/api/dashboards/1/palettes')).flush([palette('#fedcba')]);
    (await requestTo(http, widgetDataUrl(1, 'by-status'), 'POST')).flush(
      rowsOf('Status', [['open', '2']]),
    );
    (await requestTo(http, widgetDataUrl(1, 'by-city'), 'POST')).flush(cityRows);
    await eventually(harness, () => drawn(['#fedcba', '#111111']));
    expect(slices()).toEqual(['#fedcba', '#111111']);
  });

  it('is the working copy where there is nothing published', async () => {
    const { page, http } = await openPage('/dashboards/1');
    (await requestTo(http, '/api/dashboards/1')).flush(
      dashboardOf(salesDefinition(), {
        published: null,
        publishedHash: null,
        publishedNumber: null,
      }),
    );
    await settle();
    const inline = http.match((r) => r.url === '/api/dashboards/data');
    expect(inline.map((r) => r.request.body.widget)).toEqual(['by-status', 'by-city']);
    // Each widget's slice: itself (and what is chosen in those it listens to: nothing yet).
    expect(inline[0].request.body.slice.widgets.map((w: { id: string }) => w.id)).toEqual([
      'by-status',
    ]);
    inline.forEach((r) => r.flush(rowsOf('Status', [])));
    await settle();
    expect(textOf(page.querySelector('.badge'))).toBe('Your working copy');
  });
});

describe('a dashboard with tabs', () => {
  let charts: FakeECharts;

  beforeEach(() => {
    FakeResizeObserver.install();
    charts = new FakeECharts();
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'dashboards', children: dashboardRoutes }]),
        fakeEChartsProviders(charts),
      ],
    });
  });

  afterEach(() => {
    FakeResizeObserver.uninstall();
    TestBed.resetTestingModule();
  });

  const orders = rowsOf('Status', [
    ['open', '2'],
    ['shipped', '1'],
  ]);
  const cities = rowsOf('City', [['Cape Town', '1']]);

  /** Opens the dashboard at an address, answering it (its widgets' rows are the test's). */
  async function open(address = '/dashboards/1') {
    const page = await openPage(address);
    (await requestTo(page.http, '/api/dashboards/1')).flush(dashboardOf(tabbedDefinition()));
    await settle();
    page.harness.detectChanges();
    return page;
  }

  /** Shows a tab, as a viewer clicks it. */
  async function show(page: Awaited<ReturnType<typeof open>>, tab: string): Promise<void> {
    page.page.querySelector<HTMLElement>(`[data-gd-tab="${tab}"]`)!.click();
    await settle();
    page.harness.detectChanges();
  }

  /** The bar chart, drawn. */
  async function bars(page: Awaited<ReturnType<typeof open>>) {
    await eventually(page.harness, () => charts.charts.some((c) => c.options.length > 0));
    return charts.chartIn(page.page.querySelector('[data-gd-cell="0,1,6,6"]')!)!;
  }

  it("shows its first tab, asking only for its widgets' rows; another asks once it is shown", async () => {
    const page = await open();
    expect([...page.page.querySelectorAll('[data-gd-tab]')].map((t) => textOf(t))).toEqual([
      'Orders',
      'Customers',
    ]);
    expect(page.page.querySelector('[data-gd-tab="orders"]')?.getAttribute('aria-selected')).toBe(
      'true',
    );
    (await requestTo(page.http, widgetDataUrl(1, 'by-status'), 'POST')).flush(orders);
    await settle();
    page.http.expectNone(widgetDataUrl(1, 'by-city'));
    expect(page.page.querySelectorAll('gd-dashboard-grid')).toHaveLength(1);

    await show(page, 'customers');
    (await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST')).flush(cities);
    await settle();
    expect(TestBed.inject(Router).url).toBe('/dashboards/1?tab=customers');
    const grids = [...page.page.querySelectorAll('gd-dashboard-grid')];
    expect(grids.map((g) => [g.getAttribute('data-gd-grid'), g.hasAttribute('inert')])).toEqual([
      ['orders', true],
      ['customers', false],
    ]);
    // Back and forth, nothing changed: nothing is asked again.
    await show(page, 'orders');
    await show(page, 'customers');
    page.http.expectNone((r) => r.url.includes('/widgets/'));
    expect(TestBed.inject(Router).url).toBe('/dashboards/1?tab=customers');
    await show(page, 'orders');
    expect(TestBed.inject(Router).url).toBe('/dashboards/1');
  });

  it('asks nothing on a tab not shown as slices are chosen, and once when it is shown', async () => {
    const page = await open();
    (await requestTo(page.http, widgetDataUrl(1, 'by-status'), 'POST')).flush(orders);
    await show(page, 'customers');
    (await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST')).flush(cities);
    await show(page, 'orders');
    const chart = await bars(page);
    chart.emit('click', { key: ['open'] });
    await settle();
    chart.emit('click', { key: ['shipped'] }, { ctrlKey: true });
    await settle();
    page.http.expectNone((r) => r.url.includes('/widgets/'));
    await show(page, 'customers');
    const city = await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST');
    expect(city.request.body).toMatchObject({
      state: { selections: { 'by-status': { mode: 'include', keys: [['open'], ['shipped']] } } },
      refresh: false,
    });
    city.flush(cities);
    await settle();
    page.http.expectNone((r) => r.url.includes('/widgets/'));
  });

  it("reads a tab's widgets fresh when it is shown after a refresh, and only then", async () => {
    const page = await open();
    (await requestTo(page.http, widgetDataUrl(1, 'by-status'), 'POST')).flush(orders);
    await show(page, 'customers');
    (await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST')).flush(cities);
    await show(page, 'orders');
    [...page.page.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => textOf(b).endsWith('Refresh'))!
      .click();
    await settle();
    const status = await requestTo(page.http, widgetDataUrl(1, 'by-status'), 'POST');
    expect(status.request.body.refresh).toBe(true);
    status.flush(orders);
    await settle();
    page.http.expectNone(widgetDataUrl(1, 'by-city'));
    await show(page, 'customers');
    const city = await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST');
    expect(city.request.body.refresh).toBe(true);
    city.flush(cities);
  });

  it('stops reading the rows of a tab hidden while they are read, and reads them when it is shown', async () => {
    const page = await open();
    (await requestTo(page.http, widgetDataUrl(1, 'by-status'), 'POST')).flush(orders);
    await show(page, 'customers');
    const first = await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST');
    await show(page, 'orders');
    expect(first.cancelled).toBe(true);
    await show(page, 'customers');
    const again = await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST');
    expect(again.request.body.refresh).toBe(false);
    again.flush(cities);
  });

  it('opens the tab its address names, asking for nothing of the others', async () => {
    const page = await open('/dashboards/1?tab=customers');
    (await requestTo(page.http, widgetDataUrl(1, 'by-city'), 'POST')).flush(cities);
    await settle();
    page.http.expectNone(widgetDataUrl(1, 'by-status'));
    expect(
      page.page.querySelector('[data-gd-tab="customers"]')?.getAttribute('aria-selected'),
    ).toBe('true');
    expect(page.page.querySelectorAll('gd-dashboard-grid')).toHaveLength(1);
  });
});
