import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HarnessLoader } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MatFormFieldHarness } from '@angular/material/form-field/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { fakeBrowserProviders, problemBody, sessionOf, signedOut } from '../../../../testing/auth';
import { requestTo, settle } from '../../../../testing/http';
import { AuthStore } from '../../../core/auth/auth-store';
import { sessionInterceptor } from '../../../core/auth/session.interceptor';
import { SignIn } from './sign-in';

@Component({ template: 'elsewhere' })
class Elsewhere {}

describe('SignIn', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sessionInterceptor])),
        provideHttpClientTesting(),
        provideRouter(
          [
            { path: 'sign-in', component: SignIn },
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

  async function open(url = '/sign-in?returnUrl=%2Fquery%2F7') {
    const harness = await RouterTestingHarness.create(url);
    const loader = TestbedHarnessEnvironment.loader(harness.fixture);
    return { harness, loader, page: harness.routeNativeElement as HTMLElement };
  }

  async function fill(loader: HarnessLoader, userName: string, password: string) {
    await (
      await loader.getHarness(MatInputHarness.with({ selector: '[autocomplete=username]' }))
    ).setValue(userName);
    await (
      await loader.getHarness(MatInputHarness.with({ selector: '[autocomplete=current-password]' }))
    ).setValue(password);
  }

  async function submit(loader: HarnessLoader) {
    await (await loader.getHarness(MatButtonHarness.with({ text: 'Sign in' }))).click();
  }

  it('asks for the user name and password before sending them, from the first missing', async () => {
    const { loader, page } = await open();
    await submit(loader);
    const fields = await loader.getAllHarnesses(MatFormFieldHarness);
    expect(await Promise.all(fields.map((field) => field.getTextErrors()))).toEqual([
      ['Enter your user name'],
      ['Enter your password'],
    ]);
    expect(document.activeElement).toBe(page.querySelector('[autocomplete=username]'));

    await (
      await loader.getHarness(MatInputHarness.with({ selector: '[autocomplete=username]' }))
    ).setValue('ada');
    await submit(loader);
    expect(document.activeElement).toBe(page.querySelector('[autocomplete=current-password]'));
  });

  it('signs in, and goes where the user was going', async () => {
    const { loader } = await open();
    await fill(loader, 'ada', 'a password long enough');
    await submit(loader);
    const request = await requestTo(http, '/api/auth/sign-in', 'POST');
    expect(request.request.body).toEqual({ userName: 'ada', password: 'a password long enough' });
    request.flush(sessionOf());
    await settle();
    expect(TestBed.inject(Router).url).toBe('/query/7');
  });

  it('says why signing in was refused', async () => {
    const { loader, page } = await open();
    await fill(loader, 'ada', 'wrong');
    await submit(loader);
    (await requestTo(http, '/api/auth/sign-in', 'POST')).flush(
      problemBody('locked-out', 'Too many failed sign-ins', {
        detail: 'Try again in 15 minutes, or ask an administrator to unlock the account',
      }),
      { status: 401, statusText: 'Unauthorized' },
    );
    await settle();
    expect(page.querySelector('[role=alert]')?.textContent?.trim()).toContain(
      'Too many failed sign-ins. Try again in 15 minutes, or ask an administrator to unlock the account.',
    );
    expect(TestBed.inject(Router).url).toBe('/sign-in?returnUrl=%2Fquery%2F7');
    expect(TestBed.inject(AuthStore).signedIn()).toBe(false);

    await submit(loader);
    (await requestTo(http, '/api/auth/sign-in', 'POST')).flush(
      problemBody('invalid-credentials', "The user name or password isn't right"),
      { status: 401, statusText: 'Unauthorized' },
    );
    await settle();
    expect(page.querySelector('[role=alert]')?.textContent?.trim()).toBe(
      "errorThe user name or password isn't right.",
    );
  });

  it('puts the server’s errors by field on the fields', async () => {
    const { loader, page } = await open();
    await fill(loader, 'ada', 'x');
    await submit(loader);
    (await requestTo(http, '/api/auth/sign-in', 'POST')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: { userName: ['The UserName field is not right.'] },
      }),
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();
    const [userName] = await loader.getAllHarnesses(MatFormFieldHarness);
    expect(await userName.getTextErrors()).toEqual(['The UserName field is not right.']);
    expect(page.querySelector('[role=alert]')?.textContent?.trim()).toBe('');
    expect(document.activeElement).toBe(page.querySelector('[autocomplete=username]'));
  });

  it('tells a user whose session ended why they are asked to sign in', async () => {
    const auth = TestBed.inject(AuthStore);
    const loaded = auth.ensure();
    http.expectOne('/api/auth/session').flush(sessionOf());
    await loaded;
    auth.ended();
    const { page } = await open();
    expect(page.querySelector('[role=status]')?.textContent?.trim()).toContain(
      'Your session has ended. Sign in again to go on.',
    );
  });

  it("asks for the session again when the server couldn't be reached", async () => {
    const auth = TestBed.inject(AuthStore);
    const loaded = auth.ensure();
    http.expectOne('/api/auth/session').error(new ProgressEvent('error'));
    await loaded;
    const { loader, page } = await open();
    expect(page.querySelector('[role=alert]')?.textContent).toContain("Can't reach the server.");
    await (await loader.getHarness(MatButtonHarness.with({ text: 'Try again' }))).click();
    (await requestTo(http, '/api/auth/session')).flush(sessionOf());
    await settle();
    expect(TestBed.inject(Router).url).toBe('/query/7');
  });

  it('stays on the page when the session asked for again finds no one', async () => {
    const auth = TestBed.inject(AuthStore);
    const loaded = auth.ensure();
    http.expectOne('/api/auth/session').error(new ProgressEvent('error'));
    await loaded;
    const { loader, page } = await open();
    await (await loader.getHarness(MatButtonHarness.with({ text: 'Try again' }))).click();
    (await requestTo(http, '/api/auth/session')).flush(signedOut);
    await settle();
    expect(TestBed.inject(Router).url).toBe('/sign-in?returnUrl=%2Fquery%2F7');
    expect(page.querySelector('[role=alert]')?.textContent?.trim()).toBe('');
  });

  it('shows the password when asked to', async () => {
    const { loader, page } = await open();
    const input = page.querySelector<HTMLInputElement>('[autocomplete=current-password]')!;
    const show = await loader.getHarness(
      MatButtonHarness.with({ selector: '[aria-label="Show password"]' }),
    );
    expect(input.type).toBe('password');
    await show.click();
    expect(input.type).toBe('text');
    expect(await (await show.host()).getAttribute('aria-pressed')).toBe('true');
  });
});
