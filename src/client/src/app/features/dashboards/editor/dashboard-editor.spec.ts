import { LiveAnnouncer } from '@angular/cdk/a11y';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import {
  dashboardOf,
  rowsOf,
  salesDefinition,
  tabbedDefinition,
} from '../../../../testing/dashboards';
import { FakeECharts, fakeEChartsProviders } from '../../../../testing/echarts';
import { eventually, requestTo, settle } from '../../../../testing/http';
import { openPage, pageProviders, textOf, wordsOf } from '../../../../testing/pages';
import { FakeResizeObserver } from '../../../../testing/resize';
import { dashboardRoutes } from '../dashboards.routes';
import type { Definition, Filter, KpiConfig } from '../model/definition';
import { removeFilter } from '../model/definition-ops';
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

  it('draws its previews with the palettes it names, asking no rows again for them', async () => {
    const { http, store, harness, page } = await open();
    const slices = () =>
      (
        charts.chartIn(page.querySelector('[data-gd-cell="6,1,6,6"]')!)!.option.series as {
          data: { itemStyle: { color: string } }[];
        }[]
      )[0].data.map((d) => d.itemStyle.color);
    await eventually(harness, () => slices().length === 1);
    const before = slices();
    store.apply('Coloured', (d) => ({ ...d, palette: 5 }));
    harness.detectChanges();
    await settle();
    (await requestTo(http, '/api/palettes/5')).flush({
      id: 5,
      name: 'Statuses',
      description: null,
      owner: 'ada',
      isMine: true,
      canEdit: true,
      definition: {
        colors: [{ light: '#121212', dark: null }],
        assign: 'order',
        distinct: true,
        whenOut: 'repeat',
        matching: {
          ignoreCase: true,
          ignoreWhitespace: false,
          ignoreBrackets: false,
          ignoreAccents: false,
        },
        overrides: [],
      },
      hash: 'h'.repeat(64),
      usedBy: 1,
      dashboards: [],
      createdAt: '2026-03-01T08:00:00Z',
      updatedAt: '2026-03-01T08:00:00Z',
      version: 0,
    });
    await settle();
    harness.detectChanges();
    expect(await previews(http)).toEqual([]);
    await eventually(harness, () => slices()[0] === '#121212');
    expect(slices()).toEqual(['#121212']);
    // One that isn't there: the colours as they were.
    store.apply('Coloured', (d) => ({ ...d, palette: 6 }));
    harness.detectChanges();
    await settle();
    (await requestTo(http, '/api/palettes/6')).flush(
      { status: 404, code: 'not-found', title: 'Not found' },
      { status: 404, statusText: 'Not found' },
    );
    await settle();
    harness.detectChanges();
    expect(await previews(http)).toEqual([]);
    await eventually(harness, () => slices()[0] === before[0]);
    expect(slices()).toEqual(before);
  });

  /** A palette as its own page reads it: Statuses, ada's, by order. */
  function paletteDto(extra: Record<string, unknown> = {}) {
    return {
      id: 5,
      name: 'Statuses',
      description: null,
      owner: 'ada',
      isMine: true,
      canEdit: true,
      definition: {
        colors: [
          { light: '#121212', dark: null },
          { light: '#343434', dark: null },
        ],
        assign: 'order',
        distinct: true,
        whenOut: 'repeat',
        matching: {
          ignoreCase: true,
          ignoreWhitespace: false,
          ignoreBrackets: false,
          ignoreAccents: false,
        },
        overrides: [] as unknown[],
      },
      hash: 'h'.repeat(64),
      usedBy: 2,
      dashboards: [],
      createdAt: '2026-03-01T08:00:00Z',
      updatedAt: '2026-03-01T08:00:00Z',
      version: 2,
      ...extra,
    };
  }

  /** The pie's slices' colours on the canvas. */
  function pieColors(page: HTMLElement): string[] {
    return (
      charts.chartIn(page.querySelector('[data-gd-cell="6,1,6,6"]')!)!.option.series as {
        data: { itemStyle: { color: string } }[];
      }[]
    )[0].data.map((d) => d.itemStyle.color);
  }

  /** Sets the chart's first label's colour (written) in the dialog, once `asked` has answered what it asks first. */
  async function setFirstColor(
    page: HTMLElement,
    harness: { detectChanges(): void },
    color: string,
    asked: () => Promise<void> = async () => undefined,
  ): Promise<Element> {
    await eventually(harness, () => page.querySelector('gd-chart-colors li button') !== null);
    page.querySelector<HTMLButtonElement>('gd-chart-colors li button')!.click();
    await asked();
    await eventually(harness, () =>
      textOf(document.querySelector('gd-set-color-dialog')).includes('The colour of'),
    );
    const dialog = document.querySelector('gd-set-color-dialog')!;
    const said = textOf(dialog);
    const hex = dialog.querySelector<HTMLInputElement>('gd-color-field input[matinput]')!;
    hex.value = color;
    hex.dispatchEvent(new Event('input'));
    harness.detectChanges();
    button(dialog, 'Set colour').click();
    return { textContent: said } as Element;
  }

  it("gives a chart's labels their colours from its settings, saved in its palette", async () => {
    const { page, http, harness } = await open({ ...salesDefinition(), palette: 5 });
    (await requestTo(http, '/api/palettes/5')).flush(paletteDto());
    await eventually(harness, () => pieColors(page)[0] === '#121212');
    page.querySelector<HTMLElement>('[data-gd-cell="6,1,6,6"] .cover')!.click();
    await settle();
    harness.detectChanges();
    await eventually(harness, () => textOf(page.querySelector('gd-chart-colors')).includes('open'));
    expect(textOf(page.querySelector('gd-chart-colors'))).toContain(
      "The dashboard's palette, Statuses: colours set here are saved in it",
    );
    expect(wordsOf(page.querySelector('gd-chart-colors li'))).toBe('open, by order Set colour…');
    // Read as it is now, then saved with the label's colour, at its version.
    const dialog = await setFirstColor(page, harness, '#abcdef', async () =>
      (await requestTo(http, '/api/palettes/5')).flush(paletteDto()),
    );
    expect(textOf(dialog)).toContain('Saved in Statuses, which 2 dashboards use.');
    const saved = await requestTo(http, '/api/palettes/5', 'PUT');
    expect(saved.request.body.version).toBe(2);
    expect(saved.request.body.definition.overrides).toEqual([
      { label: 'open', color: { light: '#abcdef', dark: null } },
    ]);
    saved.flush(
      paletteDto({
        version: 3,
        hash: 'i'.repeat(64),
        definition: {
          ...paletteDto().definition,
          overrides: saved.request.body.definition.overrides,
        },
      }),
    );
    // The chart follows, and says the colour is its label's own; the dashboard has no step to undo.
    await eventually(harness, () => pieColors(page)[0] === '#abcdef');
    expect(pieColors(page)).toEqual(['#abcdef']);
    expect(wordsOf(page.querySelector('gd-chart-colors li'))).toBe('open, its own Set colour…');
    expect(await previews(http)).toEqual([]);
  });

  it('makes a palette for a dashboard without one, as its charts were drawn', async () => {
    const { page, http, harness, store } = await open();
    page.querySelector<HTMLElement>('[data-gd-cell="6,1,6,6"] .cover')!.click();
    await settle();
    harness.detectChanges();
    expect(textOf(page.querySelector('gd-chart-colors'))).toContain(
      'No palette: the built-in colours. Setting a colour makes a palette for the dashboard.',
    );
    await setFirstColor(page, harness, '#abcdef');
    const made = await requestTo(http, '/api/palettes', 'POST');
    expect(made.request.body).toMatchObject({
      name: 'Sales colours',
      definition: {
        assign: 'order',
        distinct: false,
        whenOut: 'neutral',
        overrides: [{ label: 'open', color: { light: '#abcdef', dark: null } }],
      },
    });
    made.flush(
      paletteDto({ id: 7, name: 'Sales colours', definition: made.request.body.definition }),
    );
    await eventually(harness, () => store.draft().palette === 7);
    expect(store.draft().palette).toBe(7);
    expect(store.history.undoLabel()).toBe('Made a palette');
    await eventually(harness, () => pieColors(page)[0] === '#abcdef');
    expect(pieColors(page)).toEqual(['#abcdef']);
  });

  it('says when a palette it names is gone, in the Dashboard tab', async () => {
    const { http, harness, store, page } = await open({ ...salesDefinition(), palette: 9 });
    (await requestTo(http, '/api/palettes/9')).flush(
      { status: 404, code: 'not-found', title: 'Not found' },
      { status: 404, statusText: 'Not found' },
    );
    await eventually(harness, () => store.issues().some((i) => i.field === 'palette'));
    const issue = store.issues().find((i) => i.field === 'palette')!;
    expect(issue).toMatchObject({ severity: 'warning', place: { panel: 'refresh' } });
    expect(issue.message).toContain("Its charts' palette is gone");
    const tabs = [...page.querySelectorAll('[role=tab]')].map((t) => wordsOf(t));
    expect(tabs).toContain('Dashboard');
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
    button(page.querySelector('.add-widgets')!, 'Bar chart').click();
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

  it('adds a heading a row high, its text, size and alignment its own, asking for no rows', async () => {
    const { page, http, store, harness } = await open();
    button(page.querySelector('.add-widgets')!, 'Heading').click();
    harness.detectChanges();
    const made = store.selected()!;
    const config = () => store.draft().widgets.find((w) => w.id === made)?.config;
    expect([config(), store.draft().layout.items[made]]).toEqual([
      { kind: 'heading', text: '', size: 'large', align: 'start' },
      { x: 0, y: 7, w: 12, h: 1 },
    ]);
    await eventually(harness, () => !!page.querySelector('gd-heading-settings input'));
    const panel = page.querySelector('gd-widget-panel')!;
    // A heading is its own title, and stands on the page as no region of its own.
    expect([...panel.querySelectorAll('mat-label')].map((l) => textOf(l))).not.toContain('Title');
    const cell = page.querySelector('[data-gd-cell="0,7,12,1"]')!;
    expect(cell.querySelector('section')!.hasAttribute('aria-label')).toBe(false);

    const text = panel.querySelector<HTMLInputElement>('gd-heading-settings input')!;
    text.value = 'Orders';
    text.dispatchEvent(new Event('input'));
    harness.detectChanges();
    await eventually(harness, () => !!cell.querySelector('gd-heading-widget h2'));
    expect(textOf(cell.querySelector('gd-heading-widget h2'))).toBe('Orders');
    button(panel, 'Medium').click();
    panel.querySelector<HTMLElement>('button[aria-label=Centred]')!.click();
    harness.detectChanges();
    expect(config()).toEqual({ kind: 'heading', text: 'Orders', size: 'medium', align: 'center' });
    expect(textOf(cell.querySelector('gd-heading-widget h3'))).toBe('Orders');
    expect(store.nameOf(made)).toBe('Heading: Orders');
    expect(await previews(http)).toEqual([]);
    // Its settings' edits within a second are one step, as every widget's are.
    press('z', { ctrlKey: true });
    harness.detectChanges();
    expect(config()).toEqual({ kind: 'heading', text: '', size: 'large', align: 'start' });
  });

  it('adds a KPI compared with the period filter, its format, comparison and sparkline its settings', async () => {
    const period: Filter = {
      id: 'period',
      label: 'Period',
      field: { source: 'orders', path: [], column: 'order_date' },
      kind: 'relative',
      value: {
        op: 'relative',
        value: null,
        valueTo: null,
        values: null,
        relative: { unit: 'day', count: 30, mode: 'last' },
      },
      visible: true,
      editable: true,
      multiple: false,
      except: [],
    };
    const { page, http, store, harness } = await open(salesDefinition({ filters: [period] }));
    button(page.querySelector('.add-widgets')!, 'KPI').click();
    harness.detectChanges();
    const made = store.selected()!;
    const config = () => store.draft().widgets.find((w) => w.id === made)!.config as KpiConfig;
    expect(config()).toEqual({
      kind: 'kpi',
      source: 'orders',
      conditions: [],
      emits: false,
      listens: { mode: 'all', widgets: [] },
      measure: { aggregate: 'count', field: null, label: 'Rows', format: null },
      compare: { filter: 'period', good: 'up' },
      sparkline: null,
    });
    expect(store.draft().layout.items[made]).toEqual({ x: 0, y: 7, w: 3, h: 3 });

    // Its preview is its number, and how it changed.
    await settle(5);
    const asked = http.match((r) => r.url === '/api/dashboards/data' && r.body.widget === made);
    expect(asked).toHaveLength(1);
    asked[0].flush(
      rowsOf('Rows', [], {
        columns: [rowsOf('Rows', []).columns[1]],
        rows: [['1250']],
        previous: { value: '1000', from: '2026-01-30', to: '2026-02-28' },
      }),
    );
    const cell = page.querySelector('[data-gd-cell="0,7,3,3"]')!;
    await eventually(harness, () => !!cell.querySelector('gd-kpi-widget .value'));
    expect(textOf(cell.querySelector('.value'))).toBe('1,250');
    expect(textOf(cell.querySelector('.percent'))).toBe('+25%');

    await eventually(harness, () => !!page.querySelector('gd-kpi-settings'));
    const settings = page.querySelector('gd-kpi-settings')!;
    // How its number shows is its measure's format, in the measure list.
    const format = settings.querySelector('gd-measure-list gd-number-format')!;
    const input = (label: string) =>
      [...format.querySelectorAll('mat-form-field')]
        .find((f) => textOf(f.querySelector('mat-label')) === label)!
        .querySelector<HTMLInputElement>('input')!;
    type(input('Decimals'), '1');
    type(input('Before'), 'R ');
    harness.detectChanges();
    expect(config().measure.format).toEqual({
      decimals: 1,
      prefix: 'R ',
      suffix: null,
      compact: false,
    });
    expect(textOf(format.querySelector('mat-hint'))).toBe('As R 1,234.6');

    /** Chooses an option of a select of its settings, by their labels. */
    const choose = async (label: string, option: string) => {
      const field = [...settings.querySelectorAll('mat-form-field')].find(
        (f) => textOf(f.querySelector('mat-label')) === label,
      )!;
      field.querySelector<HTMLElement>('.mat-mdc-select-trigger')!.click();
      harness.detectChanges();
      await settle();
      [...document.querySelectorAll<HTMLElement>('mat-option')]
        .find((o) => textOf(o) === option)!
        .click();
      harness.detectChanges();
      await settle();
    };
    /** A toggle of its settings, by its label. */
    const toggle = (label: string) =>
      [...settings.querySelectorAll('mat-slide-toggle')].find((t) => textOf(t) === label)!;
    const flip = (label: string) => {
      toggle(label).querySelector('button')!.click();
      harness.detectChanges();
    };
    await choose('Better when it goes', 'Down');
    expect(config().compare).toEqual({ filter: 'period', good: 'down' });
    flip('With the period before');
    expect(config().compare).toBeNull();
    flip('With the period before');
    expect(config().compare).toEqual({ filter: 'period', good: 'up' });

    // A sparkline wants its date first, which the canvas says.
    flip('A sparkline of it over time');
    expect(config().sparkline).toEqual({
      field: { path: [], column: '' },
      bucket: 'day',
      label: 'Day',
    });
    harness.detectChanges();
    expect(textOf(cell.querySelector('.missing'))).toBe('Choose the date its sparkline is along.');
    await choose('By', 'Week');
    expect(config().sparkline).toEqual({
      field: { path: [], column: '' },
      bucket: 'week',
      label: 'Week',
    });

    // Removing the filter compares it with nothing.
    store.apply('Removed', (d) => removeFilter(d, 'period'));
    expect(config().compare).toBeNull();
    harness.detectChanges();
    expect(toggle('With the period before').querySelector('button')!.disabled).toBe(true);
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

  it('makes a new dashboard: a widget added, saved under its name, at its own address', async () => {
    const page = await openPage('/dashboards/new');
    page.harness.detectChanges();
    const store = page.harness.routeDebugElement!.injector.get(EditorStore);
    expect(textOf(page.page.querySelector('gd-editor-canvas'))).toContain(
      'A blank dashboard is a blank page',
    );
    button(page.page.querySelector('.add-widgets')!, 'Text').click();
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

  /** Shows a panel of the side's, by its tab's label. */
  async function panel(page: HTMLElement, harness: { detectChanges(): void }, label: string) {
    [...page.querySelectorAll<HTMLElement>('.side [role=tab]')]
      .find((t) => wordsOf(t) === label)!
      .click();
    harness.detectChanges();
    await settle();
    harness.detectChanges();
  }

  /** Types into a field, as a person does. */
  function type(input: HTMLInputElement, text: string): void {
    input.value = text;
    input.dispatchEvent(new Event('input'));
  }

  it('splits a dashboard into tabs, adds, renames, moves and removes them, each a step undone', async () => {
    const { page, store, harness } = await open();
    await panel(page, harness, 'Layout');
    const layout = () => page.querySelector('gd-layout-panel')!;
    button(layout(), 'Split into tabs').click();
    harness.detectChanges();
    expect(store.draft().tabs).toEqual([{ id: 'overview', label: 'Overview' }]);
    expect(store.draft().widgets.every((w) => w.tab === 'overview')).toBe(true);
    await settle();
    harness.detectChanges();
    expect(
      [...page.querySelectorAll('gd-editor-canvas [data-gd-tab]')].map((t) => textOf(t)),
    ).toEqual(['Overview']);

    const fields = () => [...layout().querySelectorAll<HTMLInputElement>('input')];
    type(
      fields().find((f) => f.closest('.new-tab'))!,
      'Customers',
    );
    harness.detectChanges();
    button(layout(), 'Add').click();
    harness.detectChanges();
    await settle();
    harness.detectChanges();
    expect(store.draft().tabs?.map((t) => t.id)).toEqual(['overview', 'customers']);
    expect(store.tab()).toBe('customers');
    expect(textOf(page.querySelector('gd-editor-canvas .empty'))).toContain('This tab is empty');

    type(fields()[1], 'People');
    harness.detectChanges();
    expect(store.draft().tabs?.[1]).toEqual({ id: 'customers', label: 'People' });
    expect(store.history.undoLabel()).toBe('Renamed a tab');
    layout().querySelector<HTMLButtonElement>('[aria-label="Move People earlier"]')!.click();
    harness.detectChanges();
    expect(store.draft().tabs?.map((t) => t.id)).toEqual(['customers', 'overview']);
    store.undo();
    harness.detectChanges();
    expect(store.draft().tabs?.map((t) => t.id)).toEqual(['overview', 'customers']);
    layout().querySelector<HTMLButtonElement>('[aria-label="Remove People"]')!.click();
    harness.detectChanges();
    expect(store.draft().tabs?.map((t) => t.id)).toEqual(['overview']);
    button(layout(), 'Stop using tabs').click();
    harness.detectChanges();
    expect('tabs' in store.draft()).toBe(false);
    expect(store.draft().widgets.some((w) => 'tab' in w)).toBe(false);
    expect(store.draft()).toEqual(salesDefinition());
  });

  it('previews the tab shown alone, adds widgets to it, and moves one to another by its Tab', async () => {
    const { page, store, harness, http, first } = await open(tabbedDefinition());
    expect(first.map((body) => body['widget'])).toEqual(['by-status']);
    page.querySelector<HTMLElement>('gd-editor-canvas [data-gd-tab="customers"]')!.click();
    harness.detectChanges();
    expect((await previews(http)).map((body) => body['widget'])).toEqual(['by-city']);
    button(page.querySelector('.add-widgets')!, 'Text').click();
    harness.detectChanges();
    const made = store.selected()!;
    expect(store.draft().widgets.find((w) => w.id === made)?.tab).toBe('customers');
    expect(store.draft().layout.items[made]).toEqual({ x: 0, y: 6, w: 12, h: 2 });

    store.select('by-city');
    harness.detectChanges();
    await settle();
    const field = [...page.querySelectorAll('gd-widget-panel mat-form-field')].find(
      (f) => textOf(f.querySelector('mat-label')) === 'Tab',
    )!;
    field.querySelector<HTMLElement>('.mat-mdc-select-trigger')!.click();
    harness.detectChanges();
    await settle();
    [...document.querySelectorAll<HTMLElement>('mat-option')]
      .find((o) => textOf(o) === 'Orders')!
      .click();
    harness.detectChanges();
    expect(store.draft().widgets.find((w) => w.id === 'by-city')?.tab).toBe('orders');
    expect(store.draft().layout.items['by-city']).toEqual({ x: 6, y: 1, w: 6, h: 6 });
    expect(store.history.undoLabel()).toBe('Moved Customers by city to Orders');
    // The canvas follows it; shown elsewhere, it is let go of.
    expect([store.tab(), store.selected()]).toEqual(['orders', 'by-city']);
    page.querySelector<HTMLElement>('gd-editor-canvas [data-gd-tab="customers"]')!.click();
    harness.detectChanges();
    await settle();
    expect(store.selected()).toBeNull();
    store.select('by-status');
    expect(store.tab()).toBe('orders');
  });

  it("moves a widget on the tab shown, the other tabs' cells as they were", async () => {
    const { page, store, harness, http } = await open(tabbedDefinition());
    page.querySelector<HTMLElement>('gd-editor-canvas [data-gd-tab="customers"]')!.click();
    harness.detectChanges();
    await previews(http);
    harness.detectChanges();
    const handle = page.querySelector<HTMLButtonElement>('[data-gd-move="by-city"]')!;
    press('Enter', {}, handle);
    press('ArrowRight', {}, handle);
    press('ArrowDown', { shiftKey: true }, handle);
    press('Enter', {}, handle);
    harness.detectChanges();
    expect(store.draft().layout.items).toEqual({
      title: { x: 0, y: 0, w: 12, h: 1 },
      'by-status': { x: 0, y: 1, w: 6, h: 6 },
      'by-city': { x: 1, y: 0, w: 6, h: 7 },
    });
  });

  it('asks for a new tab from the canvas in the Layout panel, and places refusals of tabs there', async () => {
    const { page, store, harness } = await open(tabbedDefinition());
    page.querySelector<HTMLButtonElement>('gd-editor-canvas [aria-label="Add a tab"]')!.click();
    harness.detectChanges();
    await settle(5);
    harness.detectChanges();
    expect(store.panel()).toBe(3);
    expect((document.activeElement as HTMLElement | null)?.closest('.new-tab')).not.toBeNull();
    store.apply('Renamed a tab', (d) => ({
      ...d,
      tabs: d.tabs!.map((t) => (t.id === 'orders' ? { ...t, label: ' ' } : t)),
    }));
    expect(store.issues()).toContainEqual({
      severity: 'error',
      message: 'Tab 1 has no label: give it one.',
      place: { panel: 'layout' },
      field: 'tabs[0].label',
    });
  });
});
