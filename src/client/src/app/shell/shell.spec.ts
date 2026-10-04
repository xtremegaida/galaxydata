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
import { requestTo, settle } from '../../testing/http';
import type { Session } from '../core/auth/auth-store';
import { AuthStore } from '../core/auth/auth-store';
import { PageLoader } from '../core/browser/page-loader';
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
        { provide: BreakpointObserver, useValue: { observe: () => wide } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function open(session: Session = sessionOf(), url = '/') {
    const auth = TestBed.inject(AuthStore);
    const loaded = auth.ensure();
    http.expectOne('/api/auth/session').flush(session);
    await loaded;
    const harness = await RouterTestingHarness.create(url);
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
    http.expectOne('/api/auth/session').flush(sessionOf());
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
