import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import {
  answerChildren,
  catalogOf,
  childrenUrl,
  entityOf,
  schemaNode,
  sourceNode,
  tableNode,
  viewportsFor,
} from '../../../testing/catalog';
import { problemBody, sessionOf } from '../../../testing/auth';
import { requestTo, settle } from '../../../testing/http';
import { alertsOf, clickButton, openPage, pageProviders, textOf } from '../../../testing/pages';
import { POLL_INTERVAL } from '../../core/api/poll';
import type { Session } from '../../core/auth/auth-store';
import { keepFocus } from '../../core/browser/keep-focus';
import type { TreeNode } from '../../core/catalog/catalog-tree-store';
import { browseRoutes } from './browse.routes';
import { SEARCH_WAIT } from './catalog-panel';

describe('CatalogTree', () => {
  const wide = new BehaviorSubject<BreakpointState>({ matches: true, breakpoints: {} });
  const shop = sourceNode('shop', { label: 'The shop', isReadOnly: true });
  const pg = sourceNode('pg', { sourceKind: 'postgres' });
  const sales = schemaNode('shop.sales');
  const orders = tableNode('shop.orders', { comment: 'What was ordered', editable: false });
  const openOrders = tableNode('shop.open_orders', { kind: 'view', rows: null, editable: false });

  beforeEach(() => {
    wide.next({ matches: true, breakpoints: {} });
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'browse', children: browseRoutes }]),
        { provide: BreakpointObserver, useValue: { observe: () => wide } },
        { provide: POLL_INTERVAL, useValue: 1 },
        { provide: SEARCH_WAIT, useValue: 0 },
      ],
    });
    viewportsFor(20);
  });

  /** Browsing opened, the catalog's start answered, and the tree's top nodes. */
  async function open(roots: TreeNode[] = [pg, shop], session: Session = sessionOf()) {
    const opened = await openPage('/browse', session);
    (await requestTo(opened.http, '/api/catalog')).flush(catalogOf());
    await answerChildren(opened.http, null, roots);
    const page = opened.harness.fixture.nativeElement as HTMLElement;
    const tree = page.querySelector<HTMLElement>('[role=tree]')!;
    const shown = async () => {
      await settle();
      opened.harness.detectChanges();
      await opened.harness.fixture.whenStable();
    };
    const press = async (key: string) => {
      tree.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
      await shown();
    };
    const items = () => [...page.querySelectorAll<HTMLElement>('[role=treeitem]')];
    const active = () => {
      const id = tree.getAttribute('aria-activedescendant');
      return id ? textOf(page.querySelector(`#${id} .name`)) : null;
    };
    await shown();
    return { ...opened, page, tree, shown, press, items, active };
  }

  it('is a tree of the catalog, each node saying where it is and how it stands', async () => {
    const { http, items, page, shown } = await open();
    const tree = page.querySelector('[role=tree]')!;
    expect(tree.getAttribute('aria-label')).toBe('Catalog');
    expect(tree.getAttribute('tabindex')).toBe('0');
    expect(items().map((item) => textOf(item.querySelector('.name')))).toEqual(['pg', 'shop']);
    expect(
      ['aria-level', 'aria-setsize', 'aria-posinset', 'aria-expanded'].map((name) =>
        items()[1].getAttribute(name),
      ),
    ).toEqual(['1', '2', '2', 'false']);
    expect(textOf(items()[1])).toBe('chevron_rightdatabaseshop, connectionThe shoplock, read-only');

    items()[1].click();
    await shown();
    expect(items()[1].getAttribute('aria-busy')).toBe('true');
    expect(items()[1].getAttribute('aria-expanded')).toBe('false');
    await answerChildren(http, 'shop', [sales, openOrders, orders]);
    await shown();
    expect(items()[1].getAttribute('aria-expanded')).toBe('true');
    expect(items().map((item) => textOf(item.querySelector('.name')))).toEqual([
      'pg',
      'shop',
      'sales',
      'open_orders',
      'orders',
    ]);
    const table = items()[4];
    expect(
      ['aria-level', 'aria-setsize', 'aria-posinset'].map((name) => table.getAttribute(name)),
    ).toEqual(['2', '3', '3']);
    expect(table.getAttribute('aria-expanded')).toBeNull();
    expect(table.getAttribute('title')).toBe('What was ordered');
    expect(textOf(table)).toBe('tableorders, table, about 1.2K rows');
    expect(table.querySelector('.rows')?.getAttribute('title')).toBe('About 1,200 rows');
    expect(textOf(items()[3])).toBe('table_eyeopen_orders, view');
    expect(table.querySelector('a')?.getAttribute('href')).toBe('/browse/shop.orders');
    expect(table.querySelector('a')?.getAttribute('tabindex')).toBe('-1');
  });

  it('says how the schemas of sources stand', async () => {
    const { items } = await open([
      sourceNode('a', { status: 'failed', hasChildren: false }),
      sourceNode('b', { status: 'notLoaded', hasChildren: false }),
      sourceNode('c', { status: 'failed' }),
    ]);
    expect(items().map((item) => textOf(item))).toEqual([
      "databasea, connectionerror, its schema couldn't be read",
      'databaseb, connectionnot read yet',
      "chevron_rightdatabasec, connectionerror, its schema couldn't be read",
    ]);
  });

  it('moves through the nodes with the arrow keys, Home and End, opening and closing them', async () => {
    const { http, tree, press, active, items } = await open();
    tree.focus();
    tree.dispatchEvent(new FocusEvent('focus'));
    await press('ArrowDown');
    expect(active()).toBe('shop');
    await press('ArrowUp');
    expect(active()).toBe('pg');
    await press('End');
    expect(active()).toBe('shop');

    await press('ArrowRight');
    await answerChildren(http, 'shop', [sales, orders]);
    await press('ArrowRight');
    expect(active()).toBe('sales');
    await press('ArrowRight');
    await answerChildren(http, 'shop.sales', [tableNode('shop.sales.invoices')]);
    await press('End');
    expect(active()).toBe('orders');
    await press('ArrowLeft');
    expect(active()).toBe('shop');
    await press('ArrowLeft');
    expect(items().length).toBe(2);
    expect(active()).toBe('shop');
    await press('Home');
    expect(active()).toBe('pg');
    // Opened again, as it was.
    await press('End');
    await press('ArrowRight');
    expect(items().map((item) => textOf(item.querySelector('.name')))).toEqual([
      'pg',
      'shop',
      'sales',
      'invoices',
      'orders',
    ]);
  });

  it('goes a page at a time, opens all siblings with *, and ignores keys with modifiers', async () => {
    viewportsFor(4);
    const sources = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'].map((alias) => sourceNode(alias));
    const { http, tree, press, active, shown } = await open(sources);
    tree.focus();
    tree.dispatchEvent(new FocusEvent('focus'));
    await press('PageDown');
    expect(active()).toBe('d');
    await press('PageDown');
    expect(active()).toBe('g');
    await press('PageUp');
    expect(active()).toBe('d');
    tree.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'ArrowDown',
        ctrlKey: true,
        bubbles: true,
        cancelable: true,
      }),
    );
    await shown();
    expect(active()).toBe('d');
    await press('Home');
    await press('*');
    for (const alias of ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h']) {
      await answerChildren(http, alias, [tableNode(`${alias}.t`)]);
    }
  });

  it("puts the tree's active descendant only on a row in the page", async () => {
    viewportsFor(3);
    const sources = Array.from({ length: 40 }, (_, index) => sourceNode(`s${index + 10}`));
    const { tree, press, page, shown } = await open(sources);
    tree.focus();
    tree.dispatchEvent(new FocusEvent('focus'));
    await shown();
    expect(tree.getAttribute('aria-activedescendant')).not.toBeNull();
    await press('End');
    // The last row isn't rendered until the viewport scrolls to it (which jsdom doesn't do).
    const id = tree.getAttribute('aria-activedescendant');
    expect(id === null || page.querySelector(`#${id}`) !== null).toBe(true);
  });

  it('keeps focus on the tree when a row is pressed, not on its link', async () => {
    const { http, items, tree, shown } = await open();
    items()[1].click();
    await answerChildren(http, 'shop', [orders]);
    await shown();
    const link = items()[2].querySelector('a')!;
    const press = new MouseEvent('mousedown', { button: 0, bubbles: true, cancelable: true });
    link.dispatchEvent(press);
    expect(press.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(tree);
    const middle = new MouseEvent('mousedown', { button: 1, bubbles: true, cancelable: true });
    link.dispatchEvent(middle);
    expect(middle.defaultPrevented).toBe(false);
  });

  it('starts at the entity shown when focus comes to it', async () => {
    const { http, tree, shown, active, harness, press } = await open();
    const router = TestBed.inject(Router);
    void router.navigateByUrl('/browse/shop.orders');
    (await requestTo(http, '/api/catalog/tree/search?text=shop.orders&take=20')).flush({
      text: 'shop.orders',
      hits: [{ node: orders, path: ['shop'], columns: null }],
      more: false,
    });
    await answerChildren(http, 'shop', [sales, orders]);
    (await requestTo(http, '/api/catalog/entity?name=shop.orders')).flush(entityOf());
    await shown();
    harness.detectChanges();
    expect(active()).toBe('orders');
    tree.focus();
    tree.dispatchEvent(new FocusEvent('focus'));
    await shown();
    expect(active()).toBe('orders');
    // Focus coming again leaves the keyboard where it was.
    await press('ArrowUp');
    tree.dispatchEvent(new FocusEvent('focus'));
    await shown();
    expect(active()).toBe('sales');
    const selected = harness.fixture.nativeElement.querySelector('[aria-selected=true]');
    expect(textOf(selected?.querySelector('.name'))).toBe('orders');
  });

  it('opens an entity with Enter, leaving focus in the tree', async () => {
    const { http, tree, press, shown } = await open();
    tree.focus();
    tree.dispatchEvent(new FocusEvent('focus'));
    await press('End');
    await press('Enter');
    await answerChildren(http, 'shop', [sales, orders]);
    await press('End');
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate');
    // The page's request keeps the application busy: the key is pressed without waiting for it.
    tree.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(navigate).toHaveBeenCalledWith(['/browse', 'shop.orders'], { state: keepFocus });
    (await requestTo(http, '/api/catalog/entity?name=shop.orders')).flush(entityOf());
    await shown();
    expect(router.url).toBe('/browse/shop.orders');
    expect(document.activeElement).toBe(tree);
  });

  it('goes to the node whose name starts with what is typed', async () => {
    const { http, tree, press, active } = await open([
      pg,
      sourceNode('sales'),
      sourceNode('salt'),
      sourceNode('shop'),
      sourceNode('stock'),
    ]);
    tree.focus();
    tree.dispatchEvent(new FocusEvent('focus'));
    await press('s');
    expect(active()).toBe('sales');
    // A word being typed stays on the node it still starts.
    await press('a');
    expect(active()).toBe('sales');
    await press('l');
    expect(active()).toBe('sales');
    await press('t');
    expect(active()).toBe('salt');
    // A space in a name being typed is part of it, not Enter's.
    await press(' ');
    expect(active()).toBe('salt');
    http.expectNone(childrenUrl('salt'));
    vi.spyOn(Date, 'now').mockReturnValue(Date.now() + 10_000);
    // The same letter again goes on to the next node it starts, round to the first.
    await press('s');
    expect(active()).toBe('shop');
    await press('s');
    expect(active()).toBe('stock');
    await press('s');
    expect(active()).toBe('sales');
    http.expectNone(childrenUrl('sales'));
  });

  it('opens and closes a node by its arrow, and opens an entity clicked anywhere on its row', async () => {
    const { http, items, shown } = await open();
    (items()[1].querySelector('.twisty') as HTMLElement).click();
    await answerChildren(http, 'shop', [sales, orders]);
    await shown();
    expect(items().length).toBe(4);
    (items()[3].querySelector('.kind') as HTMLElement).click();
    (await requestTo(http, '/api/catalog/entity?name=shop.orders')).flush(entityOf());
    await shown();
    expect(TestBed.inject(Router).url).toBe('/browse/shop.orders');
    (items()[1].querySelector('.twisty') as HTMLElement).click();
    await shown();
    expect(items().length).toBe(2);
  });

  it("says why a node couldn't be opened, and tries again", async () => {
    const { http, items, page, shown } = await open();
    items()[1].click();
    (await requestTo(http, childrenUrl('shop'))).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await shown();
    expect(alertsOf(page)).toBe("error Couldn't open shop: Oops. Try again");
    expect(items()[1].getAttribute('aria-expanded')).toBe('false');
    clickButton(page, 'Try again');
    await answerChildren(http, 'shop', [orders]);
    await shown();
    expect(alertsOf(page)).toBe('');
    expect(items().length).toBe(3);
  });

  it("says why the catalog couldn't be loaded, and loads it again", async () => {
    const opened = await openPage('/browse');
    (await requestTo(opened.http, '/api/catalog')).flush(catalogOf());
    (await requestTo(opened.http, childrenUrl(null))).flush(null, {
      status: 0,
      statusText: 'Unknown Error',
    });
    await settle();
    opened.harness.detectChanges();
    const page = opened.harness.fixture.nativeElement as HTMLElement;
    expect(alertsOf(page.querySelector('gd-catalog-tree')!)).toMatch(
      /^error Couldn't load the catalog: .*reach the server.* Try again$/,
    );
    clickButton(page.querySelector('gd-catalog-tree')!, 'Try again');
    await answerChildren(opened.http, null, [shop]);
    await settle();
    opened.harness.detectChanges();
    expect(alertsOf(page.querySelector('gd-catalog-tree')!)).toBe('');
    expect(page.querySelectorAll('[role=treeitem]').length).toBe(1);
  });

  it('asks administrators for a first connection', async () => {
    const { page } = await open([]);
    expect(textOf(page.querySelector('gd-catalog-tree .empty'))).toBe(
      'There are no connections yet. Make one to browse a database.',
    );
    expect(page.querySelector('gd-catalog-tree .empty a')?.getAttribute('href')).toBe(
      '/admin/connections/new',
    );
  });

  it("tells those who change data which tables they can't change, where their sources take changes", async () => {
    const writable = sourceNode('pg', { isReadOnly: false });
    const { http, items, shown } = await open([writable, shop], sessionOf('dataManager'));
    items()[0].click();
    await answerChildren(http, 'pg', [
      tableNode('pg.accounts'),
      tableNode('pg.log', { editable: false }),
      tableNode('pg.totals', { kind: 'view', editable: false }),
    ]);
    await shown();
    items()[4].click();
    await answerChildren(http, 'shop', [orders]);
    await shown();
    expect(items().map((item) => !!item.querySelector('mat-icon.flag'))).toEqual([
      false,
      false,
      true,
      false,
      true,
      false,
    ]);
    expect(textOf(items()[2])).toContain(", its rows can't be changed");
    expect(textOf(items()[4])).toContain(', read-only');
  });

  it('follows the sources while their schemas are read, and loads the tree again once one is', async () => {
    const reading = sourceNode('pg', { status: 'loading', hasChildren: false });
    const { http, items, shown } = await open([reading, shop]);
    items()[1].click();
    await answerChildren(http, 'shop', [orders]);
    await answerChildren(http, null, [reading, shop]);
    // Read: the catalog's version changes, and the tree is loaded again, keeping shop open.
    await answerChildren(http, null, [sourceNode('pg', { sourceKind: 'postgres' }), shop], 'v2');
    await answerChildren(http, null, [sourceNode('pg', { sourceKind: 'postgres' }), shop], 'v2');
    await answerChildren(http, 'shop', [orders, tableNode('shop.payments')], 'v2');
    await shown();
    expect(items().map((item) => textOf(item.querySelector('.name')))).toEqual([
      'pg',
      'shop',
      'orders',
      'payments',
    ]);
    http.expectNone(childrenUrl(null));
  });
});
