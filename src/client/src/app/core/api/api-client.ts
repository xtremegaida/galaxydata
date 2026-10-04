import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import type { components, paths } from './schema';

/** One of the API's schemas, by name: `Schema<'UserDto'>`. */
export type Schema<Name extends keyof components['schemas']> = components['schemas'][Name];

/** The methods the API's operations take. */
export type ApiMethod = 'get' | 'post' | 'put' | 'delete';

type Operation<P extends keyof paths, M extends ApiMethod> = NonNullable<paths[P][M]>;

/** The API's paths that take a method, as the API writes them: `/api/users/{id}`. */
export type ApiPath<M extends ApiMethod> = {
  [P in keyof paths]: [Operation<P, M>] extends [never] ? never : P;
}[keyof paths];

/** What an operation's request sends, or never when it sends nothing. */
export type RequestBody<M extends ApiMethod, P extends ApiPath<M>> =
  Operation<P, M> extends { requestBody: { content: { 'application/json': infer Body } } }
    ? Body
    : never;

/** What an operation's answer holds when it succeeds: void when it holds nothing (204). */
export type ResponseBody<M extends ApiMethod, P extends ApiPath<M>> = Success<
  Operation<P, M>['responses']
>;

type SuccessStatus = 200 | 201 | 202 | 204;

type Success<Responses> = {
  [Status in keyof Responses & SuccessStatus]: Responses[Status] extends {
    content: { 'application/json': infer Body };
  }
    ? Body
    : void;
}[keyof Responses & SuccessStatus];

/**
 * What a request gives besides its path: the values of the path's parameters (`{id}`), its query's, and its body,
 * each needed as the operation needs it.
 */
export type RequestOptions<M extends ApiMethod, P extends ApiPath<M>> = Pick<
  Operation<P, M>['parameters'],
  'path' | 'query'
> &
  ([RequestBody<M, P>] extends [never] ? { body?: never } : { body: RequestBody<M, P> });

/** Options a request may leave out, or must give. */
type OptionsArgument<Options> = object extends Options ? [options?: Options] : [options: Options];

type ParameterValue = string | number | boolean | null | undefined;

/**
 * The API, typed by its OpenAPI document: each call names an operation by its method and path, as the document
 * does, and gives what it needs.
 *
 * ```ts
 * api.delete('/api/users/{id}', { path: { id }, query: { version } });
 * ```
 */
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);

  get<P extends ApiPath<'get'>>(
    path: P,
    ...[options]: OptionsArgument<RequestOptions<'get', P>>
  ): Observable<ResponseBody<'get', P>> {
    return this.request('GET', path, options);
  }

  post<P extends ApiPath<'post'>>(
    path: P,
    ...[options]: OptionsArgument<RequestOptions<'post', P>>
  ): Observable<ResponseBody<'post', P>> {
    return this.request('POST', path, options);
  }

  put<P extends ApiPath<'put'>>(
    path: P,
    ...[options]: OptionsArgument<RequestOptions<'put', P>>
  ): Observable<ResponseBody<'put', P>> {
    return this.request('PUT', path, options);
  }

  delete<P extends ApiPath<'delete'>>(
    path: P,
    ...[options]: OptionsArgument<RequestOptions<'delete', P>>
  ): Observable<ResponseBody<'delete', P>> {
    return this.request('DELETE', path, options);
  }

  private request<T>(
    method: string,
    path: string,
    options?: { path?: object; query?: object; body?: unknown },
  ): Observable<T> {
    return this.http.request<T>(method, ApiClient.url(path, options?.path), {
      body: options?.body,
      params: ApiClient.params(options?.query),
      responseType: 'json',
    });
  }

  /**
   * The path with its parameters' values in place, each encoded as one segment. An empty value, `.` or `..` would
   * be read as no segment or a step along the path (encoded too), so they are refused.
   */
  static url(path: string, values?: object): string {
    const given = (values ?? {}) as Record<string, ParameterValue>;
    return path.replace(/\{([^}]+)\}/g, (_, name: string) => {
      const value = given[name];
      if (value === undefined || value === null) {
        throw new Error(`${path} needs a value for {${name}}.`);
      }
      const segment = String(value);
      if (segment === '' || segment === '.' || segment === '..') {
        throw new Error(`${path} can't have '${segment}' for {${name}}.`);
      }
      return encodeURIComponent(segment);
    });
  }

  /** The query's parameters, leaving out those without values; a list's values each as one. */
  static params(values?: object): HttpParams {
    let params = new HttpParams();
    for (const [name, value] of Object.entries(values ?? {}) as [
      string,
      ParameterValue | ParameterValue[],
    ][]) {
      for (const item of Array.isArray(value) ? value : [value]) {
        if (item !== undefined && item !== null) {
          params = params.append(name, String(item));
        }
      }
    }
    return params;
  }
}
