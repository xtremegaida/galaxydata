import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { MatSidenavHarness } from '@angular/material/sidenav/testing';
import { Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import {
  answerChildren,
  catalogOf,
  entityOf,
  searchUrl,
  sourceNode,
  tableNode,
  viewportsFor,
} from '../../../testing/catalog';
import { answerGrid } from '../../../testing/browse';
import { requestTo, settle } from '../../../testing/http';
import { clickButton, openPage, pageProviders } from '../../../testing/pages';
import { POLL_INTERVAL } from '../../core/api/poll';
import { BrowseLayout } from './browse-layout';
import { browseRoutes } from './browse.routes';
import { SEARCH_WAIT } from './catalog-panel';

describe('BrowseLayout', () => {
  const wide = new BehaviorSubject<BreakpointState>({ matches: true, breakpoints: {} });
  const shop = sourceNode('shop');

  beforeEach(() => {
    localStorage.clear();
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

  afterEach(() => localStorage.clear());

  async function open(url = '/browse') {
    const opened = await openPage(url);
    await answerChildren(opened.http, null, [shop]);
    if (url === '/browse') {
      (await requestTo(opened.http, '/api/catalog')).flush(catalogOf());
    } else {
      const name = url.slice('/browse/'.length);
      (await requestTo(opened.http, searchUrl(name, 20))).flush({
        text: name,
        hits: [],
        more: false,
      });
      (await requestTo(opened.http, `/api/catalog/entity?name=${name}`)).flush(entityOf());
      await answerGrid(opened.http);
    }
    const page = opened.harness.fixture.nativeElement as HTMLElement;
    const shown = async () => {
      await settle();
      opened.harness.detectChanges();
    };
    await shown();
    const catalog = await opened.loader.getHarness(MatSidenavHarness);
    return { ...opened, page, shown, catalog };
  }

  it("sets the catalog's width with its splitter's keys, and keeps it", async () => {
    const { page, shown } = await open();
    const splitter = page.querySelector<HTMLElement>('[role=separator]')!;
    const sidenav = page.querySelector<HTMLElement>('mat-sidenav')!;
    expect(splitter.getAttribute('aria-controls')).toBe('gd-catalog');
    expect(splitter.getAttribute('aria-valuenow')).toBe('320');
    expect(sidenav.style.width).toBe('320px');
    const press = async (key: string) => {
      splitter.dispatchEvent(
        new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }),
      );
      await shown();
    };
    await press('ArrowRight');
    expect(splitter.getAttribute('aria-valuenow')).toBe('336');
    expect(sidenav.style.width).toBe('336px');
    await press('Home');
    expect(splitter.getAttribute('aria-valuenow')).toBe('200');
    await press('ArrowLeft');
    expect(splitter.getAttribute('aria-valuenow')).toBe('200');
    await press('End');
    expect(splitter.getAttribute('aria-valuetext')).toBe('640 pixels');
    expect(localStorage.getItem(BrowseLayout.storageKey)).toBe('640');
  });

  it('sets the width by dragging the splitter', async () => {
    const { page, shown } = await open();
    const splitter = page.querySelector<HTMLElement>('[role=separator]')!;
    splitter.dispatchEvent(new MouseEvent('pointerdown', { clientX: 300, button: 0 }));
    splitter.dispatchEvent(new MouseEvent('pointermove', { clientX: 380 }));
    await shown();
    expect(splitter.getAttribute('aria-valuenow')).toBe('400');
    expect(localStorage.getItem(BrowseLayout.storageKey)).toBeNull();
    splitter.dispatchEvent(new MouseEvent('pointermove', { clientX: 1300 }));
    splitter.dispatchEvent(new MouseEvent('pointerup'));
    await shown();
    expect(splitter.getAttribute('aria-valuenow')).toBe('640');
    expect(localStorage.getItem(BrowseLayout.storageKey)).toBe('640');
    splitter.dispatchEvent(new MouseEvent('pointermove', { clientX: 0 }));
    await shown();
    expect(splitter.getAttribute('aria-valuenow')).toBe('640');
  });

  it('drags only with the primary button', async () => {
    const { page, shown } = await open();
    const splitter = page.querySelector<HTMLElement>('[role=separator]')!;
    splitter.dispatchEvent(new MouseEvent('pointerdown', { clientX: 300, button: 2 }));
    splitter.dispatchEvent(new MouseEvent('pointermove', { clientX: 400 }));
    await shown();
    expect(splitter.getAttribute('aria-valuenow')).toBe('320');
  });

  it('starts at the width kept, within its bounds', async () => {
    localStorage.setItem(BrowseLayout.storageKey, '5000');
    const { page } = await open();
    expect(page.querySelector('[role=separator]')?.getAttribute('aria-valuenow')).toBe('640');
  });

  it("ignores a width kept that isn't one", async () => {
    localStorage.setItem(BrowseLayout.storageKey, 'wide');
    const { page } = await open();
    expect(page.querySelector('[role=separator]')?.getAttribute('aria-valuenow')).toBe('320');
  });

  it('opens the catalog over the page on narrow screens, at the start, and closes it once an entity is chosen', async () => {
    wide.next({ matches: false, breakpoints: {} });
    const { http, page, catalog, shown } = await open();
    expect([await catalog.getMode(), await catalog.isOpen()]).toEqual(['over', true]);
    expect(page.querySelector('[role=separator]')).toBeNull();

    (page.querySelector('[role=treeitem]') as HTMLElement).click();
    await answerChildren(http, 'shop', [tableNode('shop.orders')]);
    await shown();
    (page.querySelectorAll('[role=treeitem]')[1] as HTMLElement).click();
    (await requestTo(http, '/api/catalog/entity?name=shop.orders')).flush(entityOf());
    await answerGrid(http);
    await shown();
    expect(TestBed.inject(Router).url).toBe('/browse/shop.orders');
    expect(await catalog.isOpen()).toBe(false);

    const button = page.querySelector('.catalog-bar button')!;
    expect(button.getAttribute('aria-expanded')).toBe('false');
    clickButton(page, 'account_tree Catalog');
    await shown();
    expect(await catalog.isOpen()).toBe(true);
    expect(button.getAttribute('aria-expanded')).toBe('true');
  });

  it("doesn't close the catalog over the page when its search is cleared with Escape", async () => {
    wide.next({ matches: false, breakpoints: {} });
    const { http, page, catalog, shown } = await open();
    const input = page.querySelector<HTMLInputElement>('input[type=search]')!;
    input.value = 'ord';
    input.dispatchEvent(new Event('input'));
    await shown();
    (await requestTo(http, searchUrl('ord'))).flush({ text: 'ord', hits: [], more: false });
    const escape = { key: 'Escape', keyCode: 27, bubbles: true, cancelable: true };
    input.dispatchEvent(new KeyboardEvent('keydown', escape));
    await shown();
    expect(await catalog.isOpen()).toBe(true);
    input.dispatchEvent(new KeyboardEvent('keydown', escape));
    await shown();
    expect(await catalog.isOpen()).toBe(false);
  });

  it("closes the catalog over the page when an entity's name is clicked, the one shown too", async () => {
    wide.next({ matches: false, breakpoints: {} });
    const { http, page, catalog, shown } = await open('/browse/shop.orders');
    clickButton(page, 'account_tree Catalog');
    await shown();
    (page.querySelector('[role=treeitem]') as HTMLElement).click();
    await answerChildren(http, 'shop', [tableNode('shop.orders')]);
    await shown();
    (page.querySelectorAll('[role=treeitem]')[1].querySelector('a') as HTMLElement).click();
    await shown();
    expect(TestBed.inject(Router).url).toBe('/browse/shop.orders');
    expect(await catalog.isOpen()).toBe(false);
  });

  it("keeps the catalog closed on narrow screens when an entity's page is opened, and closes it on choosing that entity again", async () => {
    wide.next({ matches: false, breakpoints: {} });
    const { http, page, catalog, shown } = await open('/browse/shop.orders');
    expect([await catalog.getMode(), await catalog.isOpen()]).toEqual(['over', false]);
    clickButton(page, 'account_tree Catalog');
    await shown();
    (page.querySelector('[role=treeitem]') as HTMLElement).click();
    await answerChildren(http, 'shop', [tableNode('shop.orders')]);
    await shown();
    (page.querySelectorAll('[role=treeitem]')[1] as HTMLElement).click();
    await shown();
    expect(await catalog.isOpen()).toBe(false);
  });
});
