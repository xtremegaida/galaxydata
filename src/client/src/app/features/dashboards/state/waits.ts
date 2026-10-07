import { InjectionToken } from '@angular/core';

/** How long dashboards wait, in milliseconds: before an editor's preview, and for typing in a filter to pause. */
export interface DashboardWaits {
  readonly preview: number;
  readonly filterTyping: number;
}

/** The waits (a millisecond in tests). */
export const DASHBOARD_WAITS = new InjectionToken<DashboardWaits>('DASHBOARD_WAITS', {
  providedIn: 'root',
  factory: () => ({ preview: 400, filterTyping: 300 }),
});
