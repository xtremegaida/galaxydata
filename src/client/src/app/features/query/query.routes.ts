import type { Routes } from '@angular/router';
import { QueryPage, queryMatcher } from './query-page';

/** The query editor: a new query (`/query`), or a saved one (`/query/<id>`), on one route so the page stays. */
export const queryRoutes: Routes = [
  { matcher: queryMatcher, title: 'Query', component: QueryPage },
];
