import {
  HttpErrorResponse,
  HttpResponse,
  type HttpHeaders,
  type HttpInterceptorFn,
} from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { tap } from 'rxjs';

/** The header the API gives the catalog's version in. */
export const catalogVersionHeader = 'X-Catalog-Version';

/**
 * The version of the catalog the server last answered with: what is known of entities, columns and their links
 * changes with it (a schema read again, the overlay changed), and what was read of them may be out of date.
 *
 * Answers can come in another order than their requests went, so an older version may follow a newer one for a
 * moment.
 */
@Injectable({ providedIn: 'root' })
export class CatalogVersion {
  private readonly state = signal<string | null>(null);

  /** The version; null until an answer has said. */
  readonly version = this.state.asReadonly();

  /** An answer's version, when it gave one. */
  seen(version: string | null): void {
    if (version) {
      this.state.set(version);
    }
  }
}

/** Keeps the catalog's version each answer gives, failures' too. */
export const catalogVersionInterceptor: HttpInterceptorFn = (request, next) => {
  const versions = inject(CatalogVersion);
  const seen = (headers: HttpHeaders | undefined) =>
    versions.seen(headers?.get(catalogVersionHeader) ?? null);
  return next(request).pipe(
    tap({
      next: (event) => {
        if (event instanceof HttpResponse) {
          seen(event.headers);
        }
      },
      error: (error: unknown) => {
        if (error instanceof HttpErrorResponse) {
          seen(error.headers);
        }
      },
    }),
  );
};
