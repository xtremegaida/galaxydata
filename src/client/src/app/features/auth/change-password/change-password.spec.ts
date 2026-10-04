import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HarnessLoader } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MatCheckboxHarness } from '@angular/material/checkbox/testing';
import { MatFormFieldHarness } from '@angular/material/form-field/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { MatSnackBarHarness } from '@angular/material/snack-bar/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import {
  FakePageLoader,
  fakeBrowserProviders,
  problemBody,
  sessionOf,
} from '../../../../testing/auth';
import { requestTo, settle } from '../../../../testing/http';
import type { Session } from '../../../core/auth/auth-store';
import { AuthStore } from '../../../core/auth/auth-store';
import { sessionInterceptor } from '../../../core/auth/session.interceptor';
import { PageLoader } from '../../../core/browser/page-loader';
import { ChangePassword } from './change-password';

@Component({ template: 'elsewhere' })
class Elsewhere {}

describe('ChangePassword', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sessionInterceptor])),
        provideHttpClientTesting(),
        provideRouter(
          [
            { path: 'change-password', component: ChangePassword },
            { path: '**', component: Elsewhere },
          ],
          withComponentInputBinding(),
        ),
        fakeBrowserProviders(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function open(session: Session) {
    const auth = TestBed.inject(AuthStore);
    const loaded = auth.ensure();
    http.expectOne('/api/auth/session').flush(session);
    await loaded;
    const harness = await RouterTestingHarness.create('/change-password?returnUrl=%2Fquery%2F7');
    (await requestTo(http, '/api/auth/password-policy')).flush({
      minimumLength: 12,
      maximumLength: 256,
    });
    harness.detectChanges();
    const loader = TestbedHarnessEnvironment.loader(harness.fixture);
    return { harness, loader, page: harness.routeNativeElement as HTMLElement };
  }

  async function fill(loader: HarnessLoader, current: string, next: string, again = next) {
    const inputs = await loader.getAllHarnesses(MatInputHarness);
    await inputs[0].setValue(current);
    await inputs[1].setValue(next);
    await inputs[2].setValue(again);
  }

  async function submit(loader: HarnessLoader) {
    await (await loader.getHarness(MatButtonHarness.with({ text: 'Change password' }))).click();
  }

  async function errors(loader: HarnessLoader) {
    const fields = await loader.getAllHarnesses(MatFormFieldHarness);
    return Promise.all(fields.map((field) => field.getTextErrors()));
  }

  it('asks for a password of their own from a user whose password was set for them', async () => {
    const { loader, page } = await open(sessionOf('read', { mustChangePassword: true }));
    expect(page.querySelector('h1')?.textContent).toBe('Choose a new password');
    expect(page.textContent).toContain('Choose one of your own before you go on.');
    const [, next] = await loader.getAllHarnesses(MatFormFieldHarness);
    expect(await next.getTextHints()).toEqual(['At least 12 characters, without the user name']);
    expect(await loader.getHarnessOrNull(MatButtonHarness.with({ text: 'Cancel' }))).toBeNull();
    expect(await loader.getHarness(MatButtonHarness.with({ text: 'Sign out' }))).toBeTruthy();
  });

  it("checks what it can before sending: the length, the user's name, and the two the same", async () => {
    const { loader, page } = await open(sessionOf());
    await submit(loader);
    expect(await errors(loader)).toEqual([
      ['Enter your current password'],
      ['Enter a new password'],
      ['Enter the new password again'],
    ]);
    expect(document.activeElement).toBe(page.querySelector('[autocomplete=current-password]'));
    await fill(loader, 'the old password', 'short', 'shorter');
    expect(await errors(loader)).toEqual([
      [],
      ['A password needs at least 12 characters'],
      ["The passwords don't match"],
    ]);
    await fill(loader, 'the old password', 'all about ADA now');
    expect(await errors(loader)).toEqual([[], ['A password may not hold the user name'], []]);
    await fill(loader, 'the old password', 'the old password');
    expect(await errors(loader)).toEqual([[], ['It must differ from the current one'], []]);
  });

  it("puts the server's refusals on their fields", async () => {
    const { loader, page } = await open(sessionOf());
    await fill(loader, 'not the password', 'a new password here');
    await submit(loader);
    (await requestTo(http, '/api/auth/change-password', 'POST')).flush(
      problemBody('wrong-password', "The current password isn't right"),
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    await settle();
    expect(await errors(loader)).toEqual([["The current password isn't right"], [], []]);
    expect(document.activeElement).toBe(page.querySelector('[autocomplete=current-password]'));

    await fill(loader, 'the old password', 'a new password here');
    await submit(loader);
    (await requestTo(http, '/api/auth/change-password', 'POST')).flush(
      problemBody('weak-password', "The new password won't do", {
        detail: 'A password needs at least 16 characters',
      }),
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    await settle();
    expect(await errors(loader)).toEqual([[], ['A password needs at least 16 characters'], []]);
  });

  it('changes the password, says so, and goes on', async () => {
    const { harness, loader } = await open(sessionOf('dataManager', { mustChangePassword: true }));
    await fill(loader, 'the old password', 'a new password here');
    await submit(loader);
    const request = await requestTo(http, '/api/auth/change-password', 'POST');
    expect(request.request.body).toEqual({
      currentPassword: 'the old password',
      newPassword: 'a new password here',
    });
    request.flush(sessionOf('dataManager'));
    await settle();
    expect(TestBed.inject(Router).url).toBe('/query/7');
    expect(TestBed.inject(AuthStore).permissions().canEditData).toBe(true);
    const notice = await TestbedHarnessEnvironment.documentRootLoader(harness.fixture).getHarness(
      MatSnackBarHarness,
    );
    expect(await notice.getMessage()).toBe('Your password is changed.');
  });

  it('goes back on cancelling', async () => {
    const { loader } = await open(sessionOf());
    await (await loader.getHarness(MatButtonHarness.with({ text: 'Cancel' }))).click();
    await settle();
    expect(TestBed.inject(Router).url).toBe('/query/7');
  });

  it('lets a user whose password must be changed sign out instead', async () => {
    const { loader } = await open(sessionOf('read', { mustChangePassword: true }));
    await (await loader.getHarness(MatButtonHarness.with({ text: 'Sign out' }))).click();
    (await requestTo(http, '/api/auth/sign-out', 'POST')).flush({ signedIn: false, user: null });
    await settle();
    expect((TestBed.inject(PageLoader) as unknown as FakePageLoader).loads).toEqual(['/sign-in']);
  });

  it('shows the passwords when asked to', async () => {
    const { loader, page } = await open(sessionOf());
    await (await loader.getHarness(MatCheckboxHarness)).check();
    expect(
      [...page.querySelectorAll<HTMLInputElement>('input[matInput]')].map((input) => input.type),
    ).toEqual(['text', 'text', 'text']);
  });
});
