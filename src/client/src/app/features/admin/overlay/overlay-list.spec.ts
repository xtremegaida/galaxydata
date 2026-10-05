import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatSlideToggleHarness } from '@angular/material/slide-toggle/testing';
import { CatalogVersion, catalogVersionHeader } from '../../../core/catalog/catalog-version';
import { requestTo, settle } from '../../../../testing/http';
import {
  issueOf,
  overlayOf,
  overlayUrl,
  overrideOf,
  relationOf,
  settingsOf,
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

/** Each table's rows, as text: a list of cells' text for each row. */
function rowsOf(section: Element | null): string[][] {
  return [...(section?.querySelectorAll('tbody tr') ?? [])].map((row) =>
    [...row.querySelectorAll('td')].map((cell) => wordsOf(cell)),
  );
}

describe('OverlayList', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: pageProviders([{ path: 'admin', children: adminRoutes }]),
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function open(overlay = overlayOf()) {
    const opened = await openPage('/admin/overlay');
    (await requestTo(opened.http, overlayUrl)).flush(overlay, {
      headers: { [catalogVersionHeader]: 'v1' },
    });
    await settle();
    opened.harness.detectChanges();
    const section = (title: string) =>
      [...opened.page.querySelectorAll('section')].find(
        (each) => textOf(each.querySelector('h2')) === title,
      ) ?? null;
    return { ...opened, section };
  }

  it('lists the items of each kind, with what the catalog finds wrong with them', async () => {
    const { page, section } = await open(
      overlayOf({
        relations: [
          relationOf({ description: 'Orders kept in the warehouse' }),
          relationOf({
            id: 7,
            from: 'wh.orders',
            fromColumns: ['shipper_id'],
            to: 'shop.shippers',
            navigations: null,
            issues: [issueOf("There is no column 'shipper_id' in wh.orders")],
          }),
        ],
        virtualEntities: [
          virtualEntityOf(),
          virtualEntityOf({ id: 8, name: 'reports.spend', key: ['customer_id'] }),
        ],
        entitySettings: [settingsOf()],
        navigations: [
          overrideOf({
            issues: [issueOf("The name 'buyer' is taken", 'warning', 'GDQ5010')],
            renameTo: 'buyer',
            hidden: false,
          }),
        ],
        errors: 1,
        warnings: 1,
      }),
    );
    expect(textOf(page.querySelector('h1'))).toBe('Overlay');
    expect(wordsOf(page.querySelector('[role=status]'))).toBe(
      "1 item doesn't work as the catalog is now: it leaves out what is at fault, and 1 has warnings.",
    );
    expect(rowsOf(section('Relations'))).toEqual([
      [
        // The description is a line of its own, under the link.
        'wh.orders (customer_id) → shop.customers (id)Orders kept in the warehouse',
        'customer, back wh_orders',
        'None',
      ],
      [
        'wh.orders (shipper_id) → shop.shippers (id)',
        'None made',
        "Error: There is no column 'shipper_id' in wh.orders GDQ5001",
      ],
    ]);
    expect(rowsOf(section('Virtual entities'))).toEqual([
      ['reports.big_orders', "Its query's", 'None'],
      ['reports.spend', 'customer_id', 'None'],
    ]);
    expect(rowsOf(section('Entity settings'))).toEqual([
      ['shop.customers', "Shown by name; 1 column's settings", 'None'],
    ]);
    expect(rowsOf(section('Navigation overrides'))).toEqual([
      ['shop.orders.bill_address', 'Renamed buyer', "Warning: The name 'buyer' is taken GDQ5010"],
    ]);
    // Each leads to its page.
    const links = [...page.querySelectorAll<HTMLAnchorElement>('tbody a')].map((link) =>
      link.getAttribute('href'),
    );
    expect(links).toEqual([
      '/admin/overlay/relations/3',
      '/admin/overlay/relations/7',
      '/admin/overlay/virtual-entities/5',
      '/admin/overlay/virtual-entities/8',
      '/admin/overlay/entity-settings/6',
      '/admin/overlay/navigations/4',
    ]);
    expect(page.querySelector('a[download]')?.getAttribute('href')).toBe('/api/overlay/export');
  });

  it('lists only the items with issues, on asking', async () => {
    const { loader, section } = await open(
      overlayOf({
        relations: [relationOf(), relationOf({ id: 7, issues: [issueOf('Broken')] })],
        virtualEntities: [virtualEntityOf()],
        errors: 1,
      }),
    );
    await (await loader.getHarness(MatSlideToggleHarness)).check();
    expect(rowsOf(section('Relations')).map((row) => row[2])).toEqual(['Error: Broken GDQ5001']);
    expect(textOf(section('Virtual entities')?.querySelector('p'))).toBe('None with issues.');
    await (await loader.getHarness(MatSlideToggleHarness)).uncheck();
    expect(rowsOf(section('Relations'))).toHaveLength(2);
    expect(rowsOf(section('Virtual entities'))).toHaveLength(1);
  });

  it('keeps the items shown when reading them again fails', async () => {
    const { page, harness, http } = await open(overlayOf({ relations: [relationOf()] }));
    TestBed.inject(CatalogVersion).seen('v2');
    await settle();
    harness.detectChanges();
    (await requestTo(http, overlayUrl)).flush(null, { status: 502, statusText: 'Bad gateway' });
    await settle();
    harness.detectChanges();
    expect(alertsOf(page)).toContain('Try again');
    expect(page.querySelectorAll('tbody tr')).toHaveLength(1);
  });

  it('says when every item works, with warnings, and when there are none', async () => {
    const { page, harness, http } = await open(
      overlayOf({
        navigations: [overrideOf({ issues: [issueOf('Taken', 'warning')] }), overrideOf({ id: 9 })],
        warnings: 2,
      }),
    );
    expect(wordsOf(page.querySelector('[role=status]'))).toBe('Every item works; 2 have warnings.');

    // The catalog changed (a schema read again elsewhere): the overlay is read again.
    TestBed.inject(CatalogVersion).seen('v2');
    await settle();
    harness.detectChanges();
    (await requestTo(http, overlayUrl)).flush(overlayOf());
    await settle();
    harness.detectChanges();
    expect(wordsOf(page.querySelector('[role=status]'))).toBe('');
    expect(textOf(page.querySelector('.empty'))).toContain('Nothing yet');
    expect(page.querySelector('section')).toBeNull();
  });

  it('says when the overlay can’t be read, and reads it again', async () => {
    const opened = await openPage('/admin/overlay');
    (await requestTo(opened.http, overlayUrl)).flush(null, {
      status: 502,
      statusText: 'Bad gateway',
    });
    await settle();
    opened.harness.detectChanges();
    expect(alertsOf(opened.page)).toContain('Try again');
    clickButton(opened.page, 'Try again');
    (await requestTo(opened.http, overlayUrl)).flush(overlayOf({ relations: [relationOf()] }));
    await settle();
    opened.harness.detectChanges();
    expect(alertsOf(opened.page)).toBe('');
    expect(opened.page.querySelectorAll('tbody tr')).toHaveLength(1);
  });

  it('offers new items of each kind', async () => {
    const { page, harness } = await open();
    clickButton(page, 'add New');
    await settle();
    harness.detectChanges();
    const items = [...document.querySelectorAll<HTMLAnchorElement>('[mat-menu-item]')];
    expect(items.map((item) => [textOf(item), item.getAttribute('href')])).toEqual([
      ['Relation', '/admin/overlay/relations/new'],
      ['Virtual entity', '/admin/overlay/virtual-entities/new'],
      ['Entity settings', '/admin/overlay/entity-settings/new'],
      ['Navigation override', '/admin/overlay/navigations/new'],
    ]);
  });
});
