import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { isSessionProblem, problemMessage, problemOf } from './problem';

describe('problemOf', () => {
  it("reads the API's problems", () => {
    const problem = problemOf(
      new HttpErrorResponse({
        status: 422,
        error: {
          code: 'weak-password',
          title: "The new password won't do",
          detail: 'A password needs at least 12 characters',
          traceId: '00-abc',
        },
      }),
    );
    expect(problem).toMatchObject({
      status: 422,
      code: 'weak-password',
      title: "The new password won't do",
      detail: 'A password needs at least 12 characters',
      traceId: '00-abc',
    });
    expect(problem.body?.['code']).toBe('weak-password');
    expect(problemMessage(problem)).toBe(
      "The new password won't do. A password needs at least 12 characters.",
    );
  });

  it("reads requests' errors by field", () => {
    const problem = problemOf(
      new HttpErrorResponse({
        status: 400,
        error: {
          code: 'invalid-request',
          title: 'One or more validation errors occurred.',
          errors: { userName: ['The UserName field is required.'] },
        },
      }),
    );
    expect(problem.errors).toEqual({ userName: ['The UserName field is required.'] });
    expect(problemMessage(problem)).toBe('One or more validation errors occurred.');
  });

  it('takes no answer, or a proxy’s, as the server out of reach', () => {
    for (const error of [
      new HttpErrorResponse({ status: 0, error: new ProgressEvent('error') }),
      new HttpErrorResponse({ status: 504, error: 'Error occurred while trying to proxy' }),
    ]) {
      expect(problemOf(error)).toMatchObject({
        code: 'unreachable',
        title: "Can't reach the server",
      });
    }
  });

  it('keeps the codes of the API’s own failures as gateway', () => {
    const problem = problemOf(
      new HttpErrorResponse({
        status: 502,
        error: { code: 'source-unavailable', title: "Can't reach the source shop" },
      }),
    );
    expect(problem.code).toBe('source-unavailable');
  });

  it('gives answers without problems the codes of their statuses', () => {
    expect(problemOf(new HttpErrorResponse({ status: 404, error: null }))).toMatchObject({
      code: 'not-found',
      title: 'The request failed (404)',
    });
    expect(problemOf(new HttpErrorResponse({ status: 418, error: 'teapot' })).code).toBe(
      'status-418',
    );
  });

  it('says how long to wait after too many requests', () => {
    const problem = problemOf(
      new HttpErrorResponse({
        status: 429,
        headers: new HttpHeaders({ 'Retry-After': '12' }),
        error: { code: 'too-many-requests', title: 'Too Many Requests' },
      }),
    );
    expect(problem.retryAfter).toBe(12);
    expect(problemMessage(problem)).toBe('Too many requests: try again in 12 seconds.');
  });

  it("takes the client's own failures as such", () => {
    expect(problemOf(new TypeError('x is undefined'))).toMatchObject({
      status: 0,
      code: 'client-error',
      detail: 'x is undefined',
    });
  });

  it("tells the session's problems", () => {
    const session = (code: string) => isSessionProblem({ status: 401, code, title: '' });
    expect(session('unauthenticated')).toBe(true);
    expect(session('password-change-required')).toBe(true);
    expect(session('forbidden')).toBe(false);
  });
});
