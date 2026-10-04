import { DOCUMENT } from '@angular/common';
import { DestroyRef, inject } from '@angular/core';
import type { CanDeactivateFn } from '@angular/router';
import { Confirmer } from './confirmer';

/** A page with changes that may not have been saved. */
export interface HasUnsavedChanges {
  hasUnsavedChanges(): boolean;
}

/** Asks before leaving a page whose changes aren't saved. */
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = (page) =>
  !page.hasUnsavedChanges() ||
  inject(Confirmer).confirm({
    title: 'Leave without saving?',
    message: 'The changes on this page will be lost.',
    confirm: 'Leave',
    cancel: 'Stay',
    destructive: true,
  });

/**
 * Has the browser ask before the page is closed or loaded anew while `unsaved` says there are changes. Call it from
 * a page's constructor.
 */
export function warnBeforeUnload(unsaved: () => boolean): void {
  const view = inject(DOCUMENT).defaultView;
  const warn = (event: BeforeUnloadEvent) => {
    if (unsaved()) {
      event.preventDefault();
    }
  };
  view?.addEventListener('beforeunload', warn);
  inject(DestroyRef).onDestroy(() => view?.removeEventListener('beforeunload', warn));
}
