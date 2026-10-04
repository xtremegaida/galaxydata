import { effect, signal, untracked, type Signal } from '@angular/core';

/**
 * `source`'s value once it has stayed the same for `wait` milliseconds (what is typed, once typing pauses). Call it
 * in an injection context.
 */
export function debounced<T>(source: () => T, wait: number): Signal<T> {
  const value = signal(untracked(source));
  effect((onCleanup) => {
    const latest = source();
    const timer = setTimeout(() => value.set(latest), wait);
    onCleanup(() => clearTimeout(timer));
  });
  return value.asReadonly();
}
