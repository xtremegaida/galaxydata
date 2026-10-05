import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { problemBody } from '../../../../testing/auth';
import { entityOf } from '../../../../testing/catalog';
import { requestTo } from '../../../../testing/http';
import {
  answerLookups,
  checkOf,
  navigationsUrl,
  overlayPage,
  overlayUrl,
  overlayOf,
  overrideOf,
  validateUrl,
} from '../../../../testing/overlay';
import { clickButton, openPage, pageProviders, textOf, wordsOf } from '../../../../testing/pages';
import { adminRoutes } from '../admin.routes';
import { OVERLAY_WAITS } from './overlay-items';

const orders = entityOf();
const entities = { 'shop.orders': orders };

describe('NavigationPage', () => {
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
    return page;
  }

  it("renames a navigation chosen from the entity's, tried as it is written", async () => {
    const page = await opened('/admin/overlay/navigations/new');
    expect(textOf(page.page.querySelector('h1'))).toBe('New navigation override');
    await page.typeIn(page.field('Entity'), 'shop.orders');
    await answerLookups(page.http, entities, { 'shop.orders': ['shop.orders'] });
    await page.shown();
    await page.closePanels();

    // The entity's navigations, those holding what is typed.
    await page.typeIn(page.field('Navigation'), '');
    expect(page.options()).toEqual([
      'customer to shop.customers',
      'order_lines to shop.order_lines',
    ]);
    await page.typeIn(page.field('Navigation'), 'cust');
    expect(page.options()).toEqual(['customer to shop.customers']);
    await page.choose('customer to shop.customers');
    expect(page.field('Navigation').value).toBe('customer');
    expect(wordsOf(page.page.querySelector('gd-overlay-check'))).toContain(
      'Rename the navigation or hide it, and it is tried as you go.',
    );

    await page.typeIn(page.field('Rename it to'), 'buyer');
    const check = await requestTo(page.http, validateUrl(navigationsUrl), 'POST');
    expect(check.request.body).toEqual({
      entity: 'shop.orders',
      navigation: 'customer',
      renameTo: 'buyer',
      hidden: false,
    });
    // The entity's navigations as it makes them.
    check.flush(
      checkOf({
        entity: entityOf({
          navigations: [
            { ...orders.navigations[0], name: 'buyer' },
            { ...orders.navigations[1], hidden: true },
          ],
        }),
      }),
    );
    await page.shown();
    expect(
      [...page.page.querySelectorAll('gd-overlay-check .navigations li')].map((item) =>
        textOf(item),
      ),
    ).toEqual(['buyer to shop.customers', 'order_lines to shop.order_lines (hidden)']);

    // One for the navigation is there already: said on its field.
    await page.press('Save');
    (await requestTo(page.http, navigationsUrl, 'POST')).flush(
      problemBody('overlay-item-exists', 'The navigation override is there already', {
        detail:
          "shop.orders's navigation 'customer' is renamed or hidden already: change that override",
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await page.shown();
    expect(textOf(page.errorOf(page.field('Navigation')))).toBe(
      "shop.orders's navigation 'customer' is renamed or hidden already: change that override",
    );
    expect(document.activeElement).toBe(page.field('Navigation'));
  });

  it('renames a navigation or hides it, and says so first', async () => {
    const page = await opened('/admin/overlay/navigations/new');
    await page.typeIn(page.field('Entity'), 'shop.orders');
    await page.typeIn(page.field('Navigation'), 'customer');
    await answerLookups(page.http, entities);
    await page.closePanels();
    await page.press('Save');
    expect(textOf(page.errorOf(page.field('Rename it to')))).toBe(
      'Rename the navigation, or hide it',
    );
    expect(document.activeElement).toBe(page.field('Rename it to'));

    // Hidden, and named as the convention has it.
    page.page.querySelector<HTMLButtonElement>('mat-slide-toggle button')!.click();
    await page.shown();
    expect(page.errorOf(page.field('Rename it to'))).toBeNull();
    const check = await requestTo(page.http, validateUrl(navigationsUrl), 'POST');
    expect(check.request.body).toEqual({
      entity: 'shop.orders',
      navigation: 'customer',
      renameTo: null,
      hidden: true,
    });
    check.flush(checkOf());
    await page.shown();
    await page.press('Save');
    const made = await requestTo(page.http, navigationsUrl, 'POST');
    expect(made.request.body).toEqual(check.request.body);
    made.flush(overrideOf({ id: 4, navigation: 'customer' }));
    await page.shown();
    expect(TestBed.inject(Router).url).toBe('/admin/overlay/navigations/4');
    (await requestTo(page.http, `${navigationsUrl}/4`)).flush(
      overrideOf({ navigation: 'customer' }),
    );
    await page.shown();
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(navigationsUrl, 4), 'POST')).flush(checkOf());
    await page.shown();
    expect(textOf(page.page.querySelector('h1'))).toBe('shop.orders.customer');
  });

  it('edits an override to the version read, and deletes it once sure', async () => {
    // What an address gives starts a new override only: this one is as it was saved.
    const page = await opened('/admin/overlay/navigations/4?entity=shop.customers&navigation=x');
    (await requestTo(page.http, `${navigationsUrl}/4`)).flush(overrideOf());
    await page.shown();
    await answerLookups(page.http, entities);
    (await requestTo(page.http, validateUrl(navigationsUrl, 4), 'POST')).flush(checkOf());
    await page.shown();
    expect(page.field('Entity').value).toBe('shop.orders');
    expect(page.field('Navigation').value).toBe('bill_address');

    await page.typeIn(page.field('Rename it to'), 'billed_to');
    (await requestTo(page.http, validateUrl(navigationsUrl, 4), 'POST')).flush(checkOf());
    await page.press('Save');
    const saved = await requestTo(page.http, `${navigationsUrl}/4`, 'PUT');
    expect(saved.request.body).toEqual({
      navigation: {
        entity: 'shop.orders',
        navigation: 'bill_address',
        renameTo: 'billed_to',
        hidden: true,
      },
      version: 1,
    });
    saved.flush(overrideOf({ renameTo: 'billed_to', version: 2 }));
    await page.shown();
    (await requestTo(page.http, validateUrl(navigationsUrl, 4), 'POST')).flush(checkOf());
    await page.shown();

    clickButton(page.page, 'delete Delete');
    await page.shown();
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe(
      'Delete the override of shop.orders.bill_address?',
    );
    expect(textOf(dialog.querySelector('mat-dialog-content'))).toBe(
      'The navigation is named, and shown, as the convention has it again.',
    );
    clickButton(dialog, 'Delete');
    await page.shown();
    (await requestTo(page.http, `${navigationsUrl}/4?version=2`, 'DELETE')).flush(null);
    await page.shown();
    (await requestTo(page.http, overlayUrl)).flush(overlayOf());
    await page.shown();
    expect(TestBed.inject(Router).url).toBe('/admin/overlay');
  });

  it('suggests the navigation it renames by the name the convention gives it', async () => {
    const page = await opened('/admin/overlay/navigations/4');
    (await requestTo(page.http, `${navigationsUrl}/4`)).flush(
      overrideOf({ navigation: 'customer', renameTo: 'buyer', hidden: false }),
    );
    await page.shown();
    // The catalog names it as the override does.
    await answerLookups(page.http, {
      'shop.orders': entityOf({
        navigations: [{ ...orders.navigations[0], name: 'buyer' }, orders.navigations[1]],
      }),
    });
    (await requestTo(page.http, validateUrl(navigationsUrl, 4), 'POST')).flush(checkOf());
    await page.shown();
    await page.typeIn(page.field('Navigation'), '');
    expect(page.options()).toEqual([
      'customer to shop.customers',
      'order_lines to shop.order_lines',
    ]);
    await page.typeIn(page.field('Navigation'), 'customer');
    expect(page.options()).toEqual(['customer to shop.customers']);
    await page.closePanels();
    (await requestTo(page.http, validateUrl(navigationsUrl, 4), 'POST')).flush(checkOf());
  });

  it('starts with the entity and navigation a link gives, which leaving keeps as they were', async () => {
    const page = await opened(
      '/admin/overlay/navigations/new?entity=shop.orders&navigation=order_lines',
    );
    await answerLookups(page.http, entities, { 'shop.orders': ['shop.orders'] });
    await page.shown();
    await page.closePanels();
    expect(page.field('Entity').value).toBe('shop.orders');
    expect(page.field('Navigation').value).toBe('order_lines');
    expect(wordsOf(page.page.querySelector('gd-overlay-check'))).toContain(
      'Rename the navigation or hide it, and it is tried as you go.',
    );

    // Nothing was changed: leaving doesn't ask.
    expect(await TestBed.inject(Router).navigateByUrl('/admin/overlay')).toBe(true);
    (await requestTo(page.http, overlayUrl)).flush(overlayOf());
  });

  it('asks before another address lets go of what was typed in one a link gave', async () => {
    const page = await opened(
      '/admin/overlay/navigations/new?entity=shop.orders&navigation=order_lines',
    );
    await answerLookups(page.http, entities, { 'shop.orders': ['shop.orders'] });
    await page.shown();
    await page.closePanels();
    await page.typeIn(page.field('Rename it to'), 'lines');
    await page.closePanels();
    const left = TestBed.inject(Router).navigateByUrl(
      '/admin/overlay/navigations/new?entity=shop.orders&navigation=customer',
    );
    await page.shown();
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Leave without saving?');
    clickButton(dialog, 'Stay');
    expect(await left).toBe(false);
    expect(page.field('Navigation').value).toBe('order_lines');
    expect(page.field('Rename it to').value).toBe('lines');
    page.http.match((request) => request.url.endsWith('/validate'));
  });
});
