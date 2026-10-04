import type { Navigation } from '@angular/router';
import { keepFocus, keepsFocus } from './keep-focus';

describe('keepsFocus', () => {
  const navigation = (trigger: Navigation['trigger'], state?: Record<string, unknown>) =>
    ({ trigger, extras: { state } }) as unknown as Navigation;

  it('leaves focus where it is for navigations that say so, but not going back or forward to them', () => {
    expect(keepsFocus(navigation('imperative', keepFocus))).toBe(true);
    expect(keepsFocus(navigation('popstate', keepFocus))).toBe(false);
    expect(keepsFocus(navigation('imperative'))).toBe(false);
    expect(keepsFocus(null)).toBe(false);
  });
});
