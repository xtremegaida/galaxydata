import type { Type } from '@angular/core';
import { Routes } from '@angular/router';
import { unsavedChangesGuard } from '../../core/ui/unsaved-changes';
import { CommitList } from './audit/commit-list';
import { CommitPage } from './audit/commit-page';
import { EventList } from './audit/event-list';
import { ConnectionList } from './connections/connection-list';
import { ConnectionPage } from './connections/connection-page';
import { EntitySettingsPage } from './overlay/entity-settings-page';
import { NavigationPage } from './overlay/navigation-page';
import { OverlayList } from './overlay/overlay-list';
import { RelationPage } from './overlay/relation-page';
import { VirtualEntityPage } from './overlay/virtual-entity-page';
import { NewUser } from './users/new-user';
import { UserList } from './users/user-list';
import { UserPage } from './users/user-page';

/** A kind of overlay item's pages: a new one, and one made (`noun` titles them). */
function overlayItemRoutes(path: string, noun: string, component: Type<unknown>): Routes {
  const title = noun.charAt(0).toUpperCase() + noun.slice(1);
  return [
    {
      path: `overlay/${path}/new`,
      title: `New ${noun}`,
      component,
      canDeactivate: [unsavedChangesGuard],
    },
    {
      path: `overlay/${path}/:id`,
      title,
      component,
      canDeactivate: [unsavedChangesGuard],
    },
  ];
}

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
  { path: 'overlay', title: 'Overlay', component: OverlayList },
  ...overlayItemRoutes('relations', 'relation', RelationPage),
  ...overlayItemRoutes('virtual-entities', 'virtual entity', VirtualEntityPage),
  ...overlayItemRoutes('entity-settings', 'entity settings', EntitySettingsPage),
  ...overlayItemRoutes('navigations', 'navigation override', NavigationPage),
  { path: 'audit', pathMatch: 'full', redirectTo: 'audit/commits' },
  { path: 'audit/commits', title: 'Commits', component: CommitList },
  { path: 'audit/commits/:id', title: 'Commit', component: CommitPage },
  { path: 'audit/events', title: 'What administrators did', component: EventList },
];
