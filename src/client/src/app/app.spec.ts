import { BreakpointObserver } from '@angular/cdk/layout';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { Title } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { firstValueFrom, of } from 'rxjs';
import { fakeBrowserProviders, problemBody, sessionOf, signedOut } from '../testing/auth';
import { answerChildren, catalogOf, entityOf, searchUrl, sourceNode } from '../testing/catalog';
import { answerGrid } from '../testing/browse';
import { requestTo, settle } from '../testing/http';
import { App } from './app';
import { BrowseGrid } from './features/browse/grid/browse-grid';
import { appConfig } from './app.config';
import { ApiClient } from './core/api/api-client';
import { keepFocus } from './core/browser/keep-focus';
import { CatalogVersion } from './core/catalog/catalog-version';

describe('App', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [...appConfig.providers, provideHttpClientTesting(), fakeBrowserProviders()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('signs in, shows the start, and asks to sign in again once the session ends', async () => {
    const fixture = TestBed.createComponent(App);
    const router = TestBed.inject(Router);
    const title = TestBed.inject(Title);
    const loader = TestbedHarnessEnvironment.loader(fixture);
    const page = fixture.nativeElement as HTMLElement;

    const starting = router.navigateByUrl('/');
    (await requestTo(http, '/api/auth/session')).flush(signedOut);
    await starting;
    await fixture.whenStable();
    expect(router.url).toBe('/sign-in');
    expect(title.getTitle()).toBe('Sign in · GalaxyData');
    expect(document.activeElement).toBe(document.body);

    await (
      await loader.getHarness(MatInputHarness.with({ selector: '[autocomplete=username]' }))
    ).setValue('ada');
    await (
      await loader.getHarness(MatInputHarness.with({ selector: '[autocomplete=current-password]' }))
    ).setValue('a password long enough');
    await (await loader.getHarness(MatButtonHarness.with({ text: 'Sign in' }))).click();
    (await requestTo(http, '/api/auth/sign-in', 'POST')).flush(sessionOf(), {
      headers: { 'X-Catalog-Version': 'v1' },
    });
    await settle();
    await fixture.whenStable();
    expect(router.url).toBe('/');
    expect(title.getTitle()).toBe('Start · GalaxyData');
    expect(page.querySelector('h1')?.textContent).toBe('Welcome, Ada Lovelace');
    expect(document.activeElement).toBe(page.querySelector('gd-shell main'));
    expect(TestBed.inject(CatalogVersion).version()).toBe('v1');

    const refused = firstValueFrom(TestBed.inject(ApiClient).get('/api/catalog')).catch(
      (error: unknown) => error,
    );
    (await requestTo(http, '/api/catalog')).flush(problemBody('unauthenticated', 'Unauthorized'), {
      status: 401,
      statusText: 'Unauthorized',
    });
    expect(await refused).toMatchObject({ status: 401 });
    await settle();
    await fixture.whenStable();
    expect(router.url).toBe('/sign-in');
    expect(page.querySelector('[role=status]')?.textContent).toContain(
      'Your session has ended. Sign in again to go on.',
    );
  });

  it("keeps the administrators' pages from others", async () => {
    TestBed.createComponent(App);
    const router = TestBed.inject(Router);
    const navigating = router.navigateByUrl('/admin/users');
    (await requestTo(http, '/api/auth/session')).flush(sessionOf('dataManager'));
    await navigating;
    expect(router.url).toBe('/');
  });

  it("loads the administrators' pages for administrators", async () => {
    const fixture = TestBed.createComponent(App);
    const router = TestBed.inject(Router);
    const navigating = router.navigateByUrl('/admin/users');
    (await requestTo(http, '/api/auth/session')).flush(sessionOf('admin'));
    await navigating;
    expect(router.url).toBe('/admin/users');
    (await requestTo(http, '/api/users')).flush([]);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('h1')?.textContent).toBe('Users');
  });

  it("goes to the start from an address it doesn't know", async () => {
    TestBed.createComponent(App);
    const router = TestBed.inject(Router);
    const navigating = router.navigateByUrl('/no/such/page');
    (await requestTo(http, '/api/auth/session')).flush(sessionOf('read'));
    await navigating;
    expect(router.url).toBe('/');
  });
});

describe('App, browsing', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        ...appConfig.providers,
        provideHttpClientTesting(),
        fakeBrowserProviders(),
        {
          provide: BreakpointObserver,
          useValue: { observe: () => of({ matches: true, breakpoints: {} }) },
        },
      ],
    });
    // The grid has tests of its own; in the whole application, jsdom takes seconds to work out its styles.
    TestBed.overrideComponent(BrowseGrid, { set: { template: '', imports: [] } });
    http = TestBed.inject(HttpTestingController);
  });

  it('lets readers browse, leaving focus where it is for navigations that say so', async () => {
    const fixture = TestBed.createComponent(App);
    const router = TestBed.inject(Router);
    const page = fixture.nativeElement as HTMLElement;
    const navigating = router.navigateByUrl('/browse');
    (await requestTo(http, '/api/auth/session')).flush(sessionOf('read'));
    await navigating;
    expect(router.url).toBe('/browse');
    await answerChildren(http, null, [sourceNode('shop')]);
    (await requestTo(http, '/api/catalog')).flush(catalogOf());
    await settle();
    await fixture.whenStable();
    expect(page.querySelector('h1')?.textContent).toBe('Browse');

    const tree = page.querySelector<HTMLElement>('[role=tree]')!;
    tree.focus();
    const opening = router.navigate(['/browse', 'shop.orders'], { state: keepFocus });
    (await requestTo(http, searchUrl('shop.orders', 20))).flush({
      text: 'shop.orders',
      hits: [],
      more: false,
    });
    (await requestTo(http, '/api/catalog/entity?name=shop.orders')).flush(entityOf());
    await answerGrid(http);
    expect(await opening).toBe(true);
    await settle();
    await fixture.whenStable();
    expect(page.querySelector('h1')?.textContent).toBe('shop.orders');
    expect(document.activeElement).toBe(tree);

    await router.navigateByUrl('/browse');
    (await requestTo(http, '/api/catalog')).flush(catalogOf());
    await settle();
    await fixture.whenStable();
    expect(document.activeElement).toBe(page.querySelector('gd-shell main'));
  });
});
