import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  answerChildren,
  childrenUrl,
  hitOf,
  schemaNode,
  searchUrl,
  shownRows,
  sourceNode,
  tableNode,
} from '../../../testing/catalog';
import { problemBody } from '../../../testing/auth';
import { requestTo, settle } from '../../../testing/http';
import { CatalogTreeStore, beingRead } from './catalog-tree-store';
import { CatalogVersion, catalogVersionInterceptor } from './catalog-version';

describe('CatalogTreeStore', () => {
  let http: HttpTestingController;
  let store: CatalogTreeStore;

  const shop = sourceNode('shop');
  const pg = sourceNode('pg', { sourceKind: 'postgres' });
  const sales = schemaNode('shop.sales');
  const orders = tableNode('shop.orders');
  const invoices = tableNode('shop.sales.invoices');

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([catalogVersionInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(CatalogTreeStore);
  });

  afterEach(() => http.verify());

  /** The top nodes loaded (pg and shop), and shop opened (sales and orders). */
  async function opened(): Promise<void> {
    const started = store.start();
    await answerChildren(http, null, [pg, shop]);
    await started;
    const opening = store.expand('shop');
    await answerChildren(http, 'shop', [sales, orders]);
    await opening;
  }

  it("loads the top nodes once, and each node's children as it is first opened", async () => {
    const started = store.start();
    void store.start();
    await answerChildren(http, null, [pg, shop]);
    await started;
    expect(shownRows(store.rows())).toEqual(['pg', 'shop']);

    const opening = store.expand('shop');
    expect(store.rows()[1]).toMatchObject({ expanded: true, loading: true });
    await answerChildren(http, 'shop', [sales, orders]);
    expect(await opening).toBe(true);
    expect(shownRows(store.rows())).toEqual(['pg', 'shop', '  sales', '  orders']);
    expect(store.rows()[3]).toMatchObject({
      parent: 'shop',
      level: 2,
      position: 2,
      siblings: 2,
      expanded: false,
      loading: false,
      source: shop,
    });

    store.collapse('shop');
    expect(shownRows(store.rows())).toEqual(['pg', 'shop']);
    expect(await store.expand('shop')).toBe(true);
    expect(shownRows(store.rows())).toEqual(['pg', 'shop', '  sales', '  orders']);
    expect(await store.expand('shop.orders')).toBe(false);
  });

  it("keeps a node that can't be opened closed, says why, and opens it when asked again", async () => {
    await opened();
    const opening = store.expand('shop.sales');
    (await requestTo(http, childrenUrl('shop.sales'))).flush(
      problemBody('internal-error', 'Oops'),
      { status: 500, statusText: 'Server Error' },
    );
    expect(await opening).toBe(false);
    expect(store.rows()[2]).toMatchObject({ expanded: false, loading: false });
    expect(store.lastFailure()).toMatchObject({ node: sales, problem: { title: 'Oops' } });

    const again = store.expand('shop.sales');
    await answerChildren(http, 'shop.sales', [invoices]);
    expect(await again).toBe(true);
    expect(store.lastFailure()).toBeNull();
    expect(shownRows(store.rows())).toContain('    invoices');
  });

  it('moves the keyboard to a node closed from one under it', async () => {
    await opened();
    store.active.set('shop.orders');
    store.collapse('pg');
    expect(store.active()).toBe('shop.orders');
    store.collapse('shop');
    expect(store.active()).toBe('shop');
  });

  it('shows a node by opening its ancestors in turn, loading its parent again when it is new', async () => {
    const started = store.start();
    await answerChildren(http, null, [pg, shop]);
    await started;
    const revealing = store.reveal('shop.sales.invoices', ['shop', 'shop.sales']);
    await answerChildren(http, 'shop', [sales, orders]);
    // Children loaded before the node was made don't have it.
    await answerChildren(http, 'shop.sales', []);
    await answerChildren(http, 'shop.sales', [invoices]);
    expect(await revealing).toBe(true);
    expect(store.active()).toBe('shop.sales.invoices');
    expect(shownRows(store.rows())).toEqual(['pg', 'shop', '  sales', '    invoices', '  orders']);

    const gone = store.reveal('shop.gone', ['shop']);
    await answerChildren(http, 'shop', [sales, orders]);
    expect(await gone).toBe(false);
    expect(store.active()).toBe('shop.sales.invoices');
  });

  it("finds where an entity that isn't loaded is, to show it", async () => {
    const selecting = store.select('shop.sales.invoices');
    await answerChildren(http, null, [pg, shop]);
    (await requestTo(http, searchUrl('shop.sales.invoices', 20))).flush({
      text: 'shop.sales.invoices',
      hits: [
        hitOf(tableNode('xl.shop.sales.invoices'), ['xl']),
        hitOf(invoices, ['shop', 'shop.sales']),
      ],
      more: false,
    });
    await answerChildren(http, 'shop', [sales, orders]);
    await answerChildren(http, 'shop.sales', [invoices]);
    await selecting;
    expect(store.selected()).toBe('shop.sales.invoices');
    expect(store.active()).toBe('shop.sales.invoices');

    // One loaded is shown without searching, its ancestors opened again.
    store.collapse('shop');
    await store.select('shop.orders');
    expect(store.active()).toBe('shop.orders');
    expect(shownRows(store.rows())).toContain('  orders');
  });

  it('shows the entity chosen where search found it once, not finding it again', async () => {
    await opened();
    const revealing = store.reveal('shop.sales.invoices', ['shop', 'shop.sales']);
    const selecting = store.select('shop.sales.invoices');
    await answerChildren(http, 'shop.sales', [invoices]);
    await Promise.all([revealing, selecting]);
    http.expectNone((request) => request.url.endsWith('/search'));
    expect(store.active()).toBe('shop.sales.invoices');
  });

  it('loads again what is loaded, keeping open the nodes still there', async () => {
    await opened();
    const opening = store.expand('shop.sales');
    await answerChildren(http, 'shop.sales', [invoices]);
    await opening;
    store.active.set('shop.sales.invoices');

    const refreshing = store.refresh();
    await answerChildren(http, null, [pg, shop], 'v2');
    await answerChildren(http, 'shop', [orders, tableNode('shop.payments')], 'v2');
    await refreshing;
    // sales is gone: its children are let go, and the keyboard goes to the nearest ancestor there.
    expect(shownRows(store.rows())).toEqual(['pg', 'shop', '  orders', '  payments']);
    expect(store.active()).toBe('shop');
    http.expectNone(childrenUrl('shop.sales'));
  });

  it("closes a node gone when it is loaded again, and keeps the children of one that couldn't be", async () => {
    await opened();
    const opening = store.expand('shop.sales');
    await answerChildren(http, 'shop.sales', [invoices]);
    await opening;
    const openingPg = store.expand('pg');
    await answerChildren(http, 'pg', [tableNode('pg.accounts')]);
    await openingPg;

    const refreshing = store.refresh();
    await answerChildren(http, null, [pg, shop], 'v2');
    (await requestTo(http, childrenUrl('pg'))).flush(
      problemBody('not-found', 'There is no such node'),
      {
        status: 404,
        statusText: 'Not Found',
      },
    );
    (await requestTo(http, childrenUrl('shop'))).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await answerChildren(http, 'shop.sales', [invoices], 'v2');
    await refreshing;
    expect(shownRows(store.rows())).toEqual(['pg', 'shop', '  sales', '    invoices', '  orders']);
  });

  it('runs again when asked to while it runs', async () => {
    const started = store.start();
    await answerChildren(http, null, [pg, shop]);
    await started;
    const first = store.refresh();
    const second = store.refresh();
    expect(second).toBe(first);
    await answerChildren(http, null, [pg, shop], 'v2');
    await first;
    await answerChildren(http, null, [pg], 'v3');
    await settle();
    expect(shownRows(store.rows())).toEqual(['pg']);
  });

  it("doesn't let an earlier answer replace a later one", async () => {
    await opened();
    const opening = store.expand('shop.sales');
    const refreshing = store.refresh();
    await answerChildren(http, null, [pg, shop], 'v2');
    await answerChildren(http, 'shop', [sales, orders], 'v2');
    await settle();
    const [early, late] = http.match(childrenUrl('shop.sales'));
    late.flush({ parent: 'shop.sales', nodes: [invoices, tableNode('shop.sales.credits')] });
    early.flush({ parent: 'shop.sales', nodes: [invoices] });
    await Promise.all([opening, refreshing]);
    expect(shownRows(store.rows())).toContain('    credits');
  });

  it('loads again when the catalog is at another version than it was loaded at', async () => {
    await opened();
    const versions = TestBed.inject(CatalogVersion);
    store.catchUp('v1');
    http.expectNone(childrenUrl(null));
    versions.seen('v2');
    store.catchUp(versions.version());
    await answerChildren(http, null, [pg, shop], 'v2');
    await answerChildren(http, 'shop', [sales, orders], 'v2');
    await settle();
    store.catchUp('v2');
    http.expectNone(childrenUrl(null));
  });

  it('keeps open the nodes opened while it loads the tree again, and the keyboard on them', async () => {
    await opened();
    const refreshing = store.refresh();
    await answerChildren(http, null, [pg, shop], 'v2');
    await settle();
    // pg is opened while shop's children are loaded again.
    const opening = store.expand('pg');
    await answerChildren(http, 'pg', [tableNode('pg.accounts')], 'v2');
    await opening;
    store.active.set('pg.accounts');
    await answerChildren(http, 'shop', [sales, orders], 'v2');
    await refreshing;
    expect(shownRows(store.rows())).toEqual(['pg', '  accounts', 'shop', '  sales', '  orders']);
    expect(store.active()).toBe('pg.accounts');
  });

  it("doesn't let an earlier load's failure close a node a later one opened", async () => {
    await opened();
    const opening = store.expand('shop.sales');
    const refreshing = store.refresh();
    await answerChildren(http, null, [pg, shop], 'v2');
    await answerChildren(http, 'shop', [sales, orders], 'v2');
    await settle();
    const [early, late] = http.match(childrenUrl('shop.sales'));
    early.flush(problemBody('internal-error', 'Oops'), { status: 500, statusText: 'Server Error' });
    await settle();
    expect(store.rows()[2]).toMatchObject({ expanded: true, loading: true });
    late.flush({ parent: 'shop.sales', nodes: [invoices] });
    expect(await opening).toBe(true);
    await refreshing;
    expect(store.lastFailure()).toBeNull();
    expect(shownRows(store.rows())).toContain('    invoices');
  });

  it('opens again with a node the nodes left open under it when the tree was loaded again', async () => {
    await opened();
    const opening = store.expand('shop.sales');
    await answerChildren(http, 'shop.sales', [invoices]);
    await opening;
    store.collapse('shop');
    const refreshing = store.refresh();
    await answerChildren(http, null, [pg, shop], 'v2');
    await refreshing;
    // shop's children weren't loaded again: they are as shop opens, and sales with it.
    const reopening = store.expand('shop');
    await answerChildren(http, 'shop', [sales, orders], 'v2');
    await reopening;
    expect(store.rows()[2]).toMatchObject({ expanded: true, loading: true });
    await answerChildren(http, 'shop.sales', [invoices, tableNode('shop.sales.credits')], 'v2');
    await settle();
    expect(shownRows(store.rows())).toEqual([
      'pg',
      'shop',
      '  sales',
      '    invoices',
      '    credits',
      '  orders',
    ]);
  });

  it('follows the catalog after its first load failed, once it is loaded', async () => {
    const versions = TestBed.inject(CatalogVersion);
    const started = store.start();
    (await requestTo(http, childrenUrl(null))).flush(null, {
      status: 0,
      statusText: 'Unknown Error',
    });
    await started;
    expect(store.loaded()).toBe(false);
    const retrying = store.retry();
    await answerChildren(http, null, [pg, shop]);
    await retrying;
    expect(store.loaded()).toBe(true);
    versions.seen('v2');
    store.catchUp('v2');
    await answerChildren(http, null, [pg, shop], 'v2');
    await settle();
    store.catchUp('v2');
    http.expectNone(childrenUrl(null));
  });

  it('loads the whole tree again when asked to after loading it again failed', async () => {
    await opened();
    const refreshing = store.refresh();
    (await requestTo(http, childrenUrl(null))).flush(null, {
      status: 0,
      statusText: 'Unknown Error',
    });
    await refreshing;
    expect(store.problem()?.code).toBe('unreachable');
    const retrying = store.retry();
    await answerChildren(http, null, [pg, shop], 'v2');
    await answerChildren(http, 'shop', [orders], 'v2');
    await retrying;
    expect(store.problem()).toBeNull();
    expect(shownRows(store.rows())).toEqual(['pg', 'shop', '  orders']);
  });

  it('catches the rest up when the sources loaded again come at another version', async () => {
    await opened();
    TestBed.inject(CatalogVersion).seen('v2');
    const reloading = store.reloadRoots();
    await answerChildren(http, null, [pg, shop], 'v2');
    await reloading;
    const again = await requestTo(http, childrenUrl(null));
    // A refresh under way for this version isn't asked for again.
    store.catchUp('v2');
    again.flush({ parent: null, nodes: [pg, shop] }, { headers: { 'X-Catalog-Version': 'v2' } });
    await answerChildren(http, 'shop', [orders], 'v2');
    await settle();
    http.expectNone(childrenUrl(null));
    expect(shownRows(store.rows())).toEqual(['pg', 'shop', '  orders']);
  });

  it('shows only the entity selected last, when an earlier one is found after it', async () => {
    const started = store.start();
    await answerChildren(http, null, [pg, shop]);
    await started;
    const first = store.select('shop.sales.invoices');
    const search = await requestTo(http, searchUrl('shop.sales.invoices', 20));
    const second = store.select('pg.accounts');
    const otherSearch = await requestTo(http, searchUrl('pg.accounts', 20));
    otherSearch.flush({ text: 'pg.accounts', hits: [], more: false });
    search.flush({
      text: 'shop.sales.invoices',
      hits: [hitOf(invoices, ['shop', 'shop.sales'])],
      more: false,
    });
    await Promise.all([first, second]);
    expect(store.selected()).toBe('pg.accounts');
    http.expectNone(childrenUrl('shop'));
    expect(shownRows(store.rows())).toEqual(['pg', 'shop']);
  });

  it('says why the top nodes could not be loaded, keeping those loaded before', async () => {
    await opened();
    const reloading = store.reloadRoots();
    (await requestTo(http, childrenUrl(null))).flush(null, {
      status: 0,
      statusText: 'Unknown Error',
    });
    await reloading;
    expect(store.problem()?.code).toBe('unreachable');
    expect(shownRows(store.rows())).toEqual(['pg', 'shop', '  sales', '  orders']);
    const again = store.reloadRoots();
    await answerChildren(http, null, [pg, shop]);
    await again;
    expect(store.problem()).toBeNull();
  });

  it('follows sources whose schemas are still to be read, or being read', () => {
    expect(beingRead(sourceNode('a', { status: 'loading' }))).toBe(true);
    expect(beingRead(sourceNode('a', { status: 'notLoaded' }))).toBe(true);
    expect(beingRead(sourceNode('a', { status: 'failed' }))).toBe(false);
    expect(beingRead(sourceNode('a'))).toBe(false);
    expect(beingRead(orders)).toBe(false);
  });
});
