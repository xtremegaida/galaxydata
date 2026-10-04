import type { Schema } from '../api/api-client';

export type UserRole = Schema<'UserRole'>;

/** What each role is called. */
export const roleLabels: Readonly<Record<UserRole, string>> = {
  read: 'Reader',
  dataManager: 'Data manager',
  admin: 'Administrator',
};
