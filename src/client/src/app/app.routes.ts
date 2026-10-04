import { Routes } from '@angular/router';
import { passwordGuard, signedInGuard, signedOutGuard } from './core/auth/guards';
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
  {
    path: '',
    component: Shell,
    canActivate: [signedInGuard],
    canActivateChild: [signedInGuard],
    children: [{ path: '', title: 'Start', component: Start }],
  },
  // An address the client doesn't know (an old link) goes to the start.
  { path: '**', redirectTo: '' },
];
