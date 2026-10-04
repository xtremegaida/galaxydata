import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';

/**
 * Loading the application anew, so that nothing it held in memory stays: when who is signed in changes.
 */
@Injectable({ providedIn: 'root' })
export class PageLoader {
  private readonly location = inject(DOCUMENT).location;

  /** Loads the application at an address of its own; the start, for an address of another site. */
  load(url: string): void {
    const target = new URL(url, this.location.origin);
    this.location.assign(target.origin === this.location.origin ? target.href : '/');
  }

  /** Loads the application anew where it is. */
  reload(): void {
    this.location.reload();
  }
}
