import type { HttpTestingController, TestRequest } from '@angular/common/http/testing';

/**
 * The request to a URL, once it has been made: requests made as a navigation's guards run, or as a form is
 * submitted, come a few turns of the event loop later. Requests let go of (a resource asked anew) are left aside.
 * Two such requests fail the test (and are no longer pending, as `match` takes what it finds).
 */
export async function requestTo(
  http: HttpTestingController,
  url: string,
  method = 'GET',
): Promise<TestRequest> {
  for (let turn = 0; turn < 50; turn++) {
    const found = http
      .match((request) => request.urlWithParams === url && request.method === method)
      .filter((request) => !request.cancelled);
    if (found.length === 1) {
      return found[0];
    }
    if (found.length > 1) {
      throw new Error(`${found.length} requests to ${method} ${url}`);
    }
    await new Promise((resolve) => setTimeout(resolve));
  }
  throw new Error(`No request to ${method} ${url}`);
}

/** Lets what was started (a navigation, a request's answer) go on for a few turns of the event loop. */
export async function settle(turns = 3): Promise<void> {
  for (let turn = 0; turn < turns; turn++) {
    await new Promise((resolve) => setTimeout(resolve));
  }
}
