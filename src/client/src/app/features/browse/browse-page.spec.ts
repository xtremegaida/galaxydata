import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import {
  answerPage,
  gridCells,
  orderColumns,
  orderRows,
  pageOf,
  pageRequest,
  pagesFetched,
} from '../../../testing/browse';
import {
  answerChildren,
  entityOf,
  searchUrl,
  sourceNode,
  type EntityDto,
} from '../../../testing/catalog';
import { problemBody } from '../../../testing/auth';
import { requestTo, settle } from '../../../testing/http';
import { alertsOf, clickButton, openPage, pageProviders, textOf } from '../../../testing/pages';
import { POLL_INTERVAL } from '../../core/api/poll';
import { keepFocus } from '../../core/browser/keep-focus';
import { CatalogVersion, catalogVersionHeader } from '../../core/catalog/catalog-version';
import { browseRoutes } from './browse.routes';
import { SEARCH_WAIT } from './catalog-panel';
import { describeChanges, describeEntity, throughOf } from './entity-structure';
import { BROWSE_PAGE_SIZE } from './grid/browse-grid';

describe('BrowsePage', () => {
  const wide = new BehaviorSubject<BreakpointState>({ matches: true, breakpoints: {} });

  beforeEach(() => {
    localStorage.clear();
    wide.next({ matches: true, breakpoints: {} });
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'browse', children: browseRoutes }]),
        { provide: BreakpointObserver, useValue: { observe: () => wide } },
        { provide: POLL_INTERVAL, useValue: 1 },
        { provide: SEARCH_WAIT, useValue: 0 },
        { provide: BROWSE_PAGE_SIZE, useValue: 3 },
      ],
    });
  });

  const entityUrl = (name: string) => `/api/catalog/entity?name=${encodeURIComponent(name)}`;
  const trailUrl = '/api/browse/trail';

  /**
   * A path's page opened at `url`, the tree beside it answered (its first entity not found in it), and the entity
   * of the crumb shown read when given.
   */
  async function open(
    url = '/browse/shop.orders',
    entity: EntityDto | null = entityOf(),
    version = 'v1',
  ) {
    const opened = await openPage(url);
    const first = decodeURIComponent(url.slice('/browse/'.length).split(/[;/?]/)[0]);
    await answerChildren(opened.http, null, [sourceNode('shop')]);
    (await requestTo(opened.http, searchUrl(first, 20))).flush({
      text: first,
      hits: [],
      more: false,
    });
    const page = opened.harness.fixture.nativeElement.querySelector(
      'gd-browse-page',
    ) as HTMLElement;
    const shown = async () => {
      for (let turn = 0; turn < 2; turn++) {
        await settle();
        opened.harness.detectChanges();
      }
      await pagesFetched();
      opened.harness.detectChanges();
    };
    if (entity) {
      (await requestTo(opened.http, entityUrl(entity.name))).flush(entity, {
        headers: { [catalogVersionHeader]: version },
      });
      await shown();
    }
    /** Shows what the entity is (the tab beside the rows). */
    const showStructure = async () => {
      [...page.querySelectorAll<HTMLElement>('[role=tab]')]
        .find((tab) => textOf(tab) === 'Structure')!
        .click();
      await shown();
    };
    const facts = () =>
      [...page.querySelectorAll('.facts dt')].map((term) => [
        textOf(term),
        textOf(term.nextElementSibling),
      ]);
    const rowsOf = (table: Element | undefined) =>
      [...(table?.querySelectorAll('tbody tr') ?? [])].map((row) =>
        [...row.children].map((cell) => textOf(cell)),
      );
    const router = TestBed.inject(Router);
    return { ...opened, page, shown, showStructure, facts, rowsOf, router };
  }

  it('says what the entity is, its columns, and the navigations from its rows', async () => {
    const { page, facts, rowsOf, showStructure } = await open();
    expect(textOf(page.querySelector('h1'))).toBe('shop.orders');
    expect(textOf(page.querySelector('.subtitle'))).toBe('A table of shop');
    await showStructure();
    expect(facts()).toEqual([
      ['Full name', 'shop.main.orders'],
      ['Rows', 'About 1,200 rows, as the database estimates'],
      ['Key', 'id'],
      ['Shown by', 'statuswhere other rows refer to its rows'],
      ['Changes', 'You may add, change and delete its rows.'],
    ]);
    const [columns, navigations] = [...page.querySelectorAll('gd-entity-structure table')];
    expect([...columns.querySelectorAll('thead th')].map((th) => textOf(th))).toEqual([
      'Name',
      'Type',
      'Properties',
      'Changes',
    ]);
    expect(rowsOf(columns)).toEqual([
      [
        'keyKey:id',
        'int64INTEGER',
        'identity',
        "Read-only'id' is part of the key, which can't change: delete the row and insert it again",
      ],
      ['customer_idCustomerWho ordered', 'int64INTEGER', '', 'Can be changedNeeded in a new row'],
      ['status', 'string?TEXT', 'default', 'Can be changed'],
    ]);
    expect(rowsOf(navigations)).toEqual([
      ['customer', 'shop.customers', 'one', 'customer_id → id', ''],
      [
        'order_lines',
        'shop.order_lines',
        'many',
        'id → order_id',
        'from the other sidenot enforced',
      ],
    ]);
    expect(navigations.querySelector('a')?.getAttribute('href')).toBe('/browse/shop.customers');
    expect(textOf(page.querySelector('#gd-entity-columns'))).toBe('Columns (3)');
  });

  it("says once why an entity's rows can't be changed, not for each column", async () => {
    const reason = "shop.orders can't be changed: shop is read-only";
    const { page, facts, showStructure } = await open(
      '/browse/shop.orders',
      entityOf({
        capabilities: {
          canInsert: false,
          canUpdate: false,
          canDelete: false,
          insertReason: reason,
          changeReason: reason,
        },
        hasTriggers: true,
        uniqueKeys: [{ name: 'ux', columns: ['customer_id', 'status'], isDeclared: false }],
        rowCountEstimate: null,
      }),
    );
    await showStructure();
    expect(facts()).toEqual([
      ['Full name', 'shop.main.orders'],
      ['Key', 'id'],
      ['Unique', 'customer_id, status'],
      ['Shown by', 'statuswhere other rows refer to its rows'],
      ['Triggers', 'Changes to its rows set off triggers'],
      ['Changes', `${reason}.`],
    ]);
    expect(
      [...page.querySelectorAll('gd-entity-structure table')[0].querySelectorAll('thead th')]
        .length,
    ).toBe(3);
  });

  it("shows a virtual entity's query, and what is wrong with it", async () => {
    const { page, facts, showStructure } = await open(
      '/browse/reports.big_spenders',
      entityOf({
        name: 'reports.big_spenders',
        qualifiedName: 'reports.big_spenders',
        kind: 'virtual',
        source: null,
        schema: null,
        table: null,
        rowCountEstimate: null,
        key: null,
        displayColumn: null,
        navigations: [],
        query: 'shop.customers.where(credit_limit > 1000)',
        problem: 'GDQ2001: there is no credit_limit',
        capabilities: {
          canInsert: false,
          canUpdate: false,
          canDelete: false,
          changeReason: 'reports.big_spenders is defined by a query: only tables can be changed',
        },
      }),
    );
    expect(textOf(page.querySelector('.subtitle'))).toBe(
      'A virtual entity: the rows of a query, as the catalog defines it',
    );
    await showStructure();
    expect(facts()[0]).toEqual(['Key', 'None']);
    expect(textOf(page.querySelector('gd-entity-structure gd-message'))).toBe(
      "errorThis virtual entity doesn't work: GDQ2001: there is no credit_limit",
    );
    expect(textOf(page.querySelector('gd-entity-structure pre'))).toBe(
      'shop.customers.where(credit_limit > 1000)',
    );
    expect(textOf(page.querySelector('#gd-entity-navigations + .empty'))).toBe(
      "Its rows aren't linked to other rows.",
    );
  });

  it('says when there is no such entity, without its rows', async () => {
    const { http, page, shown } = await open('/browse/shop.gone', null);
    (await requestTo(http, entityUrl('shop.gone'))).flush(
      problemBody('not-found', 'There is no such entity'),
      { status: 404, statusText: 'Not Found' },
    );
    await shown();
    expect(alertsOf(page)).toBe(
      'error There is no shop.gone in the catalog (any more). Find it in the catalog, or choose another.',
    );
    expect(page.querySelector('mat-tab-group')).toBeNull();
  });

  it("says why it couldn't read the entity, and reads it again", async () => {
    const { http, page, shown } = await open('/browse/shop.orders', null);
    (await requestTo(http, entityUrl('shop.orders'))).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await shown();
    expect(alertsOf(page)).toBe("error Couldn't read shop.orders: Oops. Try again");
    clickButton(page, 'Try again');
    (await requestTo(http, entityUrl('shop.orders'))).flush(entityOf());
    await shown();
    expect(alertsOf(page)).toBe('');
    expect(textOf(page.querySelector('h1'))).toBe('shop.orders');
  });

  it('reads the entity again when the catalog changes, showing it meanwhile', async () => {
    const { http, page, shown, showStructure } = await open();
    await showStructure();
    TestBed.inject(CatalogVersion).seen('v2');
    await shown();
    const again = await requestTo(http, entityUrl('shop.orders'));
    expect(textOf(page.querySelector('h1'))).toBe('shop.orders');
    again.flush(entityOf({ comment: 'Orders, as placed' }), {
      headers: { [catalogVersionHeader]: 'v2' },
    });
    await shown();
    expect(textOf(page.querySelector('.comment'))).toBe('Orders, as placed');
    expect(http.match(entityUrl('shop.orders'))).toEqual([]);
  });

  it('keeps the entity shown when reading it again fails, and says why', async () => {
    const { http, page, shown } = await open();
    TestBed.inject(CatalogVersion).seen('v2');
    await shown();
    (await requestTo(http, entityUrl('shop.orders'))).flush(null, {
      status: 0,
      statusText: 'Unknown Error',
    });
    await shown();
    expect(textOf(page.querySelector('h1'))).toBe('shop.orders');
    expect(alertsOf(page)).toMatch(/^error Couldn't read shop\.orders: .* Try again$/);
  });

  it("doesn't read the entity again for the answer that says the catalog changed", async () => {
    const { http } = await open('/browse/shop.orders', entityOf(), 'v2');
    await settle();
    expect(http.match(entityUrl('shop.orders'))).toEqual([]);
  });

  it("doesn't show the entity before while another is read, nor take focus from the tree", async () => {
    const { http, page, shown, harness, router } = await open();
    const tree = harness.fixture.nativeElement.querySelector('[role=tree]') as HTMLElement;
    tree.focus();
    void router.navigate(['/browse', 'shop.customers'], { state: keepFocus });
    await shown();
    expect(page.querySelector('h1')).toBeNull();
    expect(page.querySelector('mat-progress-bar')).not.toBeNull();
    (await requestTo(http, searchUrl('shop.customers', 20))).flush({
      text: 'shop.customers',
      hits: [],
      more: false,
    });
    (await requestTo(http, entityUrl('shop.customers'))).flush(
      entityOf({ name: 'shop.customers', qualifiedName: 'shop.main.customers' }),
    );
    await shown();
    await settle();
    expect(textOf(page.querySelector('h1'))).toBe('shop.customers');
    expect(document.activeElement).toBe(tree);
  });

  it('moves focus to the heading of the entity whose link was followed', async () => {
    const { http, page, shown, showStructure } = await open();
    await showStructure();
    const link = page.querySelector<HTMLAnchorElement>('a[href="/browse/shop.customers"]')!;
    link.focus();
    link.click();
    await shown();
    (await requestTo(http, searchUrl('shop.customers', 20))).flush({
      text: 'shop.customers',
      hits: [],
      more: false,
    });
    (await requestTo(http, entityUrl('shop.customers'))).flush(
      entityOf({ name: 'shop.customers', qualifiedName: 'shop.main.customers', navigations: [] }),
    );
    await shown();
    await settle();
    expect(document.activeElement).toBe(page.querySelector('h1'));
    expect(textOf(document.activeElement)).toBe('shop.customers');
  });

  it("browses the entity's rows in the grid, the address keeping the grid's state in place", async () => {
    const { http, page, shown, router, harness } = await open(
      '/browse/shop.orders;sort=-total;page=2',
    );
    const body = await answerPage(
      http,
      pageOf(orderRows(3, 3), { offset: 3, total: 7, hasMore: true }, orderColumns()),
    );
    await shown();
    expect(body).toMatchObject({
      source: { entity: 'shop.orders' },
      grid: { sort: [{ column: 'total', desc: true }], offset: 3, limit: 3 },
      includeSchema: true,
    });
    expect(gridCells(page).map((cells) => cells[0])).toEqual(['1004', '1005', '1006']);
    const navigations: boolean[] = [];
    router.events.subscribe((event) => {
      if ('navigationTrigger' in event) {
        navigations.push(router.getCurrentNavigation()?.extras.replaceUrl ?? false);
      }
    });
    page.querySelector<HTMLElement>('.ag-header-cell[col-id=c1] .ag-header-cell-label')!.click();
    await shown();
    await answerPage(http, pageOf(orderRows(3), { total: 7, hasMore: true }));
    await shown();
    expect(router.url).toBe('/browse/shop.orders;sort=status');
    expect(navigations).toEqual([true]);
    expect(TestBed.inject(Title).getTitle()).toBe('shop.orders');
    expect(harness.routeNativeElement?.querySelector('gd-browse-page')).toBe(page);
  });

  it('follows a navigation from the row chosen in the crumb before, through the trail', async () => {
    const { http, page, shown } = await open('/browse/shop.customers;row=42/orders', null);
    const trail = await requestTo(http, trailUrl, 'POST');
    expect(trail.request.body).toEqual({
      crumbs: [
        { entity: 'shop.customers', key: ['42'] },
        { navigation: 'orders', key: null },
      ],
    });
    trail.flush({
      crumbs: [
        {
          entity: 'shop.customers',
          label: 'shop.customers',
          title: 'Acme',
          found: true,
          problem: null,
        },
        { entity: 'shop.orders', label: 'orders', title: null, found: null, problem: null },
      ],
    });
    await shown();
    (await requestTo(http, entityUrl('shop.orders'))).flush(entityOf());
    const body = await answerPage(http, pageOf(orderRows(2), {}, orderColumns()));
    await shown();
    expect(body.source).toEqual({
      from: { entity: 'shop.customers', key: ['42'] },
      navigation: 'orders',
    });
    expect(textOf(page.querySelector('.path'))).toBe('shop.customers › orders');
    expect(textOf(page.querySelector('h1'))).toBe('shop.orders');
    expect(gridCells(page).length).toBe(2);
    expect(TestBed.inject(Title).getTitle()).toBe('shop.customers › orders');
  });

  it('browses from the entity each crumb reaches, without asking for the trail again as rows are chosen', async () => {
    const { http, page, shown, router } = await open(
      '/browse/shop.customers;row=42/orders;row=1001/order_lines;row=7',
      null,
    );
    const trail = await requestTo(http, trailUrl, 'POST');
    expect(trail.request.body).toEqual({
      crumbs: [
        { entity: 'shop.customers', key: ['42'] },
        { navigation: 'orders', key: ['1001'] },
        { navigation: 'order_lines', key: null },
      ],
    });
    trail.flush({
      crumbs: [
        {
          entity: 'shop.customers',
          label: 'shop.customers',
          title: 'Acme',
          found: true,
          problem: null,
        },
        { entity: 'shop.orders', label: 'orders', title: '1001', found: true, problem: null },
        {
          entity: 'shop.order_lines',
          label: 'order_lines',
          title: null,
          found: null,
          problem: null,
        },
      ],
    });
    await shown();
    (await requestTo(http, entityUrl('shop.order_lines'))).flush(
      entityOf({ name: 'shop.order_lines', qualifiedName: 'shop.main.order_lines' }),
    );
    const body = await answerPage(http, pageOf(orderRows(3), {}, orderColumns()));
    await shown();
    expect(body.source).toEqual({
      from: { entity: 'shop.orders', key: ['1001'] },
      navigation: 'order_lines',
    });
    page.querySelector<HTMLElement>('.ag-row[row-index="1"] [col-id=c1]')!.click();
    await shown();
    expect(router.url).toBe('/browse/shop.customers;row=42/orders;row=1001/order_lines;row=1002');
    expect(http.match(trailUrl)).toEqual([]);
  });

  it('titles the page by the crumb shown, as the address says it', async () => {
    const { http, shown, router } = await open('/browse/shop.customers;row=42/orders', null);
    (await requestTo(http, trailUrl, 'POST')).flush({
      crumbs: [
        {
          entity: 'shop.customers',
          label: 'shop.customers',
          title: 'Acme',
          found: true,
          problem: null,
        },
        { entity: 'shop.orders', label: 'orders', title: null, found: null, problem: null },
      ],
    });
    await shown();
    expect(TestBed.inject(Title).getTitle()).toBe('shop.customers › orders');
    await router.navigateByUrl('/browse/shop.customers;row=42/orders?at=0');
    await shown();
    expect(TestBed.inject(Title).getTitle()).toBe('shop.customers');
  });

  it("says when a crumb can't be followed, and shows no rows", async () => {
    const { http, page, shown } = await open('/browse/shop.customers/orders', null);
    (await requestTo(http, trailUrl, 'POST')).flush({
      crumbs: [
        {
          entity: 'shop.customers',
          label: 'shop.customers',
          title: null,
          found: null,
          problem: null,
        },
        { entity: 'shop.orders', label: 'orders', title: null, found: null, problem: null },
      ],
    });
    await shown();
    expect(alertsOf(page)).toBe(
      "error Can't follow shop.customers › orders: No row is chosen in shop.customers, so orders leads nowhere.",
    );
    expect(page.querySelector('mat-tab-group')).toBeNull();
    expect(http.match((request) => request.url === '/api/browse/page')).toEqual([]);
  });

  it("says when the row chosen in a crumb isn't among its rows", async () => {
    const { http, page, shown } = await open('/browse/shop.customers;row=999999/orders', null);
    (await requestTo(http, trailUrl, 'POST')).flush({
      crumbs: [
        {
          entity: 'shop.customers',
          label: 'shop.customers',
          title: null,
          found: false,
          problem: null,
        },
        { entity: 'shop.orders', label: 'orders', title: null, found: null, problem: null },
      ],
    });
    await shown();
    expect(alertsOf(page)).toBe(
      "error Can't follow shop.customers › orders: The row chosen in shop.customers isn't among its rows (any more), so orders leads nowhere.",
    );
    expect(page.querySelector('mat-tab-group')).toBeNull();
  });

  it("says why the trail can't be followed, as it says", async () => {
    const { http, page, shown } = await open('/browse/shop.customers;row=1/nowhere', null);
    (await requestTo(http, trailUrl, 'POST')).flush({
      crumbs: [
        {
          entity: 'shop.customers',
          label: 'shop.customers',
          title: 'Acme',
          found: true,
          problem: null,
        },
        {
          entity: null,
          label: 'nowhere',
          title: null,
          found: null,
          problem: "shop.customers has no navigation 'nowhere'",
        },
      ],
    });
    await shown();
    expect(alertsOf(page)).toBe(
      "error Can't follow shop.customers › nowhere: shop.customers has no navigation 'nowhere'.",
    );
  });

  it('shows an earlier crumb, and lets go of the crumbs after it when another row is chosen there', async () => {
    const { http, page, shown, router } = await open(
      '/browse/shop.orders;row=1001/order_lines?at=0',
    );
    await answerPage(http, pageOf(orderRows(3), {}, orderColumns()));
    await shown();
    expect(textOf(page.querySelector('.path'))).toBe('shop.orders');
    expect(
      [...page.querySelectorAll('.ag-row-selected')].map((row) => row.getAttribute('row-index')),
    ).toEqual(['0']);
    page.querySelector<HTMLElement>('.ag-row[row-index="2"] [col-id=c1]')!.click();
    await shown();
    expect(router.url).toBe('/browse/shop.orders;row=1003');
  });

  it('keeps the crumbs after when the grid changes otherwise', async () => {
    const { http, page, shown, router } = await open(
      '/browse/shop.orders;row=1001/order_lines?at=0',
    );
    await answerPage(http, pageOf(orderRows(3), { total: 7, hasMore: true }, orderColumns()));
    await shown();
    page.querySelector<HTMLElement>('.ag-paging-button[aria-label="Next Page"]')!.click();
    await shown();
    await answerPage(http, pageOf(orderRows(3, 3), { offset: 3, total: null, hasMore: true }));
    await shown();
    expect(router.url).toBe('/browse/shop.orders;page=2;row=1001/order_lines?at=0');
  });

  it("says what in the address couldn't be read", async () => {
    const { page, http } = await open('/browse/shop.orders;page=zero');
    expect(textOf(page.querySelector('gd-message.kind-warning'))).toBe(
      "warningThe page (page) of shop.orders couldn't be read, and is left out: zero isn't a page, which counts from 1.",
    );
    expect((await pageRequest(http)).request.body).toMatchObject({ grid: { offset: 0 } });
  });
});

describe('the words of an entity page', () => {
  it('says what an entity is', () => {
    expect(describeEntity(entityOf({ kind: 'view' }))).toBe('A view of shop');
    expect(describeEntity(entityOf({ source: null }))).toBe('A table');
  });

  it('says what may be done with its rows', () => {
    expect(
      describeChanges({
        canInsert: false,
        canUpdate: true,
        canDelete: true,
        insertReason: 'blob is binary',
      }),
    ).toEqual(['You may change and delete its rows.', 'No rows can be added: blob is binary.']);
    expect(
      describeChanges({
        canInsert: true,
        canUpdate: false,
        canDelete: false,
        changeReason: 'No key.',
      }),
    ).toEqual(['You may add its rows.', 'No key.']);
  });

  it('pairs the columns a navigation goes through', () => {
    expect(
      throughOf({ ...entityOf().navigations[0], columns: ['a', 'b'], targetColumns: ['x', 'y'] }),
    ).toBe('a → x, b → y');
  });
});
