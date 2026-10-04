import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  ApiClient,
  type ApiPath,
  type RequestBody,
  type RequestOptions,
  type ResponseBody,
  type Schema,
} from './api-client';

describe('ApiClient', () => {
  let api: ApiClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ApiClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('gives what an operation answers', async () => {
    const health: Schema<'HealthResponse'> = {
      status: 'healthy',
      checks: [{ name: 'dataDirectory', status: 'healthy' }],
    };
    const answer = firstValueFrom(api.get('/api/health'));
    const request = http.expectOne('/api/health');
    expect(request.request.method).toBe('GET');
    request.flush(health);
    expect(await answer).toEqual(health);
  });

  it('sends the body', async () => {
    const body: Schema<'CreateUserRequest'> = {
      userName: 'ada',
      role: 'dataManager',
      password: 'a password long enough',
    };
    const answer = firstValueFrom(api.post('/api/users', { body }));
    const request = http.expectOne('/api/users');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(body);
    request.flush({ id: 7 }, { status: 201, statusText: 'Created' });
    expect(await answer).toEqual({ id: 7 });
  });

  it('puts the values of the path in place', async () => {
    const answer = firstValueFrom(
      api.delete('/api/users/{id}', { path: { id: 7 }, query: { version: 3 } }),
    );
    const request = http.expectOne('/api/users/7?version=3');
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
    expect(await answer).toBeNull();

    api
      .get('/api/connections/{id}/snapshots/{snapshotId}', { path: { id: 3, snapshotId: 12 } })
      .subscribe();
    http.expectOne('/api/connections/3/snapshots/12').flush({});
  });

  it("writes each of the path's values as one segment", () => {
    expect(ApiClient.url('/api/connection-kinds/{kind}/convert', { kind: 'a/b c?#%' })).toBe(
      '/api/connection-kinds/a%2Fb%20c%3F%23%25/convert',
    );
  });

  it('refuses a path without its values', () => {
    expect(() => ApiClient.url('/api/users/{id}', {})).toThrow(
      '/api/users/{id} needs a value for {id}.',
    );
    expect(() => ApiClient.url('/api/users/{id}')).toThrow();
  });

  it('refuses path values that would be read as steps along the path', () => {
    for (const kind of ['', '.', '..']) {
      expect(() => ApiClient.url('/api/connection-kinds/{kind}/convert', { kind })).toThrow(
        `/api/connection-kinds/{kind}/convert can't have '${kind}' for {kind}.`,
      );
    }
    expect(ApiClient.url('/api/connection-kinds/{kind}/convert', { kind: '...' })).toBe(
      '/api/connection-kinds/.../convert',
    );
  });

  it('sends the query as given, leaving out what has no value', () => {
    const name = 'xl["Budget 2024"]["Sheet 1"] + a&b=c #%;/?';
    api.get('/api/catalog/entity', { query: { name } }).subscribe();
    const request = http.expectOne((r) => r.url === '/api/catalog/entity');
    expect(
      new URL(request.request.urlWithParams, 'http://localhost').searchParams.get('name'),
    ).toBe(name);
    request.flush({});

    api.get('/api/catalog/tree/search', { query: { text: 'or', take: undefined } }).subscribe();
    http.expectOne('/api/catalog/tree/search?text=or').flush({});
  });

  it("sends each of a list's values", () => {
    expect(ApiClient.params({ a: [1, null, 'b'], c: true }).toString()).toBe('a=1&a=b&c=true');
  });

  it("is typed by the API's document", () => {
    expectTypeOf<ResponseBody<'get', '/api/health'>>().toEqualTypeOf<Schema<'HealthResponse'>>();
    expectTypeOf<ResponseBody<'get', '/api/users'>>().toEqualTypeOf<Schema<'UserDto'>[]>();
    expectTypeOf<ResponseBody<'post', '/api/users'>>().toEqualTypeOf<Schema<'UserDto'>>();
    expectTypeOf<ResponseBody<'delete', '/api/users/{id}'>>().toEqualTypeOf<void>();
    expectTypeOf<RequestBody<'post', '/api/users'>>().toEqualTypeOf<Schema<'CreateUserRequest'>>();
    expectTypeOf<RequestBody<'post', '/api/auth/sign-out'>>().toBeNever();
    expectTypeOf<RequestOptions<'delete', '/api/users/{id}'>['path']>().toEqualTypeOf<{
      id: number;
    }>();
    expectTypeOf<RequestOptions<'get', '/api/catalog/entity'>['query']>().toEqualTypeOf<{
      name: string;
    }>();
    expectTypeOf<'/api/health'>().toExtend<ApiPath<'get'>>();
    expectTypeOf<'/api/health'>().not.toExtend<ApiPath<'post'>>();

    // Calls the document doesn't describe don't compile (they're never made).
    const refused = () => {
      // @ts-expect-error: the path's values are needed.
      api.get('/api/users/{id}');
      // @ts-expect-error: of the path's types.
      api.get('/api/users/{id}', { path: { id: 'seven' } });
      // @ts-expect-error: the body is needed.
      api.post('/api/users');
      // @ts-expect-error: of its schema.
      api.post('/api/users', { body: { userName: 'ada' } });
      // @ts-expect-error: the query's values the operation needs.
      api.get('/api/catalog/tree/search');
      // @ts-expect-error: a body where none is taken.
      api.get('/api/health', { body: {} });
      // @ts-expect-error: a method the path doesn't take.
      api.post('/api/health');
      // @ts-expect-error: a path the API hasn't.
      api.get('/api/nothing');
    };
    expect(refused).toBeTypeOf('function');
  });
});
