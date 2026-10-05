import { Routes } from '@angular/router';
import { unsavedChangesGuard } from '../../core/ui/unsaved-changes';
import { CommitList } from './audit/commit-list';
import { CommitPage } from './audit/commit-page';
import { EventList } from './audit/event-list';
import { ConnectionList } from './connections/connection-list';
import { ConnectionPage } from './connections/connection-page';
import { NewUser } from './users/new-user';
import { UserList } from './users/user-list';
import { UserPage } from './users/user-page';

/** The administrators' pages (loaded when one is opened, by administrators alone). */
export const adminRoutes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'users' },
  { path: 'users', title: 'Users', component: UserList },
  {
    path: 'users/new',
    title: 'New user',
    component: NewUser,
    canDeactivate: [unsavedChangesGuard],
  },
  { path: 'users/:id', title: 'User', component: UserPage, canDeactivate: [unsavedChangesGuard] },
  { path: 'connections', title: 'Connections', component: ConnectionList },
  {
    path: 'connections/new',
    title: 'New connection',
    component: ConnectionPage,
    canDeactivate: [unsavedChangesGuard],
  },
  {
    path: 'connections/:id',
    title: 'Connection',
    component: ConnectionPage,
    canDeactivate: [unsavedChangesGuard],
  },
  { path: 'audit', pathMatch: 'full', redirectTo: 'audit/commits' },
  { path: 'audit/commits', title: 'Commits', component: CommitList },
  { path: 'audit/commits/:id', title: 'Commit', component: CommitPage },
  { path: 'audit/events', title: 'What administrators did', component: EventList },
];
