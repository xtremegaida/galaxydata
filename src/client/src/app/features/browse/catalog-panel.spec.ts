import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import {
  answerChildren,
  catalogOf,
  entityOf,
  hitOf,
  schemaNode,
  searchUrl,
  sourceNode,
  tableNode,
  viewportsFor,
} from '../../../testing/catalog';
import { problemBody } from '../../../testing/auth';
import { requestTo, settle } from '../../../testing/http';
import { alertsOf, clickButton, openPage, pageProviders, textOf } from '../../../testing/pages';
import { POLL_INTERVAL } from '../../core/api/poll';
import { browseRoutes } from './browse.routes';
import { keepFocus } from '../../core/browser/keep-focus';
import { SEARCH_WAIT, mostHits } from './catalog-panel';

describe('CatalogPanel', () => {
  const wide = new BehaviorSubject<BreakpointState>({ matches: true, breakpoints: {} });
  const shop = sourceNode('shop');
  const pg = sourceNode('pg', { sourceKind: 'postgres' });
  const sales = schemaNode('shop.sales');
  const orders = tableNode('shop.orders');
  const invoices = tableNode('shop.sales.invoices');

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

  async function open() {
    const opened = await openPage('/browse');
    (await requestTo(opened.http, '/api/catalog')).flush(catalogOf());
    await answerChildren(opened.http, null, [pg, shop]);
    const page = opened.harness.fixture.nativeElement as HTMLElement;
    const input = page.querySelector<HTMLInputElement>('input[type=search]')!;
    const shown = async () => {
      await settle();
      opened.harness.detectChanges();
    };
    const type = async (text: string) => {
      input.value = text;
      input.dispatchEvent(new Event('input'));
      await shown();
    };
    const press = async (key: string) => {
      input.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
      await shown();
    };
    const options = () => [...page.querySelectorAll<HTMLElement>('[role=option]')];
    const status = () => textOf(page.querySelector('gd-catalog-panel [role=status]'));
    await shown();
    return { ...opened, page, input, shown, type, press, options, status };
  }

  const found = (
    text: string,
    hits = [hitOf(orders, ['shop']), hitOf(invoices, ['shop', 'shop.sales'])],
    more = false,
  ) => ({
    text,
    hits,
    more,
  });

  it('lists what it finds in place of the tree, with what was looked for marked', async () => {
    const { http, page, input, type, options, status, shown } = await open();
    expect(input.getAttribute('role')).toBe('combobox');
    expect(input.getAttribute('aria-expanded')).toBe('false');
    await type(' ord ');
    expect(page.querySelector('[role=tree]')).toBeNull();
    expect(input.getAttribute('aria-expanded')).toBe('true');
    (await requestTo(http, searchUrl('ord'))).flush(
      found('ord', [
        hitOf(orders, ['shop']),
        hitOf(tableNode('shop.customers'), ['shop'], ['last_order_id']),
      ]),
    );
    await shown();
    expect(options().map((option) => textOf(option))).toEqual([
      'tableorders, table, at shop.orders',
      'tablecustomers, table, at shop.customers, Columns: last_order_id',
    ]);
    expect([...options()[0].querySelectorAll('mark')].map((mark) => mark.textContent)).toEqual([
      'ord',
      'ord',
    ]);
    expect(status()).toBe('2 found.');
    expect(page.querySelector('[role=listbox]')?.getAttribute('aria-label')).toBe(
      'Found in the catalog',
    );
  });

  it('says when nothing is found, and when there is more than it shows', async () => {
    const { http, type, status, page, shown } = await open();
    await type('zzz');
    (await requestTo(http, searchUrl('zzz'))).flush(found('zzz', []));
    await shown();
    expect(status()).toBe('Nothing in the catalog has “zzz” in its name.');

    await type('o');
    (await requestTo(http, searchUrl('o'))).flush(found('o', undefined, true));
    await shown();
    expect(status()).toBe('The first 2 found.');
    const more = [...page.querySelectorAll('gd-catalog-panel button')].find(
      (button) => textOf(button) === 'Show more',
    ) as HTMLButtonElement;
    more.focus();
    more.click();
    await shown();
    // The button goes once more are found: focus goes back to the field.
    expect(document.activeElement).toBe(page.querySelector('input[type=search]'));
    expect(more.isConnected).toBe(false);
    (await requestTo(http, searchUrl('o', mostHits))).flush(found('o', undefined, true));
    await shown();
    expect(status()).toBe('The first 2 found.');
    expect(textOf(page.querySelector('gd-catalog-panel .status'))).toBe('The first 2 found.');

    // New text asks for the first ones again.
    await type('or');
    (await requestTo(http, searchUrl('or'))).flush(found('or'));
    await shown();
    expect(status()).toBe('2 found.');
  });

  it('goes through what it found with the arrow keys, and shows the one chosen in the tree', async () => {
    const { http, page, input, type, press, options, shown } = await open();
    await type('in');
    (await requestTo(http, searchUrl('in'))).flush(found('in'));
    await shown();
    expect(input.getAttribute('aria-activedescendant')).toBeNull();
    await press('ArrowDown');
    await press('ArrowDown');
    await press('ArrowDown');
    expect(input.getAttribute('aria-activedescendant')).toBe(options()[1].id);
    expect(options().map((option) => option.getAttribute('aria-selected'))).toEqual([
      'false',
      'true',
    ]);
    await press('ArrowUp');
    await press('ArrowDown');
    // The page opens as the tree shows the entity: the request is answered without waiting.
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await answerChildren(http, 'shop', [sales, orders]);
    await answerChildren(http, 'shop.sales', [invoices]);
    (await requestTo(http, '/api/catalog/entity?name=shop.sales.invoices')).flush(
      entityOf({ name: 'shop.sales.invoices' }),
    );
    await shown();
    await settle();
    expect(TestBed.inject(Router).url).toBe('/browse/shop.sales.invoices');
    expect(input.value).toBe('');
    const tree = page.querySelector<HTMLElement>('[role=tree]')!;
    expect(document.activeElement).toBe(tree);
    const active = page.querySelector(`#${tree.getAttribute('aria-activedescendant')}`);
    expect(textOf(active?.querySelector('.name'))).toBe('invoices');
    expect(active?.getAttribute('aria-selected')).toBe('true');
  });

  it('opens a schema found, without a page', async () => {
    const { http, page, type, options, shown } = await open();
    await type('sal');
    (await requestTo(http, searchUrl('sal'))).flush(found('sal', [hitOf(sales, ['shop'])]));
    await shown();
    options()[0].click();
    await answerChildren(http, 'shop', [sales, orders]);
    await answerChildren(http, 'shop.sales', [invoices]);
    await shown();
    expect(TestBed.inject(Router).url).toBe('/browse');
    expect(
      [...page.querySelectorAll('[role=treeitem] .name')].map((name) => name.textContent),
    ).toEqual(['pg', 'shop', 'sales', 'invoices', 'orders']);
  });

  it('chooses the first found when Enter comes before the search answers', async () => {
    const { http, type, press, shown } = await open();
    await type('orders');
    await press('Enter');
    (await requestTo(http, searchUrl('orders'))).flush(found('orders'));
    await answerChildren(http, 'shop', [sales, orders]);
    (await requestTo(http, '/api/catalog/entity?name=shop.orders')).flush(entityOf());
    await shown();
    expect(TestBed.inject(Router).url).toBe('/browse/shop.orders');
  });

  it('clears with Escape, back to the tree, and leaves Escape with nothing typed to the page', async () => {
    const { http, page, input, type } = await open();
    await type('ord');
    (await requestTo(http, searchUrl('ord'))).flush(found('ord'));
    const clearing = new KeyboardEvent('keydown', {
      key: 'Escape',
      bubbles: true,
      cancelable: true,
    });
    const heard = vi.fn();
    page.addEventListener('keydown', heard);
    input.dispatchEvent(clearing);
    await settle();

    expect(clearing.defaultPrevented).toBe(true);
    expect(heard).not.toHaveBeenCalled();
    expect(input.value).toBe('');
    expect(page.querySelector('[role=tree]')).not.toBeNull();
    expect(page.querySelector('[role=listbox]')?.hasAttribute('hidden')).toBe(true);
    const leaving = new KeyboardEvent('keydown', {
      key: 'Escape',
      bubbles: true,
      cancelable: true,
    });
    input.dispatchEvent(leaving);
    expect(leaving.defaultPrevented).toBe(false);
    expect(heard).toHaveBeenCalledTimes(1);
  });

  it('starts new results without one chosen by the arrows', async () => {
    const { http, input, type, press, shown } = await open();
    await type('in');
    (await requestTo(http, searchUrl('in'))).flush(found('in'));
    await shown();
    await press('ArrowDown');
    expect(input.getAttribute('aria-activedescendant')).toBe('gd-catalog-hit-0');
    await type('inv');
    (await requestTo(http, searchUrl('inv'))).flush(found('inv'));
    await shown();
    expect(input.getAttribute('aria-activedescendant')).toBeNull();
  });

  it('opens an entity leaving focus in the catalog beside the page, and not over it', async () => {
    const { http, type, options, shown } = await open();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');
    const lastNavigation = () => {
      const [url, extras] = navigate.mock.lastCall ?? [];
      return [String(url), extras];
    };
    await type('ord');
    (await requestTo(http, searchUrl('ord'))).flush(found('ord'));
    await shown();
    options()[0].click();
    expect(lastNavigation()).toEqual(['/browse/shop.orders', { state: keepFocus }]);
    (await requestTo(http, '/api/catalog/entity?name=shop.orders')).flush(entityOf());
    await answerChildren(http, 'shop', [sales, orders]);
    await shown();

    wide.next({ matches: false, breakpoints: {} });
    await shown();
    await type('inv');
    (await requestTo(http, searchUrl('inv'))).flush(found('inv'));
    await shown();
    options()[1].click();
    expect(lastNavigation()).toEqual(['/browse/shop.sales.invoices', { state: undefined }]);
  });

  it("says why it couldn't search, and searches again", async () => {
    const { http, page, type, options, shown } = await open();
    await type('ord');
    (await requestTo(http, searchUrl('ord'))).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await shown();
    expect(alertsOf(page)).toBe("error Couldn't search: Oops. Try again");
    clickButton(page, 'Try again');
    await shown();
    (await requestTo(http, searchUrl('ord'))).flush(found('ord'));
    await shown();
    expect(alertsOf(page)).toBe('');
    expect(options().length).toBe(2);
  });
});
