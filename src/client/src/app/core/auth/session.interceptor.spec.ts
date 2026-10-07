import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
  withXsrfConfiguration,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { problemBody, sessionOf } from '../../../testing/auth';
import { AuthStore } from './auth-store';
import { sessionInterceptor, xsrfCookie, xsrfHeader } from './session.interceptor';

describe('sessionInterceptor', () => {
  let http: HttpTestingController;
  let client: HttpClient;
  const auth = {
    epoch: 0,
    ended: vi.fn(),
    passwordChangeRequired: vi.fn(),
    load: vi.fn(async () => {
      document.cookie = `${xsrfCookie}=new-token; path=/`;
      return sessionOf();
    }),
  };

  beforeEach(() => {
    vi.clearAllMocks();
    auth.epoch = 0;
    document.cookie = `${xsrfCookie}=old-token; path=/`;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(
          withXsrfConfiguration({ cookieName: xsrfCookie, headerName: xsrfHeader }),
          withInterceptors([sessionInterceptor]),
        ),
        provideHttpClientTesting(),
        { provide: AuthStore, useValue: auth },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
  });

  afterEach(() => {
    http.verify();
    document.cookie = `${xsrfCookie}=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT`;
  });

  /** Fails a request with a problem, and gives what its caller got. */
  async function failed(url: string, status: number, code: string, method = 'GET') {
    const answer = firstValueFrom(client.request(method, url, { body: {} }));
    http.expectOne(url).flush(problemBody(code, 'A problem'), { status, statusText: 'Problem' });
    return answer.then(
      () => null,
      (error: unknown) => error,
    );
  }

  it("leaves public dashboards' requests alone, without the session's store", async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([sessionInterceptor])),
        provideHttpClientTesting(),
        {
          provide: AuthStore,
          useFactory: () => {
            throw new Error('The session in a public page');
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    const answer = firstValueFrom(client.get('/api/public/dashboards/x')).catch(
      (error: unknown) => error,
    );
    http.expectOne('/api/public/dashboards/x').flush(problemBody('unauthenticated', 'No'), {
      status: 401,
      statusText: 'No',
    });
    expect(await answer).toBeInstanceOf(HttpErrorResponse);
  });

  it('asks the user to sign in again when no one is signed in any more', async () => {
    expect(await failed('/api/users', 401, 'unauthenticated')).toMatchObject({ status: 401 });
    expect(auth.ended).toHaveBeenCalledOnce();
  });

  it("leaves sign-ins' refusals to the sign-in", async () => {
    await failed('/api/auth/sign-in', 401, 'invalid-credentials', 'POST');
    await failed('/api/auth/sign-in', 401, 'locked-out', 'POST');
    expect(auth.ended).not.toHaveBeenCalled();
  });

  it('takes the user to change their password when it must be first', async () => {
    expect(await failed('/api/catalog', 403, 'password-change-required')).toMatchObject({
      status: 403,
    });
    expect(auth.passwordChangeRequired).toHaveBeenCalledOnce();
  });

  it('leaves refusals to their callers', async () => {
    await failed('/api/users', 403, 'forbidden');
    expect(auth.load).not.toHaveBeenCalled();
    expect(auth.ended).not.toHaveBeenCalled();
  });

  it('leaves alone what requests sent under a session since changed meet', async () => {
    const late = firstValueFrom(client.get('/api/catalog')).catch((error: unknown) => error);
    const later = firstValueFrom(client.get('/api/users')).catch((error: unknown) => error);
    auth.epoch = 1;
    http
      .expectOne('/api/catalog')
      .flush(problemBody('unauthenticated', 'No'), { status: 401, statusText: 'Unauthorized' });
    http
      .expectOne('/api/users')
      .flush(problemBody('password-change-required', 'No'), { status: 403, statusText: 'No' });
    expect(await late).toMatchObject({ status: 401 });
    expect(await later).toMatchObject({ status: 403 });
    expect(auth.ended).not.toHaveBeenCalled();
    expect(auth.passwordChangeRequired).not.toHaveBeenCalled();
  });

  it("leaves other addresses' answers alone", async () => {
    await failed('/assets/x.json', 401, 'unauthenticated');
    expect(auth.ended).not.toHaveBeenCalled();
  });

  it('sends a request again with a new anti-forgery token, once', async () => {
    const answer = firstValueFrom(client.post('/api/users/1/unlock', {}));
    const first = http.expectOne('/api/users/1/unlock');
    expect(first.request.headers.get(xsrfHeader)).toBe('old-token');
    first.flush(problemBody('xsrf-token-invalid', 'The token won’t do'), {
      status: 400,
      statusText: 'Bad Request',
    });
    await Promise.resolve();
    const second = http.expectOne('/api/users/1/unlock');
    expect(auth.load).toHaveBeenCalledOnce();
    expect(second.request.headers.get(xsrfHeader)).toBe('new-token');
    second.flush({ id: 1 });
    expect(await answer).toEqual({ id: 1 });

    const again = firstValueFrom(client.post('/api/users/1/unlock', {})).catch(
      (error: unknown) => error,
    );
    http
      .expectOne('/api/users/1/unlock')
      .flush(problemBody('xsrf-token-invalid', 'No'), { status: 400, statusText: 'Bad Request' });
    await Promise.resolve();
    http
      .expectOne('/api/users/1/unlock')
      .flush(problemBody('xsrf-token-invalid', 'No'), { status: 400, statusText: 'Bad Request' });
    expect(await again).toMatchObject({ status: 400 });
    expect(auth.load).toHaveBeenCalledTimes(2);
  });

  it('deals with what a request sent again meets as with any', async () => {
    const answer = firstValueFrom(client.post('/api/users/1/unlock', {})).catch(
      (error: unknown) => error,
    );
    http
      .expectOne('/api/users/1/unlock')
      .flush(problemBody('xsrf-token-invalid', 'No'), { status: 400, statusText: 'Bad Request' });
    await Promise.resolve();
    http.expectOne('/api/users/1/unlock').flush(problemBody('unauthenticated', 'Unauthorized'), {
      status: 401,
      statusText: 'Unauthorized',
    });
    expect(await answer).toMatchObject({ status: 401 });
    expect(auth.ended).toHaveBeenCalledOnce();
  });
});
