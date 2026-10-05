import { InjectionToken } from '@angular/core';

/** How many rows a page of a grid has. */
export const BROWSE_PAGE_SIZE = new InjectionToken<number>('BROWSE_PAGE_SIZE', {
  factory: () => 100,
});

/** Where the browser keeps whether grids' inspector is shown. */
export const inspectorKey = 'gd.inspector';
