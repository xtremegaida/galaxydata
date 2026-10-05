import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MatNavListHarness } from '@angular/material/list/testing';
import { MatMenuHarness } from '@angular/material/menu/testing';
import { MatSidenavHarness } from '@angular/material/sidenav/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BehaviorSubject } from 'rxjs';
import { FakePageLoader, fakeBrowserProviders, sessionOf } from '../../testing/auth';
import { FakeChangesChannel, answerChanges, changeOf, setOf } from '../../testing/changes';
import { requestTo, settle } from '../../testing/http';
import type { Session } from '../core/auth/auth-store';
import { AuthStore } from '../core/auth/auth-store';
import { PageLoader } from '../core/browser/page-loader';
import { ChangesChannel, PendingChanges } from '../core/changes/pending-changes';
import { Start } from '../features/start/start';
import { navItemsFor, type NavItem } from './nav-items';
import { Shell } from './shell';

@Component({ template: 'elsewhere' })
class Elsewhere {}

describe('Shell', () => {
  let http: HttpTestingController;
  const wide = new BehaviorSubject<BreakpointState>({ matches: true, breakpoints: {} });

  beforeEach(() => {
    wide.next({ matches: true, breakpoints: {} });
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'change-password', component: Elsewhere },
          {
            path: '',
            component: Shell,
            children: [
              { path: '', component: Start },
              { path: 'query/:id', component: Elsewhere },
            ],
          },
        ]),
        fakeBrowserProviders(),
        { provide: ChangesChannel, useClass: FakeChangesChannel },
        { provide: BreakpointObserver, useValue: { observe: () => wide } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function open(session: Session = sessionOf(), url = '/', changes = setOf()) {
    const auth = TestBed.inject(AuthStore);
    const loaded = auth.ensure();
    http.expectOne('/api/auth/session').flush(session);
    await loaded;
    const harness = await RouterTestingHarness.create(url);
    if (session.user?.permissions.canEditData) {
      await answerChanges(http, changes);
    }
    return {
      harness,
      loader: TestbedHarnessEnvironment.loader(harness.fixture),
      root: TestbedHarnessEnvironment.documentRootLoader(harness.fixture),
      page: harness.fixture.nativeElement as HTMLElement,
    };
  }

  it('greets the user, and says what they may do', async () => {
    const { page } = await open(sessionOf('dataManager'));
    expect(page.querySelector('h1')?.textContent).toBe('Welcome, Ada Lovelace');
    expect(page.textContent).toContain('You may browse, query and change the data.');
  });

  it('leads to the pages, the one open marked', async () => {
    const { loader, page } = await open(sessionOf('read'));
    const items = await (await loader.getHarness(MatNavListHarness)).getItems();
    expect(await Promise.all(items.map((item) => item.getTitle()))).toEqual(['Start', 'Browse']);
    expect(await (await items[0].host()).getAttribute('aria-current')).toBe('page');
    const groups = [...page.querySelectorAll('[role=navigation] [role=group]')];
    expect(groups.map((group) => group.getAttribute('aria-labelledby'))).toEqual([null]);
    const landmarks = page.querySelectorAll('nav, [role=navigation]');
    expect([...landmarks].map((landmark) => landmark.getAttribute('aria-label'))).toEqual([
      'Pages',
    ]);
  });

  it("says who is signed in, as what, in the user's menu", async () => {
    const { loader } = await open(sessionOf('dataManager'));
    const menu = await loader.getHarness(MatMenuHarness.with({ triggerText: /Ada Lovelace/ }));
    expect(await (await menu.host()).getAttribute('aria-label')).toBe('Account: Ada Lovelace');
    await menu.open();
    const panel = document.querySelector('.mat-mdc-menu-panel')!;
    expect(panel.querySelector('.display-name')?.textContent).toBe('Ada Lovelace');
    expect(panel.querySelector('.detail')?.textContent).toBe('ada · Data manager');
  });

  it("names the user by their user name when they haven't another", async () => {
    const { page } = await open(sessionOf('read', { displayName: null }));
    expect(page.querySelector('h1')?.textContent).toBe('Welcome, ada');
  });

  it('changes the password, to come back', async () => {
    const { loader } = await open(sessionOf(), '/query/7');
    const menu = await loader.getHarness(MatMenuHarness.with({ triggerText: /Ada/ }));
    await menu.clickItem({ text: /Change password/ });
    await settle();
    expect(TestBed.inject(Router).url).toBe('/change-password?returnUrl=%2Fquery%2F7');
  });

  it('has its bar as the banner, and a way past it to the page', async () => {
    const { loader, page } = await open();
    expect(page.querySelector('header mat-toolbar')).not.toBeNull();
    const skip = await loader.getHarness(MatButtonHarness.with({ text: 'Skip to the page' }));
    await skip.click();
    expect(document.activeElement).toBe(page.querySelector('main'));
  });

  it('signs out', async () => {
    const { loader } = await open();
    const menu = await loader.getHarness(MatMenuHarness.with({ triggerText: /Ada/ }));
    await menu.clickItem({ text: /Sign out/ });
    (await requestTo(http, '/api/auth/sign-out', 'POST')).flush({ signedIn: false, user: null });
    await settle();
    expect((TestBed.inject(PageLoader) as unknown as FakePageLoader).loads).toEqual(['/sign-in']);
  });

  it('counts the pending changes of those who change data, and lists them in a drawer', async () => {
    const { page, harness } = await open(
      sessionOf('dataManager'),
      '/',
      setOf([changeOf(), changeOf({ id: 2, key: ['1002'], rowId: '["1002"]' })]),
    );
    harness.detectChanges();
    const button = page.querySelector<HTMLElement>('button[aria-controls=gd-changes]')!;
    expect(button.getAttribute('aria-label')).toBe('Pending changes: 2 rows');
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(page.querySelector('.mat-badge-content')?.textContent).toBe('2');
    button.click();
    // The drawer is loaded as it first opens.
    for (let turn = 0; turn < 100 && !page.querySelector('#gd-changes h2'); turn++) {
      await settle(1);
      harness.detectChanges();
    }
    expect(button.getAttribute('aria-expanded')).toBe('true');
    const drawer = page.querySelector<HTMLElement>('#gd-changes')!;
    expect(drawer.querySelector('h2')?.textContent).toBe('Pending changes');
    expect(drawer.querySelector('.summary')?.textContent?.trim()).toBe(
      '2 rows changed, not committed.',
    );
    // The keyboard goes to it, and back to its button as it closes.
    await settle();
    expect(document.activeElement).toBe(drawer.querySelector('h2'));
    drawer.querySelector<HTMLElement>('button[aria-label=Close]')!.click();
    harness.detectChanges();
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(button);
  });

  it('counts one row, and shows no count for none', async () => {
    const { page, harness } = await open(sessionOf('dataManager'), '/', setOf([changeOf()]));
    harness.detectChanges();
    const button = page.querySelector<HTMLElement>('button[aria-controls=gd-changes]')!;
    expect(button.getAttribute('aria-label')).toBe('Pending changes: 1 row');
    TestBed.inject(PendingChanges).revert('shop.orders', { key: ['1001'], rowId: '["1001"]' });
    harness.detectChanges();
    expect(button.getAttribute('aria-label')).toBe('Pending changes: none');
    expect(page.querySelector('.mat-badge')?.classList).toContain('mat-badge-hidden');
    (await requestTo(http, '/api/changes/ops', 'POST')).flush(setOf([], 2));
  });

  it('has no pending changes for readers', async () => {
    const { page } = await open(sessionOf('read'));
    expect(page.querySelector('button[aria-controls=gd-changes]')).toBeNull();
    expect(page.querySelector('#gd-changes')).toBeNull();
  });

  it('keeps the navigation beside the page on wide screens, and closes it when asked', async () => {
    const { loader } = await open();
    const navigation = await loader.getHarness(MatSidenavHarness);
    expect(await navigation.getMode()).toBe('side');
    expect(await navigation.isOpen()).toBe(true);
    await (
      await loader.getHarness(MatButtonHarness.with({ selector: '[aria-label=Navigation]' }))
    ).click();
    expect(await navigation.isOpen()).toBe(false);
  });

  it('opens the navigation over the page on narrow screens, and closes it once a page is chosen', async () => {
    wide.next({ matches: false, breakpoints: {} });
    const { loader } = await open();
    const navigation = await loader.getHarness(MatSidenavHarness);
    const toggle = await loader.getHarness(
      MatButtonHarness.with({ selector: '[aria-label=Navigation]' }),
    );
    expect(await navigation.getMode()).toBe('over');
    expect(await navigation.isOpen()).toBe(false);
    expect(await (await toggle.host()).getAttribute('aria-expanded')).toBe('false');
    await toggle.click();
    expect(await navigation.isOpen()).toBe(true);
    const [start] = await (await loader.getHarness(MatNavListHarness)).getItems();
    await start.click();
    await settle();
    expect(await navigation.isOpen()).toBe(false);
  });
});

describe('Shell, as the screen changes width', () => {
  const wide = new BehaviorSubject<BreakpointState>({ matches: true, breakpoints: {} });

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: '', component: Shell, children: [{ path: '', component: Start }] }]),
        fakeBrowserProviders(),
        { provide: BreakpointObserver, useValue: { observe: () => wide } },
      ],
    });
  });

  it('moves the navigation over the page when the screen narrows, and back when it widens', async () => {
    const http = TestBed.inject(HttpTestingController);
    const loaded = TestBed.inject(AuthStore).ensure();
    http.expectOne('/api/auth/session').flush(sessionOf('read'));
    await loaded;
    const harness = await RouterTestingHarness.create('/');
    const navigation = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      MatSidenavHarness,
    );
    expect([await navigation.getMode(), await navigation.isOpen()]).toEqual(['side', true]);
    wide.next({ matches: false, breakpoints: {} });
    expect([await navigation.getMode(), await navigation.isOpen()]).toEqual(['over', false]);
    wide.next({ matches: true, breakpoints: {} });
    expect([await navigation.getMode(), await navigation.isOpen()]).toEqual(['side', true]);
    const content = harness.fixture.nativeElement.querySelector(
      'mat-sidenav-content',
    ) as HTMLElement;
    wide.next({ matches: false, breakpoints: {} });
    await navigation.getMode();
    expect(content.style.marginLeft).toBe('');
  });
});

describe('navItemsFor', () => {
  const items: NavItem[] = [
    { label: 'Start', icon: 'home', link: '/' },
    { label: 'Changes', icon: 'edit', link: '/changes', needs: 'canEditData' },
    { label: 'Users', icon: 'group', link: '/admin/users', needs: 'canAdmin', section: 'Admin' },
    { label: 'Audit', icon: 'history', link: '/admin/audit', needs: 'canAdmin', section: 'Admin' },
  ];

  const shown = (permissions: Parameters<typeof navItemsFor>[0]) =>
    navItemsFor(permissions, items).map((section) => [
      section.label,
      section.items.map((item) => item.label),
    ]);

  it('gives the pages a user may open, by section', () => {
    expect(shown({ canRead: true, canEditData: false, canAdmin: false })).toEqual([
      [null, ['Start']],
    ]);
    expect(shown({ canRead: true, canEditData: true, canAdmin: false })).toEqual([
      [null, ['Start', 'Changes']],
    ]);
    expect(shown({ canRead: true, canEditData: true, canAdmin: true })).toEqual([
      [null, ['Start', 'Changes']],
      ['Admin', ['Users', 'Audit']],
    ]);
  });

  it("puts administrators' pages under their heading", () => {
    expect(navItemsFor({ canRead: true, canEditData: true, canAdmin: true }).at(-1)).toMatchObject({
      label: 'Administration',
      items: [{ label: 'Users' }, { label: 'Connections' }],
    });
  });
});
