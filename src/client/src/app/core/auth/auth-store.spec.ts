import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import {
  FakePageLoader,
  FakeSessionChannel,
  fakeBrowserProviders,
  problemBody,
  sessionOf,
  signedOut,
} from '../../../testing/auth';
import { settle } from '../../../testing/http';
import { PageLoader } from '../browser/page-loader';
import { AuthStore, type Session } from './auth-store';
import { SessionChannel } from './session-channel';

@Component({ template: '' })
class Blank {}

const grace = sessionOf('read', { id: 2, userName: 'grace', displayName: 'Grace Hopper' });

describe('AuthStore', () => {
  let auth: AuthStore;
  let http: HttpTestingController;
  let router: Router;
  let page: FakePageLoader;
  let channel: FakeSessionChannel;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'sign-in', component: Blank },
          { path: 'change-password', component: Blank },
          {
            // Its session ends as it is opened.
            path: 'query/8',
            canActivate: [
              () => {
                inject(AuthStore).ended();
                return false;
              },
            ],
            component: Blank,
          },
          { path: '**', component: Blank },
        ]),
        fakeBrowserProviders(),
      ],
    });
    auth = TestBed.inject(AuthStore);
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    page = TestBed.inject(PageLoader) as unknown as FakePageLoader;
    channel = TestBed.inject(SessionChannel) as unknown as FakeSessionChannel;
  });

  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
  });

  /** Signs Ada in (or whom `session` names), at `url`. */
  async function signedIn(session = sessionOf(), url = '/') {
    await router.navigateByUrl(url);
    const loaded = auth.ensure();
    http.expectOne('/api/auth/session').flush(session);
    await loaded;
  }

  /** Asks the server for the session again, which answers `session`. */
  async function asked(session: Session) {
    const loaded = auth.load();
    http.expectOne('/api/auth/session').flush(session);
    await loaded;
    await settle();
  }

  /** Signs in as whom `session` names, from the sign-in page, to go back to `returnUrl`. */
  async function signsIn(session: Session, returnUrl = '/query/7') {
    const signingIn = auth.signIn(session.user!.userName, 'a password long enough', returnUrl);
    http.expectOne('/api/auth/sign-in').flush(session);
    await signingIn;
  }

  it('asks for the session once, however many ask', async () => {
    expect(auth.session()).toBeUndefined();
    const first = auth.ensure();
    const second = auth.ensure();
    http.expectOne('/api/auth/session').flush(sessionOf('read'));
    expect(await first).toEqual(sessionOf('read'));
    expect(await second).toEqual(sessionOf('read'));
    expect(auth.signedIn()).toBe(true);
    expect(auth.user()?.userName).toBe('ada');
    expect(auth.permissions()).toEqual({ canRead: true, canEditData: false, canAdmin: false });
    expect(await auth.ensure()).toEqual(sessionOf('read'));
  });

  it("takes no one to be signed in when the session can't be asked for, and says why", async () => {
    const loaded = auth.ensure();
    http.expectOne('/api/auth/session').flush('Error occurred while trying to proxy', {
      status: 504,
      statusText: 'Gateway Timeout',
    });
    expect(await loaded).toEqual(signedOut);
    expect(auth.problem()?.code).toBe('unreachable');
    expect(auth.permissions()).toEqual({ canRead: false, canEditData: false, canAdmin: false });

    await router.navigateByUrl('/sign-in?returnUrl=%2Fquery%2F7');
    await asked(sessionOf());
    expect(auth.signedIn()).toBe(true);
    expect(auth.problem()).toBeNull();
    expect(router.url).toBe('/query/7');
    expect(page.loads).toEqual([]);
  });

  it('keeps what it knew when asking again fails', async () => {
    await signedIn();
    const again = auth.load();
    http.expectOne('/api/auth/session').error(new ProgressEvent('error'));
    expect((await again).signedIn).toBe(true);
    expect(auth.user()?.id).toBe(1);
    expect(auth.problem()?.code).toBe('unreachable');
  });

  it('signs in, tells the other tabs, and goes where the user was going', async () => {
    await signedIn(signedOut, '/sign-in');
    const signingIn = auth.signIn('ada', 'a password long enough', '/query/7');
    const request = http.expectOne('/api/auth/sign-in');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ userName: 'ada', password: 'a password long enough' });
    request.flush(sessionOf());
    await signingIn;
    expect(auth.user()?.displayName).toBe('Ada Lovelace');
    expect(channel.announced).toEqual([1]);
    expect(router.url).toBe('/query/7');
    expect(page.loads).toEqual([]);
  });

  it("goes to the start rather than to addresses that aren't the application's", async () => {
    await signedIn(signedOut, '/sign-in');
    await signsIn(sessionOf(), '/\t/evil.example');
    expect(router.url).toBe('/');
  });

  it('stays signed out when signing in is refused', async () => {
    await signedIn(signedOut, '/sign-in');
    const signingIn = auth.signIn('ada', 'wrong');
    http
      .expectOne('/api/auth/sign-in')
      .flush(problemBody('invalid-credentials', "The user name or password isn't right"), {
        status: 401,
        statusText: 'Unauthorized',
      });
    await expect(signingIn).rejects.toMatchObject({ status: 401 });
    expect(auth.signedIn()).toBe(false);
    expect(channel.announced).toEqual([]);
    expect(router.url).toBe('/sign-in');
  });

  it('asks the user whose session ended to sign in again, and to go on where they were', async () => {
    await signedIn(sessionOf(), '/query/7');
    const epoch = auth.epoch;
    auth.ended();
    await settle();
    expect(auth.epoch).toBeGreaterThan(epoch);
    expect(auth.signedIn()).toBe(false);
    expect(auth.notice()).toBe('Your session has ended. Sign in again to go on.');
    expect(router.url).toBe('/sign-in?returnUrl=%2Fquery%2F7');

    auth.ended();
    await settle();
    expect(router.url).toBe('/sign-in?returnUrl=%2Fquery%2F7');

    await signsIn(sessionOf());
    expect(router.url).toBe('/query/7');
    expect(page.loads).toEqual([]);
    expect(auth.notice()).toBeNull();
  });

  it('keeps where the user was going when the session ends on the password page', async () => {
    await signedIn(sessionOf(), '/change-password?returnUrl=%2Fquery%2F7');
    auth.ended();
    await settle();
    expect(router.url).toBe('/sign-in?returnUrl=%2Fquery%2F7');
  });

  it('brings the user back to the page being opened when its session ended', async () => {
    await signedIn(sessionOf(), '/query/7');
    await router.navigateByUrl('/query/8');
    await settle();
    expect(router.url).toBe('/sign-in?returnUrl=%2Fquery%2F8');
  });

  it("forgets that the server couldn't be reached once its session ends", async () => {
    await signedIn();
    const again = auth.load();
    http.expectOne('/api/auth/session').error(new ProgressEvent('error'));
    await again;
    auth.ended();
    expect(auth.problem()).toBeNull();
  });

  it('starts anew, at the start, when another user signs in after a session ended', async () => {
    await signedIn(sessionOf(), '/query/7');
    auth.ended();
    await signsIn(grace);
    expect(page.loads).toEqual(['/']);
    expect(channel.announced).toEqual([2]);
  });

  it('starts anew where the user was going when they may do less than before', async () => {
    await signedIn(sessionOf('admin'), '/query/7');
    auth.ended();
    await signsIn(sessionOf('read'));
    expect(page.loads).toEqual(['/query/7']);
  });

  it('starts anew when asking finds another user signed in', async () => {
    await signedIn(sessionOf(), '/query/7');
    await asked(grace);
    expect(page.loads).toEqual(['/']);
    expect(auth.user()?.id).toBe(1);
  });

  it('asks to sign in again when asking finds no one signed in, and starts anew for another user', async () => {
    await signedIn(sessionOf(), '/query/7');
    await asked(signedOut);
    expect(router.url).toBe('/sign-in?returnUrl=%2Fquery%2F7');
    expect(auth.notice()).toBe('Your session has ended. Sign in again to go on.');
    await signsIn(grace);
    expect(page.loads).toEqual(['/']);
  });

  it('leaves aside an answer asked for before the session changed', async () => {
    await signedIn(signedOut, '/sign-in');
    const stale = auth.load();
    await signsIn(sessionOf());
    http.expectOne('/api/auth/session').flush(signedOut);
    await stale;
    expect(auth.signedIn()).toBe(true);
    expect(router.url).toBe('/query/7');
  });

  it('signs out, tells the other tabs, and loads the application anew', async () => {
    await signedIn();
    const signingOut = auth.signOut();
    const request = http.expectOne('/api/auth/sign-out');
    expect(request.request.method).toBe('POST');
    request.flush(signedOut);
    await signingOut;
    expect(channel.announced).toEqual([null]);
    expect(page.loads).toEqual(['/sign-in']);
  });

  it('changes the password, tells the other tabs, and goes on', async () => {
    await signedIn(sessionOf('dataManager', { mustChangePassword: true }), '/change-password');
    expect(auth.permissions().canRead).toBe(false);
    const changing = auth.changePassword('old password here', 'new password here', '/query/7');
    const request = http.expectOne('/api/auth/change-password');
    expect(request.request.body).toEqual({
      currentPassword: 'old password here',
      newPassword: 'new password here',
    });
    request.flush(sessionOf('dataManager'));
    await changing;
    expect(auth.user()?.mustChangePassword).toBe(false);
    expect(auth.permissions().canEditData).toBe(true);
    expect(channel.announced).toEqual([1]);
    expect(router.url).toBe('/query/7');
  });

  it('takes the user to change their password when the server says it must be', async () => {
    await signedIn(sessionOf(), '/query/7');
    auth.passwordChangeRequired();
    await settle();
    expect(auth.user()?.mustChangePassword).toBe(true);
    expect(auth.permissions().canRead).toBe(false);
    expect(router.url).toBe('/change-password?returnUrl=%2Fquery%2F7');
  });

  describe('with other tabs', () => {
    it('starts anew when another tab signs someone else in, or signs out', async () => {
      await signedIn();
      channel.hear(2);
      channel.hear(null);
      expect(page.loads).toEqual(['/', '/sign-in']);
      expect(page.reloads).toBe(0);
    });

    it('reads the session again when the same user signs in or changes their password there', async () => {
      await signedIn(sessionOf('read', { mustChangePassword: true }), '/change-password');
      channel.hear(1);
      http.expectOne('/api/auth/session').flush(sessionOf('read'));
      await settle();
      expect(auth.user()?.mustChangePassword).toBe(false);
      expect(page.loads).toEqual([]);
    });

    it('goes on where it was going when the user whose session ended signs in there', async () => {
      await signedIn(sessionOf(), '/query/7');
      auth.ended();
      await settle();
      channel.hear(1);
      http.expectOne('/api/auth/session').flush(sessionOf());
      await settle();
      expect(router.url).toBe('/query/7');
      expect(page.loads).toEqual([]);
    });

    it('loads anew where it is when it held no one’s and someone signs in there', async () => {
      await signedIn(signedOut, '/sign-in?returnUrl=%2Fquery%2F7');
      channel.hear(null);
      channel.hear(2);
      expect(page.reloads).toBe(1);
      expect(page.loads).toEqual([]);
    });
  });

  describe('coming back', () => {
    it('asks again when back from the browser’s back-forward cache', async () => {
      await signedIn();
      window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
      http.expectOne('/api/auth/session').flush(sessionOf());
      window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: false }));
      http.expectNone('/api/auth/session');
    });

    it('asks again when in view after a while, not at once', async () => {
      vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
      const now = vi.spyOn(Date, 'now').mockReturnValue(1_000_000);
      await signedIn();
      document.dispatchEvent(new Event('visibilitychange'));
      http.expectNone('/api/auth/session');
      now.mockReturnValue(1_000_000 + 30_000);
      document.dispatchEvent(new Event('visibilitychange'));
      http.expectOne('/api/auth/session').flush(grace);
      await settle();
      expect(page.loads).toEqual(['/']);
    });
  });
});
