import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { problemBody } from '../../../../testing/auth';
import { entityOf } from '../../../../testing/catalog';
import { requestTo } from '../../../../testing/http';
import {
  answerLookups,
  checkOf,
  columnsNamed,
  overlayOf,
  overlayPage,
  overlayUrl,
  settingsOf,
  settingsUrl,
  validateUrl,
} from '../../../../testing/overlay';
import { alertsOf, openPage, pageProviders, textOf, wordsOf } from '../../../../testing/pages';
import { adminRoutes } from '../admin.routes';
import { OVERLAY_WAITS } from './overlay-items';

const customers = entityOf({
  name: 'shop.customers',
  qualifiedName: 'shop.main.customers',
  columns: columnsNamed('id', 'name', 'city'),
  key: { name: null, columns: ['id'], isDeclared: false },
  displayColumn: 'name',
});
const entities = { 'shop.customers': customers };

/** What settings send: shop.customers', none set, unless said otherwise. */
function inputOf(changes: Record<string, unknown> = {}) {
  return {
    entity: 'shop.customers',
    key: null,
    displayColumn: null,
    hidden: false,
    columns: [],
    ...changes,
  };
}

describe('EntitySettingsPage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'admin', children: adminRoutes }]),
        { provide: OVERLAY_WAITS, useValue: { check: 1, lookup: 1 } },
      ],
    });
  });

  afterEach(() => {
    try {
      TestBed.inject(HttpTestingController).verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  async function opened(url: string) {
    const page = overlayPage(await openPage(url));
    await page.shown();
    /** The columns' table, a row's cells' text each. */
    const table = () =>
      [...page.page.querySelectorAll('.column-settings tbody tr')].map((row) =>
        [...row.querySelectorAll('th, td')].map((cell) => wordsOf(cell)).filter(Boolean),
      );
    const cell = (label: string) =>
      page.page.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`)!;
    return { ...page, table, cell };
  }

  /** Settings' page, read and tried. */
  async function openedAt(settings = settingsOf()) {
    const page = await opened(`/admin/overlay/entity-settings/${settings.id}`);
    (await requestTo(page.http, `${settingsUrl}/${settings.id}`)).flush(settings);
    await page.shown();
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(settingsUrl, settings.id), 'POST')).flush(checkOf());
    await page.shown();
    return page;
  }

  it("sets an entity's columns in a table of them, sending only what is set", async () => {
    const page = await opened('/admin/overlay/entity-settings/new');
    expect(textOf(page.page.querySelector('h1'))).toBe('New entity settings');
    expect(wordsOf(page.page.querySelector('fieldset.columns .aside'))).toBe(
      'Name the entity to list its columns.',
    );
    await page.typeIn(page.field('Entity'), 'shop.customers');
    await answerLookups(page.http, entities);
    await page.shown();
    (await requestTo(page.http, validateUrl(settingsUrl), 'POST')).flush(checkOf());
    await page.closePanels();
    expect(page.table()).toEqual([
      ['id', 'INTEGER'],
      ['name', 'TEXT'],
      ['city', 'TEXT'],
    ]);
    expect(textOf(page.hintOf('Shown by'))).toBe(
      "The column that shows a row where others refer to it; the convention's when empty (now name)",
    );

    // A label, a type from those suggested, a column hidden.
    await page.typeIn(page.cell('city’s label'), ' Town ');
    await page.typeIn(page.cell('Read id as'), 'int');
    expect(page.options()).toEqual(['int32', 'int64']);
    await page.choose('int32');
    page.cell('Hide name').click();
    await page.shown();
    // The column shown by, from the entity's.
    await page.typeIn(page.field('Shown by'), 'ci');
    expect(page.options()).toEqual(['city']);
    await page.choose('city');
    // Only what is set is sent: a label set and cleared sends nothing.
    await page.typeIn(page.cell('name’s label'), 'Name');
    await page.typeIn(page.cell('name’s label'), '');
    const check = await requestTo(page.http, validateUrl(settingsUrl), 'POST');
    expect(check.request.body).toEqual(
      inputOf({
        displayColumn: 'city',
        columns: [
          { name: 'city', hidden: false, label: 'Town', type: null },
          { name: 'id', hidden: false, label: null, type: 'int32' },
          { name: 'name', hidden: true, label: null, type: null },
        ],
      }),
    );
    check.flush(checkOf({ entity: customers }));
    await page.shown();
    expect(
      [...page.page.querySelectorAll('gd-overlay-check h3')].map((heading) => textOf(heading)),
    ).toEqual(['The entity, with them']);

    await page.press('Save');
    const made = await requestTo(page.http, settingsUrl, 'POST');
    expect(made.request.body).toEqual(check.request.body);
    made.flush(settingsOf());
    await page.shown();
    expect(TestBed.inject(Router).url).toBe('/admin/overlay/entity-settings/6');
    (await requestTo(page.http, `${settingsUrl}/6`)).flush(settingsOf());
    await page.shown();
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(settingsUrl, 6), 'POST')).flush(checkOf());
    await page.shown();
  });

  it("keeps the settings of a column the entity hasn't, said, to drop", async () => {
    const page = await openedAt(
      settingsOf({
        hidden: true,
        key: ['name'],
        columns: [{ name: 'town', hidden: false, label: 'Town', type: 'string' }],
      }),
    );
    expect(page.field('Key column 1').value).toBe('name');
    expect(wordsOf(page.page.querySelector('fieldset.key .aside'))).toContain('Its key now: id.');
    expect(page.table()).toEqual([
      ['id', 'INTEGER'],
      ['name', 'TEXT'],
      ['city', 'TEXT'],
      ['town Not a column of the entity'],
    ]);
    expect(page.cell('town’s label').value).toBe('Town');
    // Emptied, it stays (the keyboard on it), till it is dropped.
    await page.typeIn(page.cell('town’s label'), '');
    await page.typeIn(page.cell('Read town as'), '');
    expect(page.table()).toHaveLength(4);
    expect(document.activeElement).toBe(page.cell('Read town as'));
    // Emptied, they aren't sent.
    const emptied = await requestTo(page.http, validateUrl(settingsUrl, 6), 'POST');
    expect(emptied.request.body).toEqual(
      inputOf({ hidden: true, key: ['name'], displayColumn: 'name' }),
    );
    emptied.flush(checkOf());
    await page.shown();
    await page.press('Drop town’s settings');
    expect(page.table()).toHaveLength(3);
    expect(document.activeElement).toBe(page.field('Another column'));
    // Sent as it was: nothing is tried anew.
    expect(page.http.match((request) => request.url.endsWith('/validate'))).toEqual([]);
  });

  it('names the column a refusal is about, and says on the entity when it has settings already', async () => {
    const page = await openedAt();
    await page.typeIn(page.cell('Read city as'), 'dat');
    // What the server refuses in it is why it can't be tried: said, named, without a Try again that would fail alike.
    (await requestTo(page.http, validateUrl(settingsUrl, 6), 'POST')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: { 'columns[0].type': ["'dat' isn't a type: use int32, int64, ..."] },
      }),
      { status: 400, statusText: 'Bad request' },
    );
    await page.shown();
    expect(wordsOf(page.page.querySelector('gd-overlay-check [role=status]'))).toBe(
      "It can't be tried as it is: city: 'dat' isn't a type: use int32, int64, ...",
    );
    expect(page.page.querySelector('gd-overlay-check gd-entity-structure')).toBeNull();
    await page.closePanels();
    await page.press('Save');
    (await requestTo(page.http, `${settingsUrl}/6`, 'PUT')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: { 'settings.columns[0].type': ["'dat' isn't a type: use int32, int64, ..."] },
      }),
      { status: 400, statusText: 'Bad request' },
    );
    await page.shown();
    expect(alertsOf(page.page)).toContain("city: 'dat' isn't a type: use int32, int64, ...");

    await page.typeIn(page.field('Entity'), 'shop.orders');
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(settingsUrl, 6), 'POST')).flush(checkOf());
    await page.closePanels();
    await page.press('Save');
    (await requestTo(page.http, `${settingsUrl}/6`, 'PUT')).flush(
      problemBody('overlay-item-exists', 'The entity settings is there already', {
        detail: 'shop.orders has settings already: change those',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await page.shown();
    expect(textOf(page.errorOf(page.field('Entity')))).toBe(
      'shop.orders has settings already: change those',
    );
    expect(document.activeElement).toBe(page.field('Entity'));
  });

  it('gives settings to columns the catalog has not listed, as when the entity is not known', async () => {
    const page = await openedAt(
      settingsOf({
        entity: 'xl.budget',
        displayColumn: null,
        columns: [{ name: 'Amount', hidden: false, label: null, type: 'decimal(12,2)' }],
      }),
    );
    // The entity isn't in the catalog: its settings' columns are listed, not said to be gone, and may be dropped.
    expect(page.table()).toEqual([['Amount']]);
    expect(page.cell('Read Amount as').value).toBe('decimal(12,2)');
    expect(page.buttons()).toContain('Drop Amount’s settings');

    await page.typeIn(page.field('Another column'), ' Month ');
    await page.press('Add its settings');
    expect(page.table()).toEqual([['Amount'], ['Month']]);
    expect(page.field('Another column').value).toBe('');
    expect(document.activeElement).toBe(page.cell('Month’s label'));
    await page.typeIn(page.cell('Month’s label'), 'Month of the year');
    const check = await requestTo(page.http, validateUrl(settingsUrl, 6), 'POST');
    expect(check.request.body).toEqual(
      inputOf({
        entity: 'xl.budget',
        columns: [
          { name: 'Amount', hidden: false, label: null, type: 'decimal(12,2)' },
          { name: 'Month', hidden: false, label: 'Month of the year', type: null },
        ],
      }),
    );
    check.flush(checkOf());
    await page.shown();
    // A column listed already is found, not added twice.
    await page.typeIn(page.field('Another column'), 'Amount');
    await page.press('Add its settings');
    expect(page.table()).toHaveLength(2);
    expect(document.activeElement).toBe(page.cell('Amount’s label'));
  });

  it('starts with the entity a link gives, tried as it is, and leaving keeps it as it was', async () => {
    const page = await opened('/admin/overlay/entity-settings/new?entity=shop.customers');
    await answerLookups(page.http, entities, { 'shop.customers': ['shop.customers'] });
    await page.shown();
    await page.closePanels();
    expect(page.field('Entity').value).toBe('shop.customers');
    expect(page.table().map((row) => row[0])).toEqual(['id', 'name', 'city']);
    // Settings that set nothing yet, tried as made: the entity as it is.
    const check = await requestTo(page.http, validateUrl(settingsUrl), 'POST');
    expect(check.request.body).toEqual(inputOf({}));
    check.flush(checkOf({ entity: customers }));
    await page.shown();

    expect(await TestBed.inject(Router).navigateByUrl('/admin/overlay')).toBe(true);
    (await requestTo(page.http, overlayUrl)).flush(overlayOf());
  });
});
