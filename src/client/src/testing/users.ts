import type { User } from '../app/features/admin/users/users';

/** A user as the server gives them: carol, a data manager, unless said otherwise. */
export function userOf(changes: Partial<User> = {}): User {
  return {
    id: 2,
    userName: 'carol',
    displayName: 'Carol Danvers',
    role: 'dataManager',
    isDisabled: false,
    mustChangePassword: false,
    lockedOutUntil: null,
    createdAt: '2026-10-01T09:00:00Z',
    lastSignInAt: '2026-10-03T10:00:00Z',
    passwordChangedAt: '2026-10-01T09:00:00Z',
    version: 4,
    ...changes,
  };
}
