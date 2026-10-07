import { InjectionToken, type Signal, effect, inject, untracked } from '@angular/core';
import type { Schema } from '../../../core/api/api-client';
import { pageVisible } from '../../../core/browser/page-visibility';
import type { DashboardStore } from './dashboard-store';

export type RefreshPolicy = Schema<'RefreshPolicy'>;

/** The longest a refresh waits after failures (the wait doubles from the interval to this). */
export const maxRefreshWait = 5 * 60 * 1000;

/** What a refresh waits on, from the moment the clock says (tests give their own). */
export const REFRESH_CLOCK = new InjectionToken<() => number>('REFRESH_CLOCK', {
  providedIn: 'root',
  factory: () => () => Date.now(),
});

/**
 * Refreshes a dashboard on its timer: every so many seconds, but never while the page isn't shown (a tab behind
 * others) nor while its widgets are still reading (so refreshes don't pile up); after rows that couldn't be read,
 * twice as long each time, to five minutes. Shown again after longer than its interval, it refreshes at once. A
 * dashboard refreshed by hand isn't refreshed here. Made in an injection context.
 */
export function refreshOnTimer(store: DashboardStore, policy: Signal<RefreshPolicy | null>): void {
  const visible = pageVisible();
  const now = inject(REFRESH_CLOCK);
  let last = now();
  let seen = 0;
  let failures = 0;
  effect(() => {
    failures = store.failing().size > 0 ? failures + 1 : 0;
  });
  effect((onCleanup) => {
    // Each refresh (by hand or by the timer) starts the wait anew.
    const count = store.refreshes();
    if (count !== seen) {
      seen = count;
      last = untracked(now);
    }
    const refresh = policy();
    if (refresh?.mode !== 'interval' || !refresh.seconds || !visible() || store.anyLoading()) {
      return;
    }
    store.failing();
    const every = refresh.seconds * 1000;
    const wait = Math.min(
      every * 2 ** Math.max(untracked(() => failures) - 1, 0),
      Math.max(every, maxRefreshWait),
    );
    const due = Math.max(0, last + wait - now());
    const timer = setTimeout(() => store.refresh(), due);
    onCleanup(() => clearTimeout(timer));
  });
}
