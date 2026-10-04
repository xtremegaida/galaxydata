import { effect, inject, untracked } from '@angular/core';
import { tap, type MonoTypeOperatorFunction } from 'rxjs';
import { CatalogVersion } from './catalog-version';

/**
 * Follows the catalog for what was read of it: gives an operator to read it through, which notes the catalog's
 * version each answer came with, and calls `reload` when the catalog is at another (what was read may be out of
 * date). An answer that is itself the first to say the catalog changed isn't read again. Call it in an injection
 * context; it stops with it.
 */
export function followCatalog(reload: () => void): <T>() => MonoTypeOperatorFunction<T> {
  const versions = inject(CatalogVersion);
  let answeredAt: string | null = null;
  effect(() => {
    const version = versions.version();
    if (version !== null && answeredAt !== null && version !== answeredAt) {
      untracked(reload);
    }
  });
  return () => tap(() => (answeredAt = versions.version()));
}
