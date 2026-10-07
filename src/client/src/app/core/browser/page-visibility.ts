import { DOCUMENT } from '@angular/common';
import { DestroyRef, type Signal, inject, signal } from '@angular/core';

/** Whether the page is shown (not a tab behind others, nor a window minimized), as it changes; made in an injection context. */
export function pageVisible(): Signal<boolean> {
  const document = inject(DOCUMENT);
  const visible = signal(document.visibilityState !== 'hidden');
  const changed = () => visible.set(document.visibilityState !== 'hidden');
  document.addEventListener('visibilitychange', changed);
  inject(DestroyRef).onDestroy(() => document.removeEventListener('visibilitychange', changed));
  return visible.asReadonly();
}
