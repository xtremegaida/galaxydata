import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { fakeBrowserProviders, sessionOf, signedOut } from '../../../testing/auth';
import { requestTo } from '../../../testing/http';
import { AuthStore, type Session } from './auth-store';
import { allowedTo, passwordGuard, signedInGuard, signedOutGuard } from './guards';

@Component({ template: 'page' })
class Page {}

describe('guards', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'sign-in', canActivate: [signedOutGuard], component: Page },
          { path: 'change-password', canActivate: [passwordGuard], component: Page },
          {
            path: '',
            canActivate: [signedInGuard],
            canActivateChild: [signedInGuard],
            children: [
              { path: '', component: Page },
              { path: 'query/:id', component: Page },
              { path: 'admin', canMatch: [allowedTo('canAdmin')], component: Page },
            ],
          },
        ]),
        fakeBrowserProviders(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Where a navigation to `url` lands, the session being `session`. */
  async function landing(url: string, session: Session): Promise<string> {
    const harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(url);
    (await requestTo(http, '/api/auth/session')).flush(session);
    await navigation;
    return TestBed.inject(Router).url;
  }

  it('sends those not signed in to sign in, to come back', async () => {
    expect(await landing('/query/7', signedOut)).toBe('/sign-in?returnUrl=%2Fquery%2F7');
  });

  it('sends them to sign in alone from the start', async () => {
    expect(await landing('/', signedOut)).toBe('/sign-in');
  });

  it('sends a user whose password must be changed to change it first', async () => {
    expect(await landing('/query/7', sessionOf('read', { mustChangePassword: true }))).toBe(
      '/change-password?returnUrl=%2Fquery%2F7',
    );
  });

  it('lets those signed in in', async () => {
    expect(await landing('/query/7', sessionOf('read'))).toBe('/query/7');
  });

  it('sends those signed in from the sign-in page where they were going', async () => {
    expect(await landing('/sign-in?returnUrl=%2Fquery%2F7', sessionOf('read'))).toBe('/query/7');
  });

  it("sends them to the start rather than to addresses that aren't the application's", async () => {
    expect(await landing('/sign-in?returnUrl=https:%2F%2Fexample.com', sessionOf('read'))).toBe(
      '/',
    );
  });

  it('keeps the password page for those signed in', async () => {
    expect(await landing('/change-password', signedOut)).toBe('/sign-in');
  });

  it('lets in to pages only those who may use them', async () => {
    expect(await landing('/admin', sessionOf('dataManager'))).toBe('/');
  });

  it('lets administrators in to theirs', async () => {
    expect(await landing('/admin', sessionOf('admin'))).toBe('/admin');
  });

  it('sends those not signed in from those pages to sign in, to come back', async () => {
    expect(await landing('/admin?tab=2', signedOut)).toBe('/sign-in?returnUrl=%2Fadmin%3Ftab%3D2');
  });

  it('sends those with a password to change from those pages to change it, to come back', async () => {
    expect(await landing('/admin', sessionOf('admin', { mustChangePassword: true }))).toBe(
      '/change-password?returnUrl=%2Fadmin',
    );
  });

  it('checks again as the pages under it are opened', async () => {
    expect(await landing('/query/7', sessionOf('read'))).toBe('/query/7');
    vi.spyOn(TestBed.inject(AuthStore), 'ensure').mockResolvedValue(signedOut);
    await TestBed.inject(Router).navigateByUrl('/query/8');
    expect(TestBed.inject(Router).url).toBe('/sign-in?returnUrl=%2Fquery%2F8');
  });
});
