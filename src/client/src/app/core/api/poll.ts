import { InjectionToken, effect, inject, type ResourceRef } from '@angular/core';

/** How many milliseconds to wait before looking again at what is still changing. */
export const POLL_INTERVAL = new InjectionToken<number>('POLL_INTERVAL', { factory: () => 2000 });

/**
 * Loads a resource again every `interval` milliseconds (`POLL_INTERVAL`'s) while `pending` says what it holds is
 * still changing (a schema being read). After a load that fails, while it was, it tries again, waiting twice as
 * long each time (up to 30 seconds). Call it in an injection context.
 */
export function pollWhile<T>(
  resource: ResourceRef<T | undefined>,
  pending: (value: T) => boolean,
  interval = inject(POLL_INTERVAL),
): void {
  let wasPending = false;
  let failures = 0;
  effect((onCleanup) => {
    const status = resource.status();
    if (status === 'loading' || status === 'reloading') {
      return;
    }
    const value = resource.hasValue() ? resource.value() : undefined;
    let wait: number | null = null;
    if (value !== undefined) {
      failures = 0;
      wasPending = pending(value);
      wait = wasPending ? interval : null;
    } else if (status === 'error' && wasPending) {
      failures++;
      wait = Math.min(interval * 2 ** failures, 30_000);
    }
    if (wait === null) {
      return;
    }
    const timer = setTimeout(() => resource.reload(), wait);
    onCleanup(() => clearTimeout(timer));
  });
}
