import {
  HttpErrorResponse,
  HttpXsrfTokenExtractor,
  type HttpEvent,
  type HttpInterceptorFn,
  type HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable, catchError, from, switchMap, throwError } from 'rxjs';
import { ProblemCode, problemOf } from '../api/problem';
import { AuthStore } from './auth-store';

/** The cookie the server gives the anti-forgery token in, and the header requests send it back in. */
export const xsrfCookie = 'XSRF-TOKEN';
export const xsrfHeader = 'X-XSRF-TOKEN';

/**
 * Whether a request is the session's business: the API's, but its public part (dashboards anyone may see, embedded
 * in other sites), which is no one's.
 */
export function isSessionRequest(url: string): boolean {
  return url.startsWith('/api/') && !url.startsWith('/api/public/');
}

/**
 * The session's problems, whichever request meets them:
 * - no one is signed in (any more): the user signs in again, and comes back;
 * - the password must be changed first: its page;
 * - an anti-forgery token that won't do (who is signed in changed since it was given): a new one is asked for, and
 *   the request sent again, once.
 *
 * What a request sent under a session that has changed since (the user signed in again) meets is about that
 * session, and left alone. The errors go on to the request's caller all the same.
 */
export const sessionInterceptor: HttpInterceptorFn = (request, next) => {
  // Without making the session's store, whose tabs and checks a public page has nothing to do with.
  if (!isSessionRequest(request.url)) {
    return next(request);
  }
  const auth = inject(AuthStore);
  const tokens = inject(HttpXsrfTokenExtractor);
  const send = (sent: HttpRequest<unknown>, resending: boolean): Observable<HttpEvent<unknown>> => {
    const epoch = auth.epoch;
    return next(sent).pipe(
      catchError((error: unknown) => {
        if (!(error instanceof HttpErrorResponse) || epoch !== auth.epoch) {
          return throwError(() => error);
        }
        const code = problemOf(error).code;
        if (error.status === 401 && code === ProblemCode.unauthenticated) {
          auth.ended();
        } else if (error.status === 403 && code === ProblemCode.passwordChangeRequired) {
          auth.passwordChangeRequired();
        } else if (error.status === 400 && code === ProblemCode.xsrfTokenInvalid && !resending) {
          // The token is in a header the XSRF interceptor set before this one; it doesn't set it again.
          return from(auth.load()).pipe(
            switchMap(() => {
              const token = tokens.getToken();
              const headers =
                token == null
                  ? sent.headers.delete(xsrfHeader)
                  : sent.headers.set(xsrfHeader, token);
              return send(sent.clone({ headers }), true);
            }),
          );
        }
        return throwError(() => error);
      }),
    );
  };
  return send(request, false);
};
