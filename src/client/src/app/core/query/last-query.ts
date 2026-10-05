import { Injectable } from '@angular/core';
import type { Navigation } from '@angular/router';

/**
 * The query editor's last address, while the application is open: going back to the editor (from the navigation)
 * shows the query left there.
 */
@Injectable({ providedIn: 'root' })
export class LastQuery {
  /** The address, as the router serializes it; null before the editor is first left. */
  url: string | null = null;
}

/** A navigation's state that opens the editor on a new query, not the one left there. */
export const newQueryState: Readonly<Record<string, unknown>> = { gdNewQuery: true };

/** Whether a navigation opens the editor on a new query (back and forward to one too: they keep its state). */
export function opensNewQuery(navigation: Navigation | null | undefined): boolean {
  return navigation?.extras.state?.['gdNewQuery'] === true;
}
