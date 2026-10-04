import type { Routes } from '@angular/router';
import { BrowseLayout } from './browse-layout';
import { BrowsePage, browseMatcher, browseTitle } from './browse-page';
import { CatalogOverview } from './catalog-overview';

/** Browsing: the catalog beside the start of browsing, or a path through the data (its address's segments). */
export const browseRoutes: Routes = [
  {
    path: '',
    component: BrowseLayout,
    children: [
      { path: '', title: 'Browse', component: CatalogOverview },
      {
        matcher: browseMatcher,
        title: browseTitle,
        component: BrowsePage,
        // The title names the crumb shown, which `?at=` says.
        runGuardsAndResolvers: 'paramsOrQueryParamsChange',
      },
    ],
  },
];
