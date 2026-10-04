import { Routes } from '@angular/router';

export const routes: Routes = [
  // An address the client doesn't know (an old link) goes to the start.
  { path: '**', redirectTo: '' },
];
