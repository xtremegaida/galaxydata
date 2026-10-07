import type { Routes } from '@angular/router';
import { unsavedChangesGuard } from '../../core/ui/unsaved-changes';

const editor = () => import('./editor/dashboard-editor').then((m) => m.DashboardEditor);

/** The dashboards: the list, each one's page, and the editor (new, and of each); each loads when it is opened. */
export const dashboardRoutes: Routes = [
  {
    path: '',
    title: 'Dashboards',
    loadComponent: () => import('./list/dashboard-list').then((m) => m.DashboardList),
  },
  {
    path: 'new',
    title: 'New dashboard',
    canDeactivate: [unsavedChangesGuard],
    loadComponent: editor,
  },
  {
    path: ':id/edit',
    title: 'Edit dashboard',
    canDeactivate: [unsavedChangesGuard],
    loadComponent: editor,
  },
  {
    path: ':id',
    title: 'Dashboard',
    loadComponent: () => import('./dashboard-page').then((m) => m.DashboardPage),
  },
];
