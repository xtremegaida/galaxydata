import type { Navigation } from '@angular/router';

/**
 * A navigation's state that leaves focus where it is, though another page opens: an entity chosen in the catalog's
 * tree, where the keyboard goes on choosing.
 */
export const keepFocus: Readonly<Record<string, unknown>> = { gdKeepFocus: true };

/**
 * Whether a navigation leaves focus where it is. Back and forward don't: the browser gives them the state of the
 * navigation they go back to, which said so where the keyboard was then.
 */
export function keepsFocus(navigation: Navigation | null | undefined): boolean {
  return navigation?.trigger !== 'popstate' && navigation?.extras.state?.['gdKeepFocus'] === true;
}
