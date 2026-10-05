import type { Params, Router } from '@angular/router';

/** The pages of signing in and changing the password, which nothing returns to. */
const authPages = ['/sign-in', '/change-password'];

/** An origin no address of the application's can have, to resolve paths against. */
const base = 'http://galaxydata.invalid';

/**
 * Where to go after signing in or changing the password: an address of the application's (a path), but not one of
 * those pages; the start otherwise. The address is read as a browser reads it (which drops tabs and line breaks,
 * and takes `\` for `/`), so what would lead to another site doesn't pass.
 */
export function safeReturnUrl(url: string | null | undefined): string {
  if (!url || !url.startsWith('/')) {
    return '/';
  }
  let resolved: URL;
  try {
    resolved = new URL(url, base);
  } catch {
    return '/';
  }
  if (resolved.origin !== base) {
    return '/';
  }
  // Without its fragment, the browser's alone (the query editor's holds its query): kept in the sign-in page's
  // query, it would be sent to the server. The editor shows the query left there again.
  const path = resolved.pathname + resolved.search;
  return isAuthPage(path) ? '/' : path;
}

/**
 * The query that brings the user back to `url` from signing in or changing the password: none for the start. From
 * those pages, it is where they were to go back to.
 */
export function returnTo(url: string): Params {
  const safe = safeReturnUrl(isAuthPage(url) ? returnUrlOf(url) : url);
  return safe === '/' ? {} : { returnUrl: safe };
}

/** Whether an address is the page of signing in or of changing the password. */
export function isAuthPage(url: string): boolean {
  return authPages.includes(pathOf(url));
}

/** An address's path, without its matrix parameters, query or fragment. */
export function pathOf(url: string): string {
  return url.split(/[?#;]/, 1)[0];
}

/** The `returnUrl` of an address's query. */
export function returnUrlOf(url: string): string | null {
  try {
    return new URL(url, base).searchParams.get('returnUrl');
  } catch {
    return null;
  }
}

/** Where the router is going, while it navigates; where it is, otherwise. */
export function navigationTarget(router: Router): string {
  const navigation = router.currentNavigation();
  return navigation
    ? router.serializeUrl(navigation.finalUrl ?? navigation.extractedUrl)
    : router.url;
}
