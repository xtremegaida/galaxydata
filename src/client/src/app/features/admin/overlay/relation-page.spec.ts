import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Router, TitleStrategy } from '@angular/router';
import { CatalogVersion, catalogVersionHeader } from '../../../core/catalog/catalog-version';
import { PageTitles } from '../../../core/page-titles';
import { problemBody } from '../../../../testing/auth';
import { requestTo } from '../../../../testing/http';
import {
  answerLookups,
  checkOf,
  columnsNamed,
  issueOf,
  overlayOf,
  overlayPage,
  overlayUrl,
  relationOf,
  relationsUrl,
  validateUrl,
  virtualEntityOf,
} from '../../../../testing/overlay';
import {
  alertsOf,
  clickButton,
  openPage,
  pageProviders,
  textOf,
  wordsOf,
} from '../../../../testing/pages';
import { adminRoutes } from '../admin.routes';
import { OVERLAY_WAITS } from './overlay-items';
import { entityOf, type EntityDto } from '../../../../testing/catalog';

const whOrders = entityOf({
  name: 'wh.orders',
  qualifiedName: 'wh.main.orders',
  source: 'wh',
  columns: columnsNamed('id', 'customer_id', 'status'),
});
const shopCustomers = entityOf({
  name: 'shop.customers',
  qualifiedName: 'shop.main.customers',
  columns: columnsNamed('id', 'name', 'city'),
  key: { name: null, columns: ['id'], isDeclared: false },
});
const entities = { 'wh.orders': whOrders, 'shop.customers': shopCustomers };

/** What a relation sends: wh.orders (customer_id) to shop.customers (id), unless said otherwise. */
function inputOf(changes: Record<string, unknown> = {}) {
  return {
    from: 'wh.orders',
    fromColumns: ['customer_id'],
    to: 'shop.customers',
    toColumns: ['id'],
    name: null,
    inverseName: null,
    description: null,
    ...changes,
  };
}

describe('RelationPage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'admin', children: adminRoutes }]),
        { provide: OVERLAY_WAITS, useValue: { check: 1, lookup: 1 } },
        { provide: TitleStrategy, useClass: PageTitles },
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

  /** A relation's page, read (and tried) when it is a relation's. */
  async function opened(
    relation = relationOf(),
    url = `/admin/overlay/relations/${relation.id}`,
    known: Readonly<Record<string, EntityDto>> = entities,
  ) {
    const page = overlayPage(await openPage(url));
    await page.shown();
    if (!url.endsWith('/new')) {
      (await requestTo(page.http, `${relationsUrl}/${relation.id}`)).flush(relation);
      await page.shown();
      await answerLookups(page.http, known);
      await page.shown();
    }
    return page;
  }

  it('makes a relation: entities and columns suggested, tried as it is written', async () => {
    const page = await opened(undefined, '/admin/overlay/relations/new');
    expect(textOf(page.page.querySelector('h1'))).toBe('New relation');
    expect(wordsOf(page.page.querySelector('gd-overlay-check'))).toContain(
      'Name the entities it relates, and it is tried as you go.',
    );

    // The entities, chosen from what the catalog finds.
    await page.typeIn(page.field('From'), 'orders');
    await answerLookups(page.http, entities, { orders: ['shop.orders', 'wh.orders'] });
    await page.shown();
    expect(page.options()).toEqual(['shop.orders', 'wh.orders']);
    await page.choose('wh.orders');
    await answerLookups(page.http, entities);
    await page.shown();
    expect(page.field('From').value).toBe('wh.orders');
    await page.typeIn(page.field('To'), 'shop.customers');
    await answerLookups(page.http, entities, { 'shop.customers': ['shop.customers'] });
    await page.shown();
    await page.closePanels();

    // Their columns: the key of the entity it leads to, met on asking; the other's suggested.
    await page.press('Meet its key: id');
    expect(page.fields('Meets').map((input) => input.value)).toEqual(['id']);
    expect(page.buttons().some((text) => text.startsWith('Meet its key'))).toBe(false);
    await page.typeIn(page.fields('Column')[0], 'cust');
    expect(page.options()).toEqual(['customer_id']);
    await page.choose('customer_id');

    // Tried once it can be, as it is: the navigations it makes.
    const check = await requestTo(page.http, validateUrl(relationsUrl), 'POST');
    expect(check.request.body).toEqual(inputOf());
    check.flush(checkOf({ navigations: { forward: 'customer', inverse: 'wh_orders' } }));
    await page.shown();
    const made = page.page.querySelector('gd-overlay-check')!;
    expect(wordsOf(made.querySelector('[role=status]'))).toBe('It works.');
    expect(wordsOf(made.querySelector('section > p'))).toBe(
      'Rows of wh.orders lead to those they refer to through customer, and rows of shop.customers back to them through wh_orders.',
    );
    expect(textOf(page.hintOf("Navigation's name"))).toBe(
      'From the rows that refer to those they refer to. As the convention names it: customer',
    );
    // Named: the convention's name isn't said.
    await page.typeIn(page.field("Navigation's name"), 'buyer');
    expect(textOf(page.hintOf("Navigation's name"))).toBe(
      'From the rows that refer to those they refer to.',
    );
    await page.typeIn(page.field("Navigation's name"), '');
    // (Tried again as the text comes back: the same is said.)
    for (const again of page.http.match((request) => request.url.endsWith('/validate'))) {
      if (!again.cancelled) {
        again.flush(checkOf({ navigations: { forward: 'customer', inverse: 'wh_orders' } }));
      }
    }
    await page.shown();
    // What is shown was tried with the relation as it is: nothing is being tried.
    expect(page.page.querySelector('gd-overlay-check mat-progress-bar')).toBeNull();

    await page.press('Save');
    const saved = await requestTo(page.http, relationsUrl, 'POST');
    expect(saved.request.body).toEqual(inputOf());
    saved.flush(relationOf());
    await page.shown();
    expect(TestBed.inject(Router).url).toBe('/admin/overlay/relations/3');
    (await requestTo(page.http, `${relationsUrl}/3`)).flush(relationOf());
    await page.shown();
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    expect(textOf(page.page.querySelector('h1'))).toBe(
      'wh.orders (customer_id) → shop.customers (id)',
    );
    expect(TestBed.inject(Title).getTitle()).toBe(
      'wh.orders (customer_id) → shop.customers (id) · Relation · GalaxyData',
    );
    expect(textOf(document.querySelector('.mat-mdc-snack-bar-label'))).toBe('Saved. Close');
  });

  it('edits a relation to the version read, tried in its place', async () => {
    const page = await opened(relationOf({ description: 'Orders kept in the warehouse' }));
    // Tried at once, in place of itself.
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    expect(page.isDisabled('Save')).toBe(true);
    expect(page.isDisabled('Undo the changes')).toBe(true);

    await page.typeIn(page.field("Navigation's name"), ' buyer ');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    await page.press('Save');
    const saved = await requestTo(page.http, `${relationsUrl}/3`, 'PUT');
    expect(saved.request.body).toEqual({
      relation: inputOf({ name: 'buyer', description: 'Orders kept in the warehouse' }),
      version: 2,
    });
    saved.flush(
      relationOf({ name: 'buyer', version: 3, description: 'Orders kept in the warehouse' }),
    );
    await page.shown();
    // Saved: tried again, as the catalog is built anew.
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    expect(page.field("Navigation's name").value).toBe('buyer');
    expect(page.isDisabled('Save')).toBe(true);

    // Undone, as read.
    await page.typeIn(page.field('Description'), '');
    const edited = await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST');
    expect(edited.request.body).toEqual(inputOf({ name: 'buyer' }));
    await page.press('Undo the changes');
    expect(page.field('Description').value).toBe('Orders kept in the warehouse');
    // What was being tried is let go: the relation as saved is tried again.
    expect(edited.cancelled).toBe(true);
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
  });

  it('says when someone else changed the relation, and reads it again', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.typeIn(page.field("Navigation's name"), 'buyer');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.press('Save');
    (await requestTo(page.http, `${relationsUrl}/3`, 'PUT')).flush(
      problemBody('concurrency-conflict', 'The relation was changed since it was read', {
        detail: 'Read it again, and make the change again',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await page.shown();
    expect(alertsOf(page.page)).toContain(
      'The relation was changed since it was read. Read it again, and make the change again.',
    );
    clickButton(page.page, 'Read it again');
    await page.shown();
    // The change made here would be lost: the page asks first.
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Read it again?');
    clickButton(dialog, 'Read it again');
    await page.shown();
    (await requestTo(page.http, `${relationsUrl}/3`)).flush(
      relationOf({ name: 'client', version: 4 }),
    );
    await page.shown();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    expect(page.field("Navigation's name").value).toBe('client');
    expect(alertsOf(page.page)).toBe('');
  });

  it('puts what the server refuses on its fields, and the rest above them', async () => {
    const page = await opened(relationOf({ fromColumns: ['a', 'b'], toColumns: ['x', 'y'] }));
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.typeIn(page.field('Description'), 'Two columns');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.press('Save');
    (await requestTo(page.http, `${relationsUrl}/3`, 'PUT')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: {
          'relation.toColumns[1]': ["A column's name has 200 characters at most"],
          'relation.version': ['Give the version read'],
        },
      }),
      { status: 400, statusText: 'Bad request' },
    );
    await page.shown();
    expect(textOf(page.errorOf(page.fields('Meets')[1]))).toBe(
      "A column's name has 200 characters at most",
    );
    expect(alertsOf(page.page)).toContain('Give the version read');
    expect(document.activeElement).toBe(page.fields('Meets')[1]);
  });

  it('says when the relation is there already', async () => {
    const page = await opened(undefined, '/admin/overlay/relations/new');
    await page.typeIn(page.field('From'), 'wh.orders');
    await page.typeIn(page.field('To'), 'shop.customers');
    await page.typeIn(page.fields('Column')[0], 'customer_id');
    await page.typeIn(page.fields('Meets')[0], 'id');
    await answerLookups(page.http, entities);
    await page.closePanels();
    (await requestTo(page.http, validateUrl(relationsUrl), 'POST')).flush(checkOf());
    await page.press('Save');
    (await requestTo(page.http, relationsUrl, 'POST')).flush(
      problemBody('overlay-item-exists', 'The relation is there already', {
        detail: 'The overlay has the relation wh.orders(customer_id) -> shop.customers(id) already',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await page.shown();
    expect(alertsOf(page.page)).toContain(
      'The relation is there already. The overlay has the relation wh.orders(customer_id) -> shop.customers(id) already.',
    );
    expect(TestBed.inject(Router).url).toBe('/admin/overlay/relations/new');
  });

  it("checks what it can before trying or saving: entities, and each pair's columns", async () => {
    const page = await opened(undefined, '/admin/overlay/relations/new');
    await page.press('Add a column');
    expect(page.fields('Column')).toHaveLength(2);
    await page.press('Save');
    expect(textOf(page.errorOf(page.field('From')))).toBe(
      'Name the entity whose rows refer to the others',
    );
    expect(textOf(page.errorOf(page.fields('Meets')[1]))).toBe('Name the column it meets');
    expect(document.activeElement).toBe(page.field('From'));
    await page.typeIn(page.field('From'), 'wh.orders');
    await page.typeIn(page.field('To'), 'shop.customers');
    await answerLookups(page.http, entities);
    await page.closePanels();
    expect(wordsOf(page.page.querySelector('gd-overlay-check'))).toContain(
      'Name each column, and the column it meets, and it is tried as you go.',
    );
    // A column of spaces is no name; a pair without the column it meets isn't tried.
    await page.typeIn(page.fields('Column')[0], '   ');
    await page.typeIn(page.fields('Column')[1], 'customer_id');
    await page.press('Save');
    expect(textOf(page.errorOf(page.fields('Column')[0]))).toBe('Name the column');
    await page.typeIn(page.fields('Column')[0], 'customer_id');
    await page.typeIn(page.fields('Meets')[0], 'id');
    await page.closePanels();
    expect(wordsOf(page.page.querySelector('gd-overlay-check'))).toContain(
      'Name each column, and the column it meets, and it is tried as you go.',
    );
    // A pair removed: the remove buttons go when one is left.
    clickButton(page.page, 'remove_circle_outline');
    await page.shown();
    expect(page.fields('Column')).toHaveLength(1);
    expect(page.page.querySelector('[aria-label^="Remove columns"]')).toBeNull();
    expect(page.http.match((request) => request.url.endsWith('/validate'))).toEqual([]);
  });

  it('says what the catalog finds wrong, and what else it would break', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(
      checkOf({
        issues: [
          issueOf("There is no column 'customer_id' in wh.orders"),
          issueOf("The name 'customer' is taken: wh_customer it is", 'warning', 'GDQ5010'),
        ],
        breaks: [
          {
            kind: 'virtualEntity',
            id: 5,
            issues: [issueOf("There is no 'customer' here")],
          },
        ],
      }),
    );
    await page.shown();
    // The broken items are named as the overlay names them.
    (await requestTo(page.http, overlayUrl)).flush(
      overlayOf({ virtualEntities: [virtualEntityOf()] }),
    );
    await page.shown();
    const check = page.page.querySelector('gd-overlay-check')!;
    expect(wordsOf(check.querySelector('.verdict'))).toBe(
      "It doesn't work as it is: the catalog leaves it out, or the part of it at fault.",
    );
    expect([...check.querySelectorAll('.issue')].map((issue) => wordsOf(issue))).toEqual([
      "Error: There is no column 'customer_id' in wh.orders GDQ5001",
      "Warning: The name 'customer' is taken: wh_customer it is GDQ5010",
      "Error: There is no 'customer' here GDQ5001",
    ]);
    const broken = check.querySelector<HTMLAnchorElement>('.breaks a')!;
    expect(textOf(broken)).toBe('The virtual entity reports.big_orders');
    expect(broken.getAttribute('href')).toBe('/admin/overlay/virtual-entities/5');
  });

  it("says when it couldn't be tried, and tries it again", async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(null, {
      status: 502,
      statusText: 'Bad gateway',
    });
    await page.shown();
    expect(wordsOf(page.page.querySelector('gd-overlay-check [role=status]'))).toBe(
      "It couldn't be tried: Can't reach the server. Check the connection, and try again. Try again",
    );
    clickButton(page.page.querySelector('gd-overlay-check')!, 'Try again');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    expect(wordsOf(page.page.querySelector('gd-overlay-check [role=status]'))).toBe('It works.');
  });

  it('tries it again as the catalog changes', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf(), {
      headers: { [catalogVersionHeader]: 'v1' },
    });
    await page.shown();
    TestBed.inject(CatalogVersion).seen('v2');
    await page.shown();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(
      checkOf({ issues: [issueOf("The name 'customer' is taken", 'warning', 'GDQ5010')] }),
    );
    // Its entities are looked up again too.
    await answerLookups(page.http, entities);
    await page.shown();
    expect(wordsOf(page.page.querySelector('gd-overlay-check [role=status]'))).toBe(
      'It works, with warnings.',
    );
    expect(wordsOf(page.page.querySelector('gd-overlay-check .issues'))).toBe(
      "Warning: The name 'customer' is taken GDQ5010",
    );
  });

  it('deletes a relation once sure', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    clickButton(page.page, 'delete Delete');
    await page.shown();
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe(
      'Delete the relation wh.orders (customer_id) → shop.customers (id)?',
    );
    expect(textOf(dialog.querySelector('mat-dialog-content'))).toBe(
      'Its navigations go from the catalog, and queries that use them stop working.',
    );
    clickButton(dialog, 'Delete');
    await page.shown();
    (await requestTo(page.http, `${relationsUrl}/3?version=2`, 'DELETE')).flush(null);
    await page.shown();
    (await requestTo(page.http, overlayUrl)).flush(overlayOf());
    await page.shown();
    expect(TestBed.inject(Router).url).toBe('/admin/overlay');
    expect(TestBed.inject(Title).getTitle()).toBe('Overlay · GalaxyData');
  });

  it('asks before leaving changes not saved', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.typeIn(page.field('Description'), 'Changed');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    const left = TestBed.inject(Router).navigateByUrl('/admin/overlay');
    await page.shown();
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Leave without saving?');
    clickButton(dialog, 'Stay');
    expect(await left).toBe(false);
    expect(TestBed.inject(Router).url).toBe('/admin/overlay/relations/3');
  });

  it("says when the relation can't be read, and reads it again", async () => {
    const page = overlayPage(await openPage('/admin/overlay/relations/9'));
    await page.shown();
    (await requestTo(page.http, `${relationsUrl}/9`)).flush(
      problemBody('not-found', 'There is no such relation', {
        detail: 'The overlay has no relation 9',
      }),
      { status: 404, statusText: 'Not found' },
    );
    await page.shown();
    expect(alertsOf(page.page)).toBe(
      'error There is no such relation. The overlay has no relation 9. Try again',
    );
    expect(page.page.querySelector('form')).toBeNull();
    clickButton(page.page, 'Try again');
    (await requestTo(page.http, `${relationsUrl}/9`)).flush(relationOf({ id: 9 }));
    await page.shown();
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(relationsUrl, 9), 'POST')).flush(checkOf());
    await page.shown();
    expect(page.page.querySelector('form')).not.toBeNull();
  });

  it('shows another relation in place of one: what was said of it goes, and answers for it are left aside', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.typeIn(page.field("Navigation's name"), 'buyer');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.press('Save');
    (await requestTo(page.http, `${relationsUrl}/3`, 'PUT')).flush(
      problemBody('concurrency-conflict', 'The relation was changed since it was read'),
      { status: 409, statusText: 'Conflict' },
    );
    await page.shown();
    expect(alertsOf(page.page)).toContain('The relation was changed since it was read.');

    // Saved again: what is edited can't be, and nothing else done to it, till it is answered.
    await page.press('Save');
    const saving = await requestTo(page.http, `${relationsUrl}/3`, 'PUT');
    expect(page.field("Navigation's name").readOnly).toBe(true);
    expect(page.isDisabled('Delete')).toBe(true);
    expect(page.isDisabled('Add a column')).toBe(true);

    // A link to another (one it would break), left without saving.
    const router = TestBed.inject(Router);
    const went = router.navigateByUrl('/admin/overlay/relations/7');
    await page.shown();
    clickButton(document.querySelector('mat-dialog-container')!, 'Leave');
    expect(await went).toBe(true);
    (await requestTo(page.http, `${relationsUrl}/7`)).flush(
      relationOf({ id: 7, name: 'client', version: 5 }),
    );
    await page.shown();
    expect(alertsOf(page.page)).toBe('');
    // The first's answer, come now, changes nothing.
    saving.flush(relationOf({ name: 'buyer', version: 3 }));
    await page.shown();
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(relationsUrl, 7), 'POST')).flush(checkOf());
    await page.shown();
    expect(router.url).toBe('/admin/overlay/relations/7');
    expect(page.field("Navigation's name").value).toBe('client');
    expect(page.field("Navigation's name").readOnly).toBe(false);
    expect(document.querySelector('.mat-mdc-snack-bar-label')).toBeNull();

    await page.typeIn(page.field("Navigation's name"), 'customer');
    (await requestTo(page.http, validateUrl(relationsUrl, 7), 'POST')).flush(checkOf());
    await page.press('Save');
    const saved = await requestTo(page.http, `${relationsUrl}/7`, 'PUT');
    expect(saved.request.body).toMatchObject({ relation: { name: 'customer' }, version: 5 });
    saved.flush(relationOf({ id: 7, name: 'customer', version: 6 }));
    await page.shown();
    (await requestTo(page.http, validateUrl(relationsUrl, 7), 'POST')).flush(checkOf());
  });

  it("leaves aside a delete's answer once another relation is shown", async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    clickButton(page.page, 'delete Delete');
    await page.shown();
    clickButton(document.querySelector('mat-dialog-container')!, 'Delete');
    await page.shown();
    const deleting = await requestTo(page.http, `${relationsUrl}/3?version=2`, 'DELETE');
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/admin/overlay/relations/7');
    (await requestTo(page.http, `${relationsUrl}/7`)).flush(relationOf({ id: 7 }));
    await page.shown();
    deleting.flush(null);
    await page.shown();
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(relationsUrl, 7), 'POST')).flush(checkOf());
    await page.shown();
    expect(router.url).toBe('/admin/overlay/relations/7');
    expect(page.isDisabled('Delete')).toBe(false);
  });

  it('deletes a relation with changes not saved without asking to leave them', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.typeIn(page.field('Description'), 'Changed');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    clickButton(page.page, 'delete Delete');
    await page.shown();
    clickButton(document.querySelector('mat-dialog-container')!, 'Delete');
    await page.shown();
    (await requestTo(page.http, `${relationsUrl}/3?version=2`, 'DELETE')).flush(null);
    await page.shown();
    (await requestTo(page.http, overlayUrl)).flush(overlayOf());
    await page.shown();
    expect(TestBed.inject(Router).url).toBe('/admin/overlay');
    expect(document.querySelector('mat-dialog-container')).toBeNull();
  });

  it('puts what is wrong with the columns as a whole on the first pair', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.typeIn(page.field('Description'), 'Changed');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.press('Save');
    (await requestTo(page.http, `${relationsUrl}/3`, 'PUT')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: { 'relation.toColumns': ['A relation has as many columns on each side'] },
      }),
      { status: 400, statusText: 'Bad request' },
    );
    await page.shown();
    expect(textOf(page.errorOf(page.fields('Meets')[0]))).toBe(
      'A relation has as many columns on each side',
    );
    expect(alertsOf(page.page)).toBe('');
  });

  it("reads no relation for an address that isn't one's", async () => {
    const page = overlayPage(await openPage('/admin/overlay/relations/1e2'));
    await page.shown();
    expect(alertsOf(page.page)).toBe(
      "error There is no such relation. '1e2' isn't the number of one. Try again",
    );
    expect(page.page.querySelector('form')).toBeNull();
  });

  it("says why it can't be tried, of the relation as it was tried only", async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(
      checkOf({ navigations: { forward: 'customer', inverse: 'wh_orders' } }),
    );
    await page.shown();
    expect(textOf(page.hintOf("Navigation's name"))).toContain(
      'As the convention names it: customer',
    );
    await page.typeIn(page.field('From'), 'wh.');
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: { from: ["'wh.' isn't an entity path, as queries write them"] },
      }),
      { status: 400, statusText: 'Bad request' },
    );
    await page.shown();
    const status = () => wordsOf(page.page.querySelector('gd-overlay-check [role=status]'));
    expect(status()).toBe(
      "It can't be tried as it is: 'wh.' isn't an entity path, as queries write them",
    );
    // Trying it again would fail alike: not offered. Nor are names from what was tried before.
    expect(page.page.querySelector('gd-overlay-check button')).toBeNull();
    expect(textOf(page.hintOf("Navigation's name"))).toBe(
      'From the rows that refer to those they refer to.',
    );

    // What was said goes with the text it was said of.
    await page.typeIn(page.field('From'), 'wh.orders');
    // (As it was tried first: what that gave is said of it again.)
    expect(status()).toBe('It works.');
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    expect(status()).toBe('It works.');
  });

  it('tries a relation as read at once, and as edited once editing pauses', async () => {
    TestBed.overrideProvider(OVERLAY_WAITS, { useValue: { check: 60_000, lookup: 1 } });
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    await page.typeIn(page.field('Description'), 'Changed');
    await page.shown(5);
    expect(page.http.match((request) => request.url.endsWith('/validate'))).toEqual([]);
    // What was tried before stays said meanwhile, as it is tried again.
    expect(wordsOf(page.page.querySelector('gd-overlay-check [role=status]'))).toBe('It works.');
    expect(page.page.querySelector('gd-overlay-check mat-progress-bar')).not.toBeNull();
  });

  it('keeps the keyboard where it was as columns are added, removed and met', async () => {
    const page = await opened(relationOf({ toColumns: ['code'] }));
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.press('Add a column');
    expect(document.activeElement).toBe(page.fields('Column')[1]);
    await page.typeIn(page.fields('Column')[1], 'second');
    await page.press('Add a column');
    await page.typeIn(page.fields('Column')[2], 'third');
    // Removed: the pair in its place takes the keyboard, or the one before for the last.
    await page.press('Remove column pair 2');
    expect(page.fields('Column').map((input) => input.value)).toEqual(['customer_id', 'third']);
    expect(document.activeElement).toBe(page.fields('Column')[1]);
    await page.press('Remove column pair 2');
    expect(document.activeElement).toBe(page.fields('Column')[0]);
    expect(page.page.querySelector('[aria-label^="Remove column pair"]')).toBeNull();

    // Its key met: the first column to name takes it.
    await page.press('Meet its key: id');
    expect(page.fields('Meets').map((input) => input.value)).toEqual(['id']);
    expect(document.activeElement).toBe(page.fields('Column')[0]);
    // A pair more: its key isn't what they meet any more.
    await page.press('Add a column');
    expect(page.buttons()).toContain('Meet its key: id');
    await page.press('Remove column pair 2');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
  });

  it('gives the keyboard to the heading as the relation is read again, and as it is tried again', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(null, {
      status: 502,
      statusText: 'Bad gateway',
    });
    await page.shown();
    clickButton(page.page.querySelector('gd-overlay-check')!, 'Try again');
    await page.shown();
    expect(document.activeElement).toBe(page.page.querySelector('#gd-overlay-check'));
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());

    await page.typeIn(page.field("Navigation's name"), 'buyer');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.press('Save');
    (await requestTo(page.http, `${relationsUrl}/3`, 'PUT')).flush(
      problemBody('concurrency-conflict', 'The relation was changed since it was read'),
      { status: 409, statusText: 'Conflict' },
    );
    await page.shown();
    clickButton(page.page, 'Read it again');
    await page.shown();
    clickButton(document.querySelector('mat-dialog-container')!, 'Read it again');
    await page.shown();
    expect(document.activeElement).toBe(page.page.querySelector('h1'));
    (await requestTo(page.http, `${relationsUrl}/3`)).flush(relationOf({ version: 3 }));
    await page.shown();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
  });

  it("closes a field's suggestions as the keyboard goes to it, wrong, so they don't cover what is wrong", async () => {
    const page = await opened(undefined, '/admin/overlay/relations/new');
    await page.typeIn(page.field('From'), 'wh.orders');
    await answerLookups(page.http, entities, { 'wh.orders': ['wh.orders'] });
    await page.shown();
    expect(page.options()).toEqual(['wh.orders']);
    await page.typeIn(page.field('To'), 'shop.customers');
    await page.typeIn(page.fields('Column')[0], 'customer_id');
    await page.typeIn(page.fields('Meets')[0], 'id');
    await answerLookups(page.http, entities, { 'wh.orders': ['wh.orders'] });
    await page.closePanels();
    (await requestTo(page.http, validateUrl(relationsUrl), 'POST')).flush(checkOf());
    await page.press('Save');
    (await requestTo(page.http, relationsUrl, 'POST')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: { from: ['wh.orders is read-only'] },
      }),
      { status: 400, statusText: 'Bad request' },
    );
    await page.shown();
    expect(document.activeElement).toBe(page.field('From'));
    expect(textOf(page.errorOf(page.field('From')))).toBe('wh.orders is read-only');
    expect(page.options()).toEqual([]);
  });

  it("names the items it would break by kind and number when the overlay can't be read", async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(
      checkOf({
        issues: [issueOf('Broken')],
        breaks: [{ kind: 'entitySettings', id: 6, issues: [issueOf('Its key is gone')] }],
      }),
    );
    await page.shown();
    (await requestTo(page.http, overlayUrl)).flush(null, { status: 500, statusText: 'Oops' });
    await page.shown();
    expect(textOf(page.page.querySelector('gd-overlay-check .breaks a'))).toBe(
      'The entity settings 6',
    );
  });

  it('gives the keyboard to the first column to name as a key of two columns is met', async () => {
    const tenants = entityOf({
      name: 'shop.tenants',
      columns: columnsNamed('tenant', 'id', 'name'),
      key: { name: null, columns: ['tenant', 'id'], isDeclared: false },
    });
    const page = await opened(relationOf({ to: 'shop.tenants', toColumns: ['name'] }), undefined, {
      ...entities,
      'shop.tenants': tenants,
    });
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.shown();
    await page.press('Meet its key: tenant, id');
    expect(page.fields('Column').map((input) => input.value)).toEqual(['customer_id', '']);
    expect(page.fields('Meets').map((input) => input.value)).toEqual(['tenant', 'id']);
    expect(document.activeElement).toBe(page.fields('Column')[1]);
  });

  it('tries nothing while the relation can’t be tried, the catalog changing or not', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf(), {
      headers: { [catalogVersionHeader]: 'v1' },
    });
    await page.typeIn(page.field('To'), '');
    expect(wordsOf(page.page.querySelector('gd-overlay-check'))).toContain(
      'Name the entities it relates, and it is tried as you go.',
    );
    TestBed.inject(CatalogVersion).seen('v2');
    await page.shown();
    await answerLookups(page.http, entities);
    expect(page.http.match((request) => request.url.endsWith('/validate'))).toEqual([]);
  });

  it("doesn't say why the relation couldn't be tried of another text, as that is waited for", async () => {
    TestBed.overrideProvider(OVERLAY_WAITS, { useValue: { check: 60_000, lookup: 1 } });
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(null, {
      status: 502,
      statusText: 'Bad gateway',
    });
    await page.shown();
    const status = () => wordsOf(page.page.querySelector('gd-overlay-check [role=status]'));
    expect(status()).toContain("It couldn't be tried");
    await page.typeIn(page.field('Description'), 'Changed');
    expect(status()).toBe('');
    expect(page.page.querySelector('gd-overlay-check mat-progress-bar')).not.toBeNull();
  });

  it('says nothing of the relation before once another is shown', async () => {
    const page = await opened();
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.typeIn(page.field('Description'), 'Changed');
    (await requestTo(page.http, validateUrl(relationsUrl, 3), 'POST')).flush(checkOf());
    await page.press('Save');
    (await requestTo(page.http, `${relationsUrl}/3`, 'PUT')).flush(
      problemBody('concurrency-conflict', 'The relation was changed since it was read'),
      { status: 409, statusText: 'Conflict' },
    );
    await page.shown();
    expect(alertsOf(page.page)).toContain('The relation was changed since it was read.');
    const went = TestBed.inject(Router).navigateByUrl('/admin/overlay/relations/7');
    await page.shown();
    clickButton(document.querySelector('mat-dialog-container')!, 'Leave');
    expect(await went).toBe(true);
    (await requestTo(page.http, `${relationsUrl}/7`)).flush(relationOf({ id: 7 }));
    await page.shown();
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(relationsUrl, 7), 'POST')).flush(checkOf());
    await page.shown();
    expect(alertsOf(page.page)).toBe('');
  });

  it('starts from the entity a link gives, which leaving keeps as it was', async () => {
    const page = overlayPage(await openPage('/admin/overlay/relations/new?from=wh.orders'));
    await page.shown();
    await answerLookups(page.http, entities, { 'wh.orders': ['wh.orders'] });
    await page.shown();
    await page.closePanels();
    expect(page.field('From').value).toBe('wh.orders');
    expect(page.field('To').value).toBe('');
    expect(wordsOf(page.page.querySelector('gd-overlay-check'))).toContain(
      'Name the entities it relates, and it is tried as you go.',
    );

    expect(await TestBed.inject(Router).navigateByUrl('/admin/overlay')).toBe(true);
    (await requestTo(page.http, overlayUrl)).flush(overlayOf());
  });
});
