import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { Title } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { fakeBrowserProviders, problemBody, sessionOf, signedOut } from '../testing/auth';
import { requestTo, settle } from '../testing/http';
import { App } from './app';
import { appConfig } from './app.config';
import { ApiClient } from './core/api/api-client';
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

  it("goes to the start from an address it doesn't know", async () => {
    TestBed.createComponent(App);
    const router = TestBed.inject(Router);
    const navigating = router.navigateByUrl('/no/such/page');
    (await requestTo(http, '/api/auth/session')).flush(sessionOf('read'));
    await navigating;
    expect(router.url).toBe('/');
  });
});
