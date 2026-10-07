import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { dashboardOf, rowsOf, salesDefinition } from '../../../../testing/dashboards';
import { FakeECharts, fakeEChartsProviders } from '../../../../testing/echarts';
import { requestTo, settle } from '../../../../testing/http';
import { openPage, pageProviders, textOf, wordsOf } from '../../../../testing/pages';
import { FakeResizeObserver } from '../../../../testing/resize';
import { dashboardRoutes } from '../dashboards.routes';
import type { Definition } from '../model/definition';
import { DASHBOARD_WAITS } from '../state/waits';
import { EditorStore } from './editor-store';

describe('the dashboard editor', () => {
  let charts: FakeECharts;
  let said: string[];

  beforeEach(() => {
    FakeResizeObserver.install();
    charts = new FakeECharts();
    said = [];
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'dashboards', children: dashboardRoutes }]),
        fakeEChartsProviders(charts),
        { provide: DASHBOARD_WAITS, useValue: { preview: 1, filterTyping: 1 } },
        {
          provide: LiveAnnouncer,
          useValue: { announce: (message: string) => (said.push(message), Promise.resolve()) },
        },
      ],
    });
  });

  afterEach(() => {
    FakeResizeObserver.uninstall();
    TestBed.resetTestingModule();
  });

  /** The previews asked for now, by widget, each answered. */
  async function previews(
    http: Parameters<typeof requestTo>[0],
  ): Promise<Record<string, unknown>[]> {
    await settle(5);
    const asked = http.match((r) => r.url === '/api/dashboards/data');
    for (const request of asked) {
      request.flush(rowsOf('Status', [['open', '2']]));
    }
    return asked.map((r) => r.request.body as Record<string, unknown>);
  }

  /** Opens a dashboard in the editor, its previews answered. */
  async function open(definition: Definition = salesDefinition()) {
    const page = await openPage('/dashboards/1/edit');
    (await requestTo(page.http, '/api/dashboards/1')).flush(dashboardOf(definition));
    const first = await previews(page.http);
    await settle();
    page.harness.detectChanges();
    const store = page.harness.routeDebugElement!.injector.get(EditorStore);
    return { ...page, first, store, router: TestBed.inject(Router) };
  }

  function press(
    key: string,
    extra: KeyboardEventInit = {},
    target: EventTarget = document.body,
  ): void {
    target.dispatchEvent(
      new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...extra }),
    );
  }

  function button(container: ParentNode, text: string): HTMLButtonElement {
    const found = [...container.querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => wordsOf(b) === text,
    );
    if (!found) {
      throw new Error(`No button "${text}"`);
    }
    return found;
  }

  it("previews each widget with its slice, and opens the working copy's settings", async () => {
    const { page, first } = await open();
    expect(first.map((body) => body['widget'])).toEqual(['by-status', 'by-city']);
    expect((first[0]['slice'] as Definition).widgets.map((w) => w.id)).toEqual(['by-status']);
    expect((page.querySelector('input[required]') as HTMLInputElement).value).toBe('Sales');
    expect(textOf(page.querySelector('gd-widget-panel'))).toContain(
      'Choose a widget on the canvas',
    );
    // Clicking a widget chooses it (its slices aren't chosen on the canvas).
    page.querySelector<HTMLElement>('[data-gd-cell="0,1,6,6"] .cover')!.click();
    await settle();
    expect(textOf(page.querySelector('gd-widget-panel .kind'))).toBe('Bar chart · by-status');
  });

  it('asks for a preview once edits of a widget pause, of that widget alone', async () => {
    const { http, store, harness } = await open();
    // Its title isn't in its slice: renaming it asks for nothing.
    store.apply('Renamed', (d) => ({
      ...d,
      widgets: d.widgets.map((w) => (w.id === 'by-status' ? { ...w, title: 'Statuses' } : w)),
    }));
    harness.detectChanges();
    expect(await previews(http)).toEqual([]);
    // Two quick edits of its limit: one preview, of the last.
    for (const limit of [10, 12]) {
      store.apply(
        'Limited',
        (d) => ({
          ...d,
          widgets: d.widgets.map((w) =>
            w.id === 'by-status' ? { ...w, config: { ...w.config, limit } as typeof w.config } : w,
          ),
        }),
        'limit',
      );
      harness.detectChanges();
    }
    const asked = await previews(http);
    expect(asked.map((body) => body['widget'])).toEqual(['by-status']);
    const slice = asked[0]['slice'] as Definition;
    expect((slice.widgets[0].config as { limit: number }).limit).toBe(12);
    // Moving a widget changes no slice.
    store.apply('Moved', (d) => ({
      ...d,
      layout: { ...d.layout, items: { ...d.layout.items, 'by-city': { x: 0, y: 7, w: 6, h: 6 } } },
    }));
    harness.detectChanges();
    expect(await previews(http)).toEqual([]);
  });

  it("previews nothing of a widget still being made, and changes no other's preview", async () => {
    const { page, http, harness } = await open();
    button(page.querySelector('.palette')!, 'Bar chart').click();
    harness.detectChanges();
    // Nothing is chosen in it: the other widgets' slices don't hold it, so they aren't asked again.
    expect(await previews(http)).toEqual([]);
    harness.detectChanges();
    expect(textOf(page.querySelector('[data-gd-cell] .missing'))).toBe('Choose what its bars are.');
    // The Issues tab counts them while it isn't shown, and lists them when it is.
    const tab = [...page.querySelectorAll<HTMLElement>('[role=tab]')].find((t) =>
      textOf(t).startsWith('Issues'),
    )!;
    expect(textOf(tab)).toBe('Issues (1)');
    tab.click();
    harness.detectChanges();
    await settle();
    expect(textOf(page.querySelector('gd-issues-panel'))).toContain(
      'Bar chart bar: Choose what its bars are.',
    );
  });

  it('saves with Ctrl+S at the version read, and undoes and redoes by name', async () => {
    const { page, http, store, harness } = await open();
    page.querySelector<HTMLElement>('[data-gd-cell="0,1,6,6"] .cover')!.click();
    await settle();
    harness.detectChanges();
    const title = page.querySelector<HTMLInputElement>('gd-widget-panel input')!;
    title.value = 'Statuses';
    title.dispatchEvent(new Event('input'));
    harness.detectChanges();
    expect(textOf(page.querySelector('.edited'))).toBe('Not saved');
    press('z', { ctrlKey: true });
    harness.detectChanges();
    expect(store.draft().widgets[1].title).toBe('Orders by status');
    expect(said).toContain('Undid: Renamed Orders by status');
    press('y', { ctrlKey: true });
    harness.detectChanges();
    expect(store.draft().widgets[1].title).toBe('Statuses');
    // In a field, Ctrl+Z is the field's own.
    press('z', { ctrlKey: true }, title);
    expect(store.draft().widgets[1].title).toBe('Statuses');
    press('s', { ctrlKey: true });
    const put = await requestTo(http, '/api/dashboards/1', 'PUT');
    expect([put.request.body.version, put.request.body.name]).toEqual([2, 'Sales']);
    expect(put.request.body.definition.widgets[1].title).toBe('Statuses');
    put.flush(dashboardOf(put.request.body.definition, { version: 3 }));
    await settle();
    harness.detectChanges();
    expect(page.querySelector('.edited')).toBeNull();
    expect(store.saved().version).toBe(3);
    expect(said).toContain('Sales is saved');
  });

  it('after a conflict, reads the dashboard again keeping the edits, and saves over it', async () => {
    const { page, http, store, harness } = await open();
    store.apply('Renamed', (d) => ({
      ...d,
      widgets: d.widgets.map((w) => (w.id === 'by-city' ? { ...w, title: 'Cities' } : w)),
    }));
    harness.detectChanges();
    press('s', { ctrlKey: true });
    (await requestTo(http, '/api/dashboards/1', 'PUT')).flush(
      { status: 409, code: 'concurrency-conflict', title: 'Changed since' },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();
    harness.detectChanges();
    const alert = page.querySelector('[role=alert]')!;
    expect(textOf(alert)).toContain('changed elsewhere');
    button(alert, 'Read it again').click();
    (await requestTo(http, '/api/dashboards/1')).flush(
      dashboardOf(salesDefinition(), { version: 5 }),
    );
    await settle();
    harness.detectChanges();
    expect(store.draft().widgets[2].title).toBe('Cities');
    press('s', { ctrlKey: true });
    const put = await requestTo(http, '/api/dashboards/1', 'PUT');
    expect(put.request.body.version).toBe(5);
    put.flush(dashboardOf(put.request.body.definition, { version: 6 }));
  });

  it('places what a save refused by field, and goes to it', async () => {
    const { page, http, store, harness } = await open();
    store.apply('Renamed', (d) => ({ ...d, refresh: { mode: 'interval', seconds: 10 } }));
    press('s', { ctrlKey: true });
    (await requestTo(http, '/api/dashboards/1', 'PUT')).flush(
      {
        status: 400,
        code: 'invalid-request',
        title: 'Some of it is wrong',
        errors: {
          'definition.widgets[1].config.measures[0].field': ['Give the field'],
          'definition.refresh.seconds': ['Refresh every 30 seconds at least'],
        },
      },
      { status: 400, statusText: 'Bad request' },
    );
    await settle();
    harness.detectChanges();
    const issues = page.querySelector('gd-issues-panel')!;
    const items = [...issues.querySelectorAll('li')].map((li) => wordsOf(li));
    expect(items.slice(0, 2)).toEqual([
      'Error: Orders by status: Give the field Go to',
      'Error: Refresh every 30 seconds at least Go to',
    ]);
    button(issues, 'Go to').click();
    harness.detectChanges();
    expect(store.selected()).toBe('by-status');
  });

  it('moves a widget by keyboard, each step said, one step of the history', async () => {
    const { page, store, harness } = await open();
    const handle = page.querySelector<HTMLButtonElement>('[data-gd-move="by-city"]')!;
    expect(handle.getAttribute('aria-roledescription')).toBe('movable widget');
    handle.focus();
    press('Enter', {}, handle);
    harness.detectChanges();
    expect(said.at(-1)).toMatch(/^Moving Customers by city: column 7, row 2, 6 by 6\./);
    press('ArrowLeft', {}, handle);
    harness.detectChanges();
    expect(said.at(-1)).toBe('Customers by city: column 6, row 2, 6 by 6');
    press('ArrowLeft', { shiftKey: true }, handle);
    harness.detectChanges();
    expect(said.at(-1)).toBe('Customers by city: column 6, row 2, 5 by 6');
    // Nothing is the draft's till it is dropped.
    expect(store.draft().layout.items['by-city']).toEqual({ x: 6, y: 1, w: 6, h: 6 });
    press('Enter', {}, handle);
    harness.detectChanges();
    expect(said.at(-1)).toBe('Dropped Customers by city: column 6, row 2, 5 by 6');
    expect(store.draft().layout.items['by-city']).toEqual({ x: 5, y: 1, w: 5, h: 6 });
    expect(store.history.undoLabel()).toBe('Moved Customers by city');
    // Escape puts a widget back.
    press('Enter', {}, handle);
    press('ArrowLeft', {}, handle);
    press('Escape', {}, handle);
    harness.detectChanges();
    expect(store.draft().layout.items['by-city']).toEqual({ x: 5, y: 1, w: 5, h: 6 });
  });

  it('lays a narrower width out by hand, its moves its own, and derives it again', async () => {
    const { page, store, harness } = await open();
    [...page.querySelectorAll<HTMLButtonElement>('mat-button-toggle button')]
      .find((b) => textOf(b) === 'Medium')!
      .click();
    harness.detectChanges();
    // Not laid out by hand: nothing moves there.
    expect(page.querySelector('[data-gd-move]')).toBeNull();
    [...page.querySelectorAll<HTMLElement>('[role=tab]')]
      .find((t) => textOf(t) === 'Layout')!
      .click();
    harness.detectChanges();
    await settle();
    button(page.querySelector('gd-layout-panel')!, 'Lay it out by hand').click();
    harness.detectChanges();
    expect(store.draft().layout.overrides['medium'].items['by-city']).toEqual({
      x: 3,
      y: 1,
      w: 3,
      h: 6,
    });
    const handle = page.querySelector<HTMLButtonElement>('[data-gd-move="by-city"]')!;
    press('Enter', {}, handle);
    press('ArrowLeft', {}, handle);
    press('Enter', {}, handle);
    harness.detectChanges();
    // The widget it lands on goes below it, at this width only.
    expect(store.draft().layout.overrides['medium'].items).toMatchObject({
      'by-city': { x: 2, y: 1, w: 3, h: 6 },
      'by-status': { x: 0, y: 7, w: 3, h: 6 },
    });
    expect(store.draft().layout.items['by-city']).toEqual({ x: 6, y: 1, w: 6, h: 6 });
    button(page.querySelector('gd-layout-panel')!, 'Derive it from Wide again').click();
    harness.detectChanges();
    expect(store.draft().layout.overrides).toEqual({});
  });

  it('asks before leaving with changes not saved', async () => {
    const { store, router, harness } = await open();
    store.apply('Renamed', (d) => ({ ...d, refresh: { mode: 'interval', seconds: 60 } }));
    harness.detectChanges();
    const leaving = router.navigateByUrl('/dashboards');
    await settle(5);
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Leave without saving?');
    button(dialog, 'Stay').click();
    expect(await leaving).toBe(false);
    expect(router.url).toBe('/dashboards/1/edit');
  });

  it('makes a new dashboard: a widget added from the palette, saved under its name, at its own address', async () => {
    const page = await openPage('/dashboards/new');
    page.harness.detectChanges();
    const store = page.harness.routeDebugElement!.injector.get(EditorStore);
    expect(textOf(page.page.querySelector('gd-editor-canvas'))).toContain(
      'A blank dashboard is a blank page',
    );
    button(page.page.querySelector('.palette')!, 'Text').click();
    page.harness.detectChanges();
    expect(store.draft().widgets.map((w) => w.id)).toEqual(['text']);
    expect(store.draft().layout.items['text']).toEqual({ x: 0, y: 0, w: 12, h: 2 });
    expect(store.selected()).toBe('text');
    // A name first.
    press('s', { ctrlKey: true });
    await page.harness.fixture.whenStable();
    page.harness.detectChanges();
    expect(textOf(page.page.querySelector('.name .problem'))).toBe('Name the dashboard');
    expect(document.activeElement).toBe(page.page.querySelector('input[required]'));
    const name = page.page.querySelector<HTMLInputElement>('input[required]')!;
    name.value = 'Board';
    name.dispatchEvent(new Event('input'));
    press('s', { ctrlKey: true });
    const post = await requestTo(page.http, '/api/dashboards', 'POST');
    expect([post.request.body.name, post.request.body.definition.widgets.length]).toEqual([
      'Board',
      1,
    ]);
    post.flush(dashboardOf(post.request.body.definition, { id: 7, name: 'Board', version: 1 }));
    await settle(5);
    expect(TestBed.inject(Router).url).toBe('/dashboards/7/edit');
    (await requestTo(page.http, '/api/dashboards/7')).flush(
      dashboardOf(post.request.body.definition, { id: 7, name: 'Board', version: 1 }),
    );
  });
});
