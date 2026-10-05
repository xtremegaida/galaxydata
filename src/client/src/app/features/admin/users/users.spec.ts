import { userOf } from '../../../../testing/users';
import { generatePassword, userLabel, userStates } from './users';

describe('users', () => {
  it('makes up passwords of random letters, digits and marks that read unmistakably', () => {
    const passwords = Array.from({ length: 50 }, () => generatePassword());
    for (const password of passwords) {
      expect(password).toMatch(/^[A-HJ-NP-Za-km-z2-9._-]{20}$/);
    }
    expect(new Set(passwords).size).toBe(50);
    expect(generatePassword(32)).toHaveLength(32);
  });

  it("says what a user's state is", () => {
    expect(userStates(userOf())).toEqual([]);
    expect(
      userStates(
        userOf({
          isDisabled: true,
          lockedOutUntil: '2026-10-04T13:00:00Z',
          mustChangePassword: true,
        }),
      ),
    ).toEqual(['Disabled', 'Locked out', 'Password to change']);
  });

  it('names users by their display names, or their user names', () => {
    expect(userLabel(userOf())).toBe('Carol Danvers');
    expect(userLabel(userOf({ displayName: null }))).toBe('carol');
    expect(userLabel(userOf({ displayName: '' }))).toBe('carol');
  });
});
