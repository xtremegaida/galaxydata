import { HttpErrorResponse } from '@angular/common/http';

/**
 * What went wrong with a request, as the API's problem details say (RFC 9457, with the API's `code`), or as the
 * client tells it when there is no answer to read.
 */
export interface Problem {
  /** The answer's status; 0 when there was none. */
  readonly status: number;
  /** What the problem is, to tell problems apart by: the API's codes, or `unreachable`. */
  readonly code: string;
  readonly title: string;
  readonly detail?: string;
  /** What is wrong with each field of the request, by its name as the API's JSON writes it (`invalid-request`). */
  readonly errors?: Readonly<Record<string, readonly string[]>>;
  /** The request's trace, to find it in the server's log. */
  readonly traceId?: string;
  /** How many seconds to wait before asking again (429). */
  readonly retryAfter?: number;
  /** The whole problem as answered, for what some problems add (`diagnostics`, `problems`). */
  readonly body?: Readonly<Record<string, unknown>>;
}

/** Codes of problems the client works with; the API's are in its ProblemCodes. */
export const ProblemCode = {
  /** The client's own: no answer, or none from the application (a proxy's). */
  unreachable: 'unreachable',
  unauthenticated: 'unauthenticated',
  forbidden: 'forbidden',
  passwordChangeRequired: 'password-change-required',
  xsrfTokenInvalid: 'xsrf-token-invalid',
  invalidRequest: 'invalid-request',
  invalidCredentials: 'invalid-credentials',
  lockedOut: 'locked-out',
  accountDisabled: 'account-disabled',
  wrongPassword: 'wrong-password',
  weakPassword: 'weak-password',
  tooManyRequests: 'too-many-requests',
} as const;

/** The codes the API gives problems without codes of their own, by status. */
const statusCodes: Readonly<Record<number, string>> = {
  400: 'bad-request',
  401: ProblemCode.unauthenticated,
  403: ProblemCode.forbidden,
  404: 'not-found',
  405: 'method-not-allowed',
  409: 'conflict',
  415: 'unsupported-media-type',
  422: 'unprocessable',
  429: ProblemCode.tooManyRequests,
  500: 'internal-error',
};

/** The problem an error is: an answer of the API's, an answer of something else's, or no answer. */
export function problemOf(error: unknown): Problem {
  if (!(error instanceof HttpErrorResponse)) {
    return {
      status: 0,
      code: 'client-error',
      title: 'Something went wrong in the application',
      detail: error instanceof Error ? error.message : undefined,
    };
  }
  const body =
    isRecord(error.error) && typeof error.error['code'] === 'string' ? error.error : null;
  const retryAfter = Number(error.headers?.get('Retry-After') ?? NaN);
  if (body == null) {
    // No answer, or a proxy's in place of the application's (ng serve's when the server is down).
    if (
      error.status === 0 ||
      error.status === 502 ||
      error.status === 503 ||
      error.status === 504
    ) {
      return {
        status: error.status,
        code: ProblemCode.unreachable,
        title: "Can't reach the server",
        detail: 'Check the connection, and try again.',
      };
    }
    return {
      status: error.status,
      code: statusCodes[error.status] ?? `status-${error.status}`,
      title: error.status === 429 ? 'Too many requests' : `The request failed (${error.status})`,
      retryAfter: Number.isFinite(retryAfter) ? retryAfter : undefined,
    };
  }
  return {
    status: error.status,
    code: body['code'] as string,
    title:
      typeof body['title'] === 'string' ? body['title'] : `The request failed (${error.status})`,
    detail: typeof body['detail'] === 'string' ? body['detail'] : undefined,
    errors: isRecord(body['errors'])
      ? (body['errors'] as Record<string, readonly string[]>)
      : undefined,
    traceId: typeof body['traceId'] === 'string' ? body['traceId'] : undefined,
    retryAfter: Number.isFinite(retryAfter) ? retryAfter : undefined,
    body,
  };
}

/** A problem as one sentence or two, for people. */
export function problemMessage(problem: Problem): string {
  if (problem.code === ProblemCode.tooManyRequests && problem.retryAfter) {
    return `Too many requests: try again in ${problem.retryAfter} seconds.`;
  }
  const title = sentence(problem.title);
  return problem.detail ? `${title} ${sentence(problem.detail)}` : title;
}

/** Whether a problem is about the session, which the client deals with itself (signing in again, a password to change). */
export function isSessionProblem(problem: Problem): boolean {
  return (
    problem.code === ProblemCode.unauthenticated ||
    problem.code === ProblemCode.passwordChangeRequired
  );
}

function sentence(text: string): string {
  return /[.!?]$/.test(text) ? text : `${text}.`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
