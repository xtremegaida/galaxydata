import type { ResolveFn, Routes } from '@angular/router';
import { BrowseLayout } from './browse-layout';
import { CatalogOverview } from './catalog-overview';
import { EntityPage } from './entity-page';

/** An entity's page is titled by its name. */
const entityTitle: ResolveFn<string> = (route) => route.paramMap.get('entity') ?? 'Entity';

/** Browsing: the catalog beside the start of browsing, or an entity's page. */
export const browseRoutes: Routes = [
  {
    path: '',
    component: BrowseLayout,
    children: [
      { path: '', title: 'Browse', component: CatalogOverview },
      { path: ':entity', title: entityTitle, component: EntityPage },
    ],
  },
];
