import type { Schema } from '../../../core/api/api-client';

export type User = Schema<'UserDto'>;

/** What may be said of a user's state: disabled, locked out, with a password to change. */
export function userStates(user: User): string[] {
  const states: string[] = [];
  if (user.isDisabled) {
    states.push('Disabled');
  }
  if (user.lockedOutUntil) {
    states.push('Locked out');
  }
  if (user.mustChangePassword) {
    states.push('Password to change');
  }
  return states;
}

/** A user's name to show: their display name, or their user name. */
export function userLabel(user: Pick<User, 'userName' | 'displayName'>): string {
  return user.displayName || user.userName;
}

/** Letters and digits that can't be taken for one another (no 0 and O, 1 and l), and three marks. */
const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_.';

/** A password made up of `length` random characters, for an administrator to give a user. */
export function generatePassword(length = 20): string {
  const values = crypto.getRandomValues(new Uint32Array(length));
  return Array.from(values, (value) => alphabet[value % alphabet.length]).join('');
}
