import { inject } from '@angular/core';
import { Router, type CanActivateFn, type CanMatchFn, type UrlTree } from '@angular/router';
import { AuthStore, type Permissions, type Session } from './auth-store';
import { navigationTarget, returnTo, safeReturnUrl } from './return-url';

/**
 * Pages for those signed in: others sign in first, and come back; a password to change comes first. As a
 * `canActivateChild` guard too, so the pages under the shell check as they are opened.
 */
export const signedInGuard: CanActivateFn = async (_route, state) => {
  const router = inject(Router);
  const session = await inject(AuthStore).ensure();
  return firstStop(router, session, state.url) ?? true;
};

/** The sign-in page, for those not signed in: those who are go where they were going. */
export const signedOutGuard: CanActivateFn = async (route) => {
  const router = inject(Router);
  const session = await inject(AuthStore).ensure();
  return session.signedIn
    ? router.parseUrl(safeReturnUrl(route.queryParamMap.get('returnUrl')))
    : true;
};

/** The page to change the password on, for those signed in. */
export const passwordGuard: CanActivateFn = async () => {
  const router = inject(Router);
  const session = await inject(AuthStore).ensure();
  return session.signedIn ? true : router.createUrlTree(['/sign-in']);
};

/**
 * Pages for those who may do something (`canAdmin`): others go to the start. Those not signed in, or with a
 * password to change, do that first, and come back. As a `canMatch` guard it runs before the guards of the routes
 * above, so it does their part too.
 */
export function allowedTo(permission: keyof Permissions): CanMatchFn & CanActivateFn {
  return async () => {
    const router = inject(Router);
    const auth = inject(AuthStore);
    const session = await auth.ensure();
    return (
      firstStop(router, session, navigationTarget(router)) ??
      (auth.permissions()[permission] ? true : router.createUrlTree(['/']))
    );
  };
}

/** Where someone not signed in, or with a password to change, goes first, to come back to `url`. */
function firstStop(router: Router, session: Session, url: string): UrlTree | null {
  if (!session.signedIn) {
    return router.createUrlTree(['/sign-in'], { queryParams: returnTo(url) });
  }
  if (session.user?.mustChangePassword) {
    return router.createUrlTree(['/change-password'], { queryParams: returnTo(url) });
  }
  return null;
}
