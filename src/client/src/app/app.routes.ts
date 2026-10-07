import { Routes } from '@angular/router';
import { allowedTo, passwordGuard, signedInGuard, signedOutGuard } from './core/auth/guards';
import { Start } from './features/start/start';
import { Shell } from './shell/shell';

export const routes: Routes = [
  // The forms of signing in and changing the password load when they are needed, not for those signed in.
  {
    path: 'sign-in',
    title: 'Sign in',
    canActivate: [signedOutGuard],
    loadComponent: () => import('./features/auth/sign-in/sign-in').then((m) => m.SignIn),
  },
  {
    path: 'change-password',
    title: 'Change password',
    canActivate: [passwordGuard],
    loadComponent: () =>
      import('./features/auth/change-password/change-password').then((m) => m.ChangePassword),
  },
  // A public dashboard, by its link: alone, with no session (its frame may be another site's).
  {
    path: 'embed/:token',
    title: 'Dashboard',
    loadComponent: () => import('./features/dashboards/embed/embed-page').then((m) => m.EmbedPage),
  },
  {
    path: '',
    component: Shell,
    canActivate: [signedInGuard],
    canActivateChild: [signedInGuard],
    children: [
      { path: '', title: 'Start', component: Start },
      {
        path: 'browse',
        canMatch: [allowedTo('canRead')],
        loadChildren: () => import('./features/browse/browse.routes').then((m) => m.browseRoutes),
      },
      {
        path: 'query',
        canMatch: [allowedTo('canRead')],
        loadChildren: () => import('./features/query/query.routes').then((m) => m.queryRoutes),
      },
      {
        path: 'dashboards',
        canMatch: [allowedTo('canRead')],
        loadChildren: () =>
          import('./features/dashboards/dashboards.routes').then((m) => m.dashboardRoutes),
      },
      {
        path: 'admin',
        canMatch: [allowedTo('canAdmin')],
        loadChildren: () => import('./features/admin/admin.routes').then((m) => m.adminRoutes),
      },
    ],
  },
  // An address the client doesn't know (an old link) goes to the start.
  { path: '**', redirectTo: '' },
];
