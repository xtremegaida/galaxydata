import { Location } from '@angular/common';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, TitleStrategy, type Routes } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { HttpTestingController } from '@angular/common/http/testing';
import { problemBody, sessionOf } from '../../../testing/auth';
import { pagesFetched } from '../../../testing/browse';
import { requestTo, settle } from '../../../testing/http';
import { FakeMonaco, fakeMonacoProviders } from '../../../testing/monaco';
import { alertsOf, clickButton, openPage, pageProviders, textOf } from '../../../testing/pages';
import {
  diagnosticOf,
  executeUrl,
  explainOf,
  explainUrl,
  linkUrl,
  orderResultRows,
  queryPageOf,
  savedQueryOf,
  savedUrl,
  summaryOf,
  validateUrl,
  validationOf,
  type QueryValidationDto,
  type SavedQueryDto,
} from '../../../testing/query';
import { MonacoLoader } from '../../core/editor/monaco-loader';
import { PageTitles } from '../../core/page-titles';
import { LastQuery, newQueryState } from '../../core/query/last-query';
import { type QueryAddress, queryUrlTree } from '../../core/query/query-url';
import { BROWSE_PAGE_SIZE } from '../browse/grid/grid-settings';
import { QUERY_WAITS } from './query-page';
import { queryRoutes } from './query.routes';

@Component({ template: '<p>Browsing</p>' })
class Browsing {}

const routes: Routes = [
  { path: 'query', children: queryRoutes },
  { path: 'browse', children: [{ path: '**', component: Browsing }] },
];

const query = 'shop.orders.where(total > $min)';
const minimum = { name: 'min', given: false, type: 'decimal(10,2)' };

describe('QueryPage', () => {
  let monaco: FakeMonaco;

  beforeEach(() => {
    localStorage.clear();
    monaco = new FakeMonaco();
    TestBed.configureTestingModule({
      providers: [
        pageProviders(routes),
        fakeMonacoProviders(monaco),
        { provide: QUERY_WAITS, useValue: { check: 1, write: 1 } },
        { provide: BROWSE_PAGE_SIZE, useValue: 3 },
        { provide: TitleStrategy, useClass: PageTitles },
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

  /** The page at an address, Monaco loaded. */
  async function opened(url = '/query', session = sessionOf()) {
    const it = await openPage(url, session);
    const router = TestBed.inject(Router);
    const shown = async (turns = 3) => {
      for (let turn = 0; turn < turns; turn++) {
        await settle();
        it.harness.detectChanges();
      }
    };
    await shown();
    const editor = () => {
      const found = monaco.live.find((each) => each.options['ariaLabel'] === 'The query');
      if (!found) {
        throw new Error("The query's editor isn't there");
      }
      return found;
    };
    /** Types the query's whole text. */
    const type = async (text: string) => {
      editor().model.type(text);
      await shown();
    };
    /** Answers the check of the query as it is typed; its body. */
    const check = async (validation: QueryValidationDto = validationOf()) => {
      const request = await requestTo(it.http, validateUrl, 'POST');
      request.flush(validation);
      await shown();
      return request.request.body as Record<string, unknown>;
    };
    /** Answers the run of the query; its body. */
    const run = async (rows = orderResultRows(2)) => {
      const request = await requestTo(it.http, executeUrl, 'POST');
      request.flush(queryPageOf(rows));
      await shown();
      await pagesFetched();
      await shown();
      return request.request.body as Record<string, unknown>;
    };
    /** Answers the read of a saved query. */
    const read = async (saved: SavedQueryDto = savedQueryOf()) => {
      const request = await requestTo(it.http, `${savedUrl}/${saved.id}`);
      request.flush(saved);
      await shown();
    };
    const field = (name: string) =>
      it.page.querySelector<HTMLInputElement>(`input[data-parameter="${name}"]`);
    const typeValue = async (name: string, value: string) => {
      const input = field(name)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      await shown();
    };
    const press = async (text: string) => {
      const button = [...it.page.querySelectorAll<HTMLButtonElement>('button')].find((candidate) =>
        textOf(candidate).endsWith(text),
      );
      if (!button) {
        throw new Error(`No button "${text}"`);
      }
      button.click();
      await shown();
    };
    const address = () => decodeURIComponent(router.url);
    return {
      ...it,
      router,
      shown,
      editor,
      type,
      check,
      run,
      read,
      field,
      typeValue,
      press,
      address,
    };
  }

  it('opens a new query: an empty editor, nothing run', async () => {
    const { page } = await opened();
    expect(textOf(page.querySelector('h1'))).toBe('New query');
    expect(textOf(page.querySelector('.subtitle'))).toBe('Not saved');
    expect(textOf(page.querySelector('.empty'))).toBe(
      'Run the query (Ctrl+Enter) to see its rows.',
    );
    expect(monaco.last.model.language).toBe('gdq');
    expect(TestBed.inject(Title).getTitle()).toBe('Query · GalaxyData');
    expect(textOf(page.querySelector('.keys'))).toContain('Ctrl+Enter runs the query.');
  });

  it('checks the query as typing pauses: what is wrong marked in it, the parameters it uses asked for', async () => {
    const { type, check, page, typeValue, field } = await opened();
    await type('shop.orders.where(totl > $min)');
    const body = await check(
      validationOf({
        success: false,
        diagnostics: [diagnosticOf(18, 22)],
        parameters: [minimum],
      }),
    );
    expect(body).toEqual({ text: 'shop.orders.where(totl > $min)', parameters: [] });
    expect(monaco.last.model.markers).toEqual([
      {
        severity: 8,
        message: "There is no 'totl' here (GDQ2001)",
        startLineNumber: 1,
        startColumn: 19,
        endLineNumber: 1,
        endColumn: 23,
      },
    ]);
    expect(field('min')).not.toBeNull();
    expect(textOf(page.querySelector('mat-hint'))).toBe('decimal');
    expect(textOf(page.querySelectorAll('[role=tab]')[3])).toBe('Messages 1, 1 to read');

    // A value, typed as the query takes it, checked with it; what is wrong marked till the text is checked again.
    await typeValue('min', ' 50 ');
    expect(await check(validationOf({ parameters: [{ ...minimum, given: true }] }))).toEqual({
      text: 'shop.orders.where(totl > $min)',
      parameters: [{ name: 'min', type: 'decimal', value: '50' }],
    });
    expect(monaco.last.model.markers).toEqual([]);
    await type('shop.orders.where(total > $min)');
    expect(monaco.last.model.markers).toEqual([]);
    await check(validationOf({ parameters: [minimum] }));

    // What is wrong in a text isn't marked in another, before it is checked.
    await type('shop.orders.where(totl > $min)');
    await check(
      validationOf({ success: false, diagnostics: [diagnosticOf(18, 22)], parameters: [minimum] }),
    );
    expect(monaco.last.model.markers.length).toBe(1);
    await type('shop.orders.where(totl  > $min)');
    expect(monaco.last.model.markers).toEqual([]);
    await check(validationOf({ parameters: [minimum] }));

    // An empty text is checked by no one: nothing is said of it, nor asked.
    await type('');
    expect(field('min')).toBeNull();
    expect(textOf(page.querySelectorAll('[role=tab]')[3])).toBe('Messages');
  });

  it("doesn't run with a value that isn't of the parameter's type: says so at its field", async () => {
    const { type, check, typeValue, field, press, page } = await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    await typeValue('min', 'abc');
    // Not sent to be checked either (the server would refuse it, not the query).
    expect(await check(validationOf({ parameters: [minimum] }))).toEqual({
      text: query,
      parameters: [],
    });
    expect(field('min')?.getAttribute('aria-invalid')).toBe('true');
    expect(textOf(page.querySelector('mat-hint'))).toBe("abc isn't a number");
    await press('Run');
    expect(document.activeElement).toBe(field('min'));
    expect(page.querySelector('gd-query-results')).toBeNull();
  });

  it('runs the query with its values, shows its rows, and says so in its address', async () => {
    const { type, check, typeValue, press, run, page, address, router, shown, editor, field } =
      await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    await typeValue('min', '50');
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    expect(address()).toBe(`/query#${JSON.stringify({ text: query, values: { min: '50' } })}`);

    const replaced = vi.spyOn(router, 'navigateByUrl');
    await press('Run');
    expect(await run()).toEqual({
      text: query,
      parameters: [{ name: 'min', type: 'decimal', value: '50' }],
      grid: { offset: 0, limit: 3 },
      includeSchema: true,
      includeCount: true,
    });
    expect(textOf(page.querySelector('gd-query-results .count'))).toBe('2 rows in 13 ms');
    expect(address()).toBe(
      `/query#${JSON.stringify({ text: query, values: { min: '50' }, run: true })}`,
    );
    expect(replaced.mock.calls.at(-1)?.[1]).toEqual({ replaceUrl: true });

    // Ctrl+Enter, in the editor or elsewhere on the page, runs it again.
    editor().submit();
    await shown();
    await run();
    field('min')!.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Enter',
        ctrlKey: true,
        bubbles: true,
        cancelable: true,
      }),
    );
    await shown();
    await run();

    // Changed, its rows aren't the text's: the address says so.
    await type(`${query}.take(1)`);
    await check(validationOf({ parameters: [minimum] }));
    expect(address()).toBe(
      `/query#${JSON.stringify({ text: `${query}.take(1)`, values: { min: '50' } })}`,
    );
  });

  it('shows the query its address holds, and its rows when they were shown', async () => {
    const at = urlOf({ id: null, text: query, values: { min: '50' }, run: true });
    const { check, run, page } = await opened(at);
    expect(monaco.last.model.getValue()).toBe(query);
    // Run once it is checked: its values typed as the query takes them.
    expect(
      page.querySelector('mat-progress-bar[aria-label="Checking the query before it runs"]'),
    ).not.toBeNull();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    expect(await run()).toMatchObject({
      text: query,
      parameters: [{ name: 'min', type: 'decimal', value: '50' }],
    });
    expect(textOf(page.querySelector('gd-query-results .count'))).toBe('2 rows in 13 ms');
  });

  it("says what of its address it couldn't read", async () => {
    const { page, address } = await opened('/query#not-json');
    expect(alertsOf(page)).toBe('');
    expect(textOf(page.querySelector('gd-message[kind=warning]'))).toContain(
      "Part of the address couldn't be read, and was left out.",
    );
    expect(address()).toBe('/query#not-json');
  });

  it("goes to a group's rows as a query, and back to the query it came from", async () => {
    const { type, check, press, run, page, address, http, shown, router } = await opened();
    await type('shop.orders.groupBy(status).select(status, n: count())');
    await check();
    await press('Run');
    await run();
    const before = address();

    // The link's cell, followed: where it leads is a query of the group's rows.
    const results = page.querySelector('gd-query-results')!;
    results.querySelector<HTMLElement>('[col-id=c1] .gd-link')!.click();
    const link = await requestTo(http, linkUrl, 'POST');
    link.flush({
      queryText: '(shop.orders).where(status == $key1)',
      parameters: [{ name: 'key1', type: 'string', value: 'open' }],
      browse: null,
      key: null,
    });
    await shown();
    expect(address()).toBe(
      `/query#${JSON.stringify({ text: '(shop.orders).where(status == $key1)', values: { key1: 'open' }, run: true })}`,
    );
    await check();
    await run();
    // The grid the link was in is gone: the keyboard is in the editor, which holds the query of the rows.
    expect(document.activeElement).toBe(monaco.last.input);

    await router.navigateByUrl(before);
    await shown();
    expect(monaco.last.model.getValue()).toBe(
      'shop.orders.groupBy(status).select(status, n: count())',
    );
    await check();
    await run();
  });

  it("opens a saved query: its text and values, its name the page's title, edits kept in its address", async () => {
    const { read, check, page, address, type, field } = await opened('/query/7');
    expect(textOf(page.querySelector('.subtitle'))).toBe('Reading the saved query…');
    expect(monaco.last.options['readOnly']).toBe(true);
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    expect(textOf(page.querySelector('h1'))).toBe('Big orders');
    expect(textOf(page.querySelector('.subtitle'))).toBe('Yours');
    expect(TestBed.inject(Title).getTitle()).toBe('Big orders · Query · GalaxyData');
    expect(monaco.last.model.getValue()).toBe('shop.orders.where(total > $min)');
    expect(monaco.last.options['readOnly']).toBe(false);
    expect(field('min')?.value).toBe('50');
    expect(address()).toBe('/query/7');

    await type('shop.orders.where(total >= $min)');
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    expect(textOf(page.querySelector('h1'))).toBe('Big orders (edited)');
    expect(address()).toBe(
      `/query/7#${JSON.stringify({ text: 'shop.orders.where(total >= $min)' })}`,
    );
  });

  it("keeps a saved query's edits from its address", async () => {
    const at = urlOf({ id: 7, text: 'shop.orders', values: null, run: false });
    const { read, check, page } = await opened(at);
    await read();
    await check();
    expect(monaco.last.model.getValue()).toBe('shop.orders');
    expect(textOf(page.querySelector('h1'))).toBe('Big orders (edited)');
  });

  it("says when a saved query can't be read", async () => {
    const { http, page, shown, check } = await opened(
      urlOf({ id: 7, text: 'shop.orders', values: null, run: false }),
    );
    (await requestTo(http, `${savedUrl}/7`)).flush(
      problemBody('not-found', 'There is no such query'),
      {
        status: 404,
        statusText: 'Not found',
      },
    );
    await shown();
    await check();
    expect(alertsOf(page)).toContain('There is no saved query 7 you can see (any more)');
    expect(monaco.last.model.getValue()).toBe('shop.orders');
    expect(textOf(page.querySelector('h1'))).toBe('New query');
  });

  it("asks again for a saved query that couldn't be read", async () => {
    const { http, page, shown, read, check } = await opened('/query/7');
    (await requestTo(http, `${savedUrl}/7`)).flush(null, {
      status: 502,
      statusText: 'Bad gateway',
    });
    await shown();
    expect(alertsOf(page)).toContain("Couldn't read the saved query");
    clickButton(page, 'Try again');
    await read();
    await check();
    expect(textOf(page.querySelector('h1'))).toBe('Big orders');
    expect(alertsOf(page)).toBe('');
  });

  it('saves a new query, named in a dialog', async () => {
    const { type, check, press, root, http, shown, address, page } = await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    await press('Save');
    const dialog = document.querySelector('gd-save-query-dialog')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Save the query');
    const [name] = dialog.querySelectorAll<HTMLInputElement>('input');
    name.value = '  Big orders ';
    name.dispatchEvent(new Event('input'));
    await shown();
    clickButton(dialog, 'Save');
    const taken = await requestTo(http, savedUrl, 'POST');
    expect(taken.request.body).toEqual({
      name: 'Big orders',
      description: null,
      isShared: false,
      text: query,
      parameters: [],
    });
    taken.flush(
      problemBody('query-name-taken', 'The name is taken', {
        detail: 'There is a query Big orders already',
      }),
      {
        status: 409,
        statusText: 'Conflict',
      },
    );
    await shown();
    expect(textOf(dialog.querySelector('mat-error'))).toBe('There is a query Big orders already');

    // Saved under another name.
    name.value = 'Big orders 2';
    name.dispatchEvent(new Event('input'));
    await shown();
    clickButton(dialog, 'Save');
    (await requestTo(http, savedUrl, 'POST')).flush(
      savedQueryOf({ id: 9, text: query, parameters: [] }),
    );
    await shown();

    expect(document.querySelector('gd-save-query-dialog')).toBeNull();
    expect(address()).toBe('/query/9');
    expect(textOf(page.querySelector('h1'))).toBe('Big orders');
    expect(textOf(page.querySelector('.notice'))).toBe('Big orders is saved');
    expect(root).toBeTruthy();
  });

  it('saves a saved query in place, to the version read; a conflict offers to read it again or save a copy', async () => {
    const { read, check, type, http, shown, page, press } = await opened('/query/7');
    await read();
    await check();
    await type('shop.orders');
    await check();
    page.dispatchEvent(
      new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true, cancelable: true }),
    );
    await shown();
    const put = await requestTo(http, `${savedUrl}/7`, 'PUT');
    expect(put.request.body).toEqual({
      query: {
        name: 'Big orders',
        description: null,
        isShared: false,
        text: 'shop.orders',
        // The values of the parameters it uses: none.
        parameters: [],
      },
      version: 3,
    });
    put.flush(problemBody('concurrency-conflict', 'Big orders was changed since it was read'), {
      status: 409,
      statusText: 'Conflict',
    });
    await shown();
    expect(alertsOf(page)).toContain(
      "Couldn't save Big orders: Big orders was changed since it was read.",
    );
    await press('Read it again');
    await read(savedQueryOf({ version: 4, text: 'shop.orders.take(5)' }));
    // The editor keeps its text: saved now, it is saved to the version read.
    expect(monaco.last.model.getValue()).toBe('shop.orders');
    expect(textOf(page.querySelector('h1'))).toBe('Big orders (edited)');
    await press('Save');
    const again = await requestTo(http, `${savedUrl}/7`, 'PUT');
    expect(again.request.body).toMatchObject({ version: 4 });
    again.flush(savedQueryOf({ version: 5, text: 'shop.orders', parameters: [] }));
    await shown();
    expect(textOf(page.querySelector('h1'))).toBe('Big orders');
  });

  it("saves a copy, and changes a saved query's details", async () => {
    const { read, check, http, shown, page, press, address, type } = await opened('/query/7');
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    // Edits aren't saved with the details.
    await type('shop.orders');
    await check();
    await press('more_vert');
    document.querySelector<HTMLElement>('[mat-menu-item]:nth-of-type(3)')!.click();
    await shown();
    let dialog = document.querySelector('gd-save-query-dialog')!;
    expect(textOf(dialog.querySelector('h2'))).toBe("Big orders's details");
    const [name, description] = [
      dialog.querySelector<HTMLInputElement>('input')!,
      dialog.querySelector<HTMLTextAreaElement>('textarea')!,
    ];
    expect(name.value).toBe('Big orders');
    description.value = 'Orders over a minimum';
    description.dispatchEvent(new Event('input'));
    dialog.querySelector<HTMLInputElement>('mat-checkbox input')!.click();
    await shown();
    clickButton(dialog, 'Save the details');
    const put = await requestTo(http, `${savedUrl}/7`, 'PUT');
    expect(put.request.body).toEqual({
      query: {
        name: 'Big orders',
        description: 'Orders over a minimum',
        isShared: true,
        text: 'shop.orders.where(total > $min)',
        parameters: [{ name: 'min', type: 'decimal', value: '50' }],
      },
      version: 3,
    });
    put.flush(savedQueryOf({ version: 4, description: 'Orders over a minimum', isShared: true }));
    await shown();
    expect(textOf(page.querySelector('.subtitle'))).toBe(
      'Yours, shared with everyone · Orders over a minimum',
    );
    expect(textOf(page.querySelector('h1'))).toBe('Big orders (edited)');
    await type('shop.orders.where(total > $min)');
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));

    await press('more_vert');
    document.querySelector<HTMLElement>('[mat-menu-item]:nth-of-type(2)')!.click();
    await shown();
    dialog = document.querySelector('gd-save-query-dialog')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Save a copy');
    expect(dialog.querySelector<HTMLInputElement>('input')!.value).toBe('Big orders (copy)');
    clickButton(dialog, 'Save');
    const post = await requestTo(http, savedUrl, 'POST');
    expect(post.request.body).toMatchObject({ name: 'Big orders (copy)', isShared: false });
    post.flush(savedQueryOf({ id: 8, name: 'Big orders (copy)' }));
    await shown();
    expect(address()).toBe('/query/8');
  });

  it('deletes a saved query, once confirmed: the editor keeps its text', async () => {
    const { read, check, http, shown, page, press, address } = await opened('/query/7');
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    await press('more_vert');
    [...document.querySelectorAll<HTMLElement>('[mat-menu-item]')]
      .find((item) => textOf(item).endsWith('Delete…'))!
      .click();
    await shown();
    const confirm = document.querySelector('gd-confirm-dialog')!;
    expect(textOf(confirm.querySelector('h2'))).toBe('Delete Big orders?');
    clickButton(confirm, 'Delete');
    const deleted = await requestTo(http, `${savedUrl}/7?version=3`, 'DELETE');
    deleted.flush(null, { status: 204, statusText: 'No content' });
    await shown();
    expect(textOf(page.querySelector('h1'))).toBe('New query');
    expect(textOf(page.querySelector('.notice'))).toBe('Big orders is deleted');
    expect(document.activeElement).toBe(monaco.last.input);
    expect(address()).toBe(
      `/query#${JSON.stringify({ text: 'shop.orders.where(total > $min)', values: { min: '50' } })}`,
    );
  });

  it('opens a saved query chosen in a dialog', async () => {
    const { http, shown, press, read, check, address } = await opened();
    await press('Open…');
    const list = await requestTo(http, savedUrl);
    list.flush([
      summaryOf(savedQueryOf()),
      summaryOf(
        savedQueryOf({ id: 9, name: 'Shared totals', owner: 'bob', isMine: false, isShared: true }),
      ),
    ]);
    await shown();
    const dialog = document.querySelector('gd-open-query-dialog')!;
    expect([...dialog.querySelectorAll('h3')].map((heading) => textOf(heading))).toEqual([
      'Yours',
      "Other people's",
    ]);
    const find = dialog.querySelector<HTMLInputElement>('input')!;
    find.value = 'bob';
    find.dispatchEvent(new Event('input'));
    await shown();
    expect([...dialog.querySelectorAll('a.query')].map((link) => textOf(link))).toEqual([
      'Shared totals',
    ]);
    dialog.querySelector<HTMLElement>('a.query')!.click();
    await shown();
    await read(
      savedQueryOf({ id: 9, name: 'Shared totals', owner: 'bob', isMine: false, isShared: true }),
    );
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    expect(address()).toBe('/query/9');
    expect(document.querySelector('gd-open-query-dialog')).toBeNull();
  });

  it('copies a link to the query: a shared saved query by its address, else its text', async () => {
    const written: string[] = [];
    const clipboard = { writeText: (text: string) => (written.push(text), Promise.resolve()) };
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: clipboard });
    try {
      const { read, check, press, shown, page, type } = await opened('/query/7');
      await read(savedQueryOf({ isShared: true }));
      await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
      const copy = async () => {
        await press('more_vert');
        [...document.querySelectorAll<HTMLElement>('[mat-menu-item]')]
          .find((item) => textOf(item).endsWith('Copy a link to the query'))!
          .click();
        await shown();
      };
      await copy();
      expect(written.at(-1)).toBe(`${location.origin}/query/7`);
      expect(textOf(page.querySelector('.notice'))).toBe('A link to the query is copied');
      await type('shop.orders');
      await check();
      await copy();
      expect(decodeURIComponent(written.at(-1)!)).toBe(
        `${location.origin}/query#${JSON.stringify({ text: 'shop.orders' })}`,
      );

      clipboard.writeText = () => Promise.reject(new Error('Not allowed'));
      await copy();
      expect(textOf(page.querySelector('.notice'))).toContain('Copy this link to the query: http');
    } finally {
      Reflect.deleteProperty(navigator, 'clipboard');
    }
  });

  it('starts a new query, and shows the one left when it comes back to the editor', async () => {
    const { type, check, press, shown, router, address, page } = await opened();
    await type(query);
    await check();
    const left = address();
    await press('more_vert');
    document.querySelector<HTMLElement>('[mat-menu-item]')!.click();
    await shown();
    expect(address()).toBe('/query');
    expect(monaco.last.model.getValue()).toBe('');
    expect(document.activeElement).toBe(monaco.last.input);

    await router.navigateByUrl(left);
    await shown();
    await check();
    expect(monaco.last.model.getValue()).toBe(query);
    await router.navigateByUrl('/browse/shop.orders');
    await shown();
    expect(TestBed.inject(LastQuery).url).toBe(router.serializeUrl(router.parseUrl(left)));
    await router.navigateByUrl('/query');
    await shown();
    await check();
    expect(address()).toBe(left);
    expect(textOf(page.querySelector('h1'))).toBe('New query');
  });

  it("explains the query: its plan and its SQL, and again with the optimizer's phases", async () => {
    const { type, check, press, http, shown, page } = await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    await press('Explain');
    const explain = await requestTo(http, explainUrl, 'POST');
    expect(explain.request.body).toEqual({ text: query, parameters: [], verbose: false });
    explain.flush(explainOf());
    await shown();
    expect(page.querySelector('[role=tab][aria-selected=true]')?.textContent?.trim()).toBe('Plan');
    expect(textOf(page.querySelector('gd-query-plan .summary'))).toBe(
      'Runs as one SQLite query in shop, reading shop.orders.',
    );
    page.querySelector<HTMLInputElement>('.mat-mdc-tab-body-active mat-checkbox input')!.click();
    await shown();
    const verbose = await requestTo(http, explainUrl, 'POST');
    expect(verbose.request.body).toMatchObject({ verbose: true });
    verbose.flush(explainOf({ phases: [{ name: 'lowered', rules: [], plan: 'Scan' }] }));
    await shown();
    expect(textOf(page.querySelector('gd-query-plan h3'))).toBe("The optimizer's phases");

    await type(`${query}.take(1)`);
    await check(validationOf({ parameters: [minimum] }));
    expect(textOf(page.querySelector('.mat-mdc-tab-body-active gd-message'))).toContain(
      "Explained before the query's last changes.",
    );
  });

  it('goes to what the messages say is wrong', async () => {
    const { type, check, page, shown } = await opened();
    await type('shop.orders\n.where(totl > 1)');
    await check(validationOf({ success: false, diagnostics: [diagnosticOf(19, 23)] }));
    page.querySelectorAll<HTMLElement>('[role=tab]')[3].click();
    await shown();
    clickButton(page.querySelector('gd-query-messages')!, 'Line 2, column 8');
    expect(monaco.last.position).toEqual({ lineNumber: 2, column: 8 });
  });

  it("runs a group's rows with their values typed as the query takes them", async () => {
    const at = urlOf({
      id: null,
      text: '(shop.customers).where(postcode == $key1)',
      values: { key1: '0123' },
      run: true,
    });
    const { check, run, address } = await opened(at);
    expect(JSON.parse(address().slice(address().indexOf('#') + 1))).toMatchObject({ run: true });
    await check(validationOf({ parameters: [{ name: 'key1', given: true, type: 'string(10)' }] }));
    expect(await run()).toMatchObject({
      parameters: [{ name: 'key1', type: 'string', value: '0123' }],
    });
  });

  it('runs a plain text area with Ctrl+Enter once, and keeps Ctrl+Enter in the grid its own', async () => {
    TestBed.overrideProvider(MonacoLoader, {
      useValue: { load: () => Promise.reject(new Error('Offline')) },
    });
    const { page, check, run, shown, http } = await opened();
    const area = page.querySelector<HTMLTextAreaElement>('textarea.plain')!;
    area.value = query;
    area.dispatchEvent(new Event('input'));
    await shown();
    await check(validationOf({ parameters: [minimum] }));
    const pressed = (target: Element, init: KeyboardEventInit) =>
      target.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true, ...init }),
      );
    pressed(area, { ctrlKey: true });
    await shown();
    await run();
    // Ctrl+Enter in the grid is the grid's (on a header, its filter); with Shift, nothing of the page's.
    pressed(page.querySelector('.ag-cell')!, { ctrlKey: true });
    pressed(area, { ctrlKey: true, shiftKey: true });
    await shown();
    expect(http.match(() => true)).toEqual([]);
  });

  it("saves with Cmd+S on macOS, and doesn't with Ctrl+Shift+S", async () => {
    const platform = vi.spyOn(navigator, 'platform', 'get').mockReturnValue('MacIntel');
    try {
      const { type, check, page, shown, http } = await opened();
      await type(query);
      await check(validationOf({ parameters: [minimum] }));
      const key = (init: KeyboardEventInit) =>
        page.dispatchEvent(
          new KeyboardEvent('keydown', { key: 's', bubbles: true, cancelable: true, ...init }),
        );
      key({ ctrlKey: true });
      key({ metaKey: true, shiftKey: true });
      await shown();
      expect(document.querySelector('gd-save-query-dialog')).toBeNull();
      key({ metaKey: true });
      await shown();
      expect(document.querySelector('gd-save-query-dialog')).not.toBeNull();
      expect(http.match(() => true)).toEqual([]);
    } finally {
      platform.mockRestore();
    }
  });

  it('stops a run: the address no longer says its rows are shown, and the keyboard goes to Run', async () => {
    const { type, check, press, http, address, page } = await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    await press('Run');
    const running = await requestTo(http, executeUrl, 'POST');
    expect(address()).toContain('"run":true');
    await press('Stop');
    expect(running.cancelled).toBe(true);
    expect(address()).toBe(`/query#${JSON.stringify({ text: query })}`);
    expect(document.activeElement).toBe(
      [...page.querySelectorAll('button')].find((button) => textOf(button).endsWith('Run')),
    );
    expect(textOf(page.querySelector('gd-query-results'))).toContain(
      'Stopped before its rows came.',
    );
  });

  it("doesn't save a value that isn't of its parameter's type", async () => {
    const { type, check, typeValue, press, http, field } = await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    await typeValue('min', 'abc');
    await check(validationOf({ parameters: [minimum] }));
    await press('Save');
    expect(document.querySelector('gd-save-query-dialog')).toBeNull();
    expect(document.activeElement).toBe(field('min'));
    expect(http.match(() => true)).toEqual([]);
  });

  it('shows nothing of the query before while a saved one is read, and does nothing with it', async () => {
    const { type, check, router, shown, page, read, http } = await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    await router.navigateByUrl('/query/7');
    await shown();
    expect(monaco.last.model.getValue()).toBe('');
    expect(monaco.last.options['readOnly']).toBe(true);
    const run = [...page.querySelectorAll('button')].find((button) =>
      textOf(button).endsWith('Run'),
    );
    expect(run?.disabled || run?.getAttribute('aria-disabled')).toBeTruthy();
    page.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true }));
    page.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, bubbles: true }),
    );
    await shown();
    expect(document.querySelector('gd-save-query-dialog')).toBeNull();
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    expect(http.match(() => true)).toEqual([]);
  });

  it('leaves what is answered for a query no longer shown: a read, a save, a delete', async () => {
    const { http, shown, router, read, check, page, type } = await opened('/query/7');
    // Read 7 answered after 5 is shown: 7 is let go.
    const seven = await requestTo(http, `${savedUrl}/7`);
    await router.navigateByUrl('/query/5');
    await shown();
    seven.flush(savedQueryOf());
    await shown();
    expect(textOf(page.querySelector('h1'))).toBe('Query');
    await read(savedQueryOf({ id: 5, name: 'Five', text: 'shop.orders' }));
    await check();
    expect(textOf(page.querySelector('h1'))).toBe('Five');

    // Saved, then 7 shown before the save is answered: 7 stays as read.
    await type('shop.orders.take(5)');
    await check();
    page.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true }));
    await shown();
    const put = await requestTo(http, `${savedUrl}/5`, 'PUT');
    await router.navigateByUrl('/query/7');
    await shown();
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    put.flush(savedQueryOf({ id: 5, name: 'Five', text: 'shop.orders.take(5)', version: 4 }));
    await shown();
    expect(textOf(page.querySelector('h1'))).toBe('Big orders');
    expect(textOf(page.querySelector('.notice'))).toBe('');
  });

  it('lets an explanation asked for before the last go', async () => {
    const { type, check, press, http, shown, page } = await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    await press('Explain');
    (await requestTo(http, explainUrl, 'POST')).flush(explainOf());
    await shown();
    const verbose = () =>
      page.querySelector<HTMLInputElement>('.mat-mdc-tab-body-active mat-checkbox input')!;
    verbose().click();
    await shown();
    const first = await requestTo(http, explainUrl, 'POST');
    verbose().click();
    await shown();
    const second = await requestTo(http, explainUrl, 'POST');
    expect([first.request.body, second.request.body]).toMatchObject([
      { verbose: true },
      { verbose: false },
    ]);
    second.flush(explainOf({ summary: 'The second' }));
    await shown();
    first.flush(explainOf({ summary: 'The first' }));
    await shown();
    expect(textOf(page.querySelector('gd-query-plan .summary'))).toBe('The second');
  });

  it("says a query deleted elsewhere isn't saved when it is read again", async () => {
    const { read, check, type, http, shown, page, press } = await opened('/query/7');
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    await type('shop.orders');
    await check();
    page.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true }));
    await shown();
    (await requestTo(http, `${savedUrl}/7`, 'PUT')).flush(
      problemBody('forbidden', "Big orders is bob's"),
      { status: 403, statusText: 'Forbidden' },
    );
    await shown();
    // Not the user's to change: saved as a copy, or read again.
    expect(alertsOf(page)).toContain('Save a copy');
    await press('Read it again');
    (await requestTo(http, `${savedUrl}/7`)).flush(
      problemBody('not-found', 'There is no such query'),
      {
        status: 404,
        statusText: 'Not found',
      },
    );
    await shown();
    expect(alertsOf(page)).toContain(
      "There is no saved query 7 you can see (any more): the editor's query isn't saved.",
    );
    expect(textOf(page.querySelector('h1'))).toBe('New query');
    expect(monaco.last.model.getValue()).toBe('shop.orders');
  });

  it('writes its address before the page is left, so going back shows the query as left', async () => {
    TestBed.overrideProvider(QUERY_WAITS, { useValue: { check: 1, write: 60_000 } });
    const { type, check, router, shown, address } = await opened();
    router.setUpLocationChangeListener();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    expect(address()).toBe('/query');
    await router.navigateByUrl('/browse/shop.orders');
    await shown();
    TestBed.inject(Location).back();
    await shown(6);
    expect(address()).toBe(`/query#${JSON.stringify({ text: query })}`);
    await check(validationOf({ parameters: [minimum] }));
    expect(monaco.last.model.getValue()).toBe(query);
  });

  it('shows an empty query going back to one, not the one left', async () => {
    const { router, shown, read, check, page, http } = await opened();
    router.setUpLocationChangeListener();
    await router.navigateByUrl('/query/7');
    await shown();
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    TestBed.inject(Location).back();
    await shown(6);
    expect(router.url).toBe('/query');
    expect(textOf(page.querySelector('h1'))).toBe('New query');
    expect(monaco.last.model.getValue()).toBe('');
    expect(http.match(() => true)).toEqual([]);
  });

  it('keeps following the editor when Query is chosen in the navigation while it is open', async () => {
    const { type, check, router, shown, address } = await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    await router.navigateByUrl('/query');
    await shown();
    expect(address()).toBe(`/query#${JSON.stringify({ text: query })}`);
    await type(`${query}.take(2)`);
    await check(validationOf({ parameters: [minimum] }));
    expect(address()).toBe(`/query#${JSON.stringify({ text: `${query}.take(2)` })}`);
  });

  it('says nothing of the query before as another is shown', async () => {
    const { read, check, type, http, shown, page, router } = await opened('/query/7');
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    await type('shop.orders');
    await check();
    page.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true }));
    await shown();
    (await requestTo(http, `${savedUrl}/7`, 'PUT')).flush(
      problemBody('concurrency-conflict', 'Big orders was changed since it was read'),
      { status: 409, statusText: 'Conflict' },
    );
    await shown();
    expect(alertsOf(page)).toContain("Couldn't save Big orders");
    await router.navigateByUrl('/query', { state: newQueryState });
    await shown();
    expect(alertsOf(page)).toBe('');
  });

  it("saves another's query as a copy, and a value that isn't of its type not at all", async () => {
    const { read, check, page, shown, http, typeValue, router } = await opened('/query/9');
    await read(
      savedQueryOf({ id: 9, owner: 'bob', isMine: false, isShared: true, canEdit: false }),
    );
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    page.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true }));
    await shown();
    const dialog = document.querySelector('gd-save-query-dialog')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Save a copy');
    clickButton(dialog, 'Cancel');
    await shown();

    await router.navigateByUrl('/query/7');
    await shown();
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    await typeValue('min', 'abc');
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    page.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true }));
    await shown();
    expect(http.match(() => true)).toEqual([]);
    expect(document.querySelector('gd-save-query-dialog')).toBeNull();
  });

  it('shows the saved query it holds as saved, going back to it, without reading it again', async () => {
    const { read, check, type, router, shown, http, address } = await opened('/query/7');
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    await type('shop.orders');
    await check();
    expect(address()).toBe(`/query/7#${JSON.stringify({ text: 'shop.orders' })}`);
    await router.navigateByUrl('/query/7');
    await shown();
    expect(http.match((request) => request.url === `${savedUrl}/7`)).toEqual([]);
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    expect(monaco.last.model.getValue()).toBe('shop.orders.where(total > $min)');
  });

  it('runs a query whose check failed, its values typed as the command line types them', async () => {
    const { type, press, http, shown, run } = await opened();
    await type(query);
    await press('Run');
    (await requestTo(http, validateUrl, 'POST')).flush(null, {
      status: 502,
      statusText: 'Bad gateway',
    });
    await shown();
    expect(await run()).toMatchObject({ text: query, parameters: [] });
  });

  it('shows the query left again though the page was left as it showed it', async () => {
    const { type, check, router, shown, address, http } = await opened();
    await type(query);
    await check(validationOf({ parameters: [minimum] }));
    const left = address();
    await router.navigateByUrl('/browse/shop.orders');
    await shown();
    // Back to the editor, and away before it shows the query left.
    void router.navigateByUrl('/query');
    await settle(1);
    await router.navigateByUrl('/browse/shop.customers');
    await shown();
    await router.navigateByUrl('/query');
    await shown();
    for (const request of http.match((each) => each.url === validateUrl)) {
      request.flush(validationOf({ parameters: [minimum] }));
    }
    await shown();
    expect(address()).toBe(left);
  });

  it('lets go of its title as it is left', async () => {
    const { read, check, router, shown } = await opened('/query/7');
    await read();
    await check(validationOf({ parameters: [{ ...minimum, given: true }] }));
    expect(TestBed.inject(Title).getTitle()).toBe('Big orders · Query · GalaxyData');
    await router.navigateByUrl('/browse/shop.orders');
    await shown();
    expect(TestBed.inject(Title).getTitle()).toBe('GalaxyData');
  });

  it('counts the errors and warnings of the messages, not what is only said', async () => {
    const { type, check, page } = await opened();
    await type('shop.orders.select(x: -$n)');
    await check(
      validationOf({
        complete: false,
        diagnostics: [
          diagnosticOf(21, 23, '$n has no value', { severity: 'info' }),
          diagnosticOf(0, 4, 'Hidden', { severity: 'warning', code: 'GDQ2101' }),
        ],
        parameters: [{ name: 'n', given: false, type: null }],
      }),
    );
    const tab = page.querySelectorAll('[role=tab]')[3];
    expect(textOf(tab)).toBe('Messages 1, 1 to read');
    expect(tab.querySelector('.badge')?.classList).not.toContain('errors');
  });
});

/** An address of the editor, as the router writes it. */
function urlOf(address: QueryAddress): string {
  return TestBed.inject(Router).serializeUrl(queryUrlTree(address));
}
