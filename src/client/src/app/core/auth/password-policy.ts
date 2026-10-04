import { Injectable, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { maxLength, minLength, validate, type SchemaPath } from '@angular/forms/signals';
import { ApiClient, type Schema } from '../api/api-client';

export type PasswordPolicyDto = Schema<'PasswordPolicyDto'>;

/**
 * What a password must be (`GET /api/auth/password-policy`): long enough, not too long, and not holding the user's
 * name. Asked for once, for every form that sets a password.
 */
@Injectable({ providedIn: 'root' })
export class PasswordPolicy {
  private readonly api = inject(ApiClient);
  private readonly loaded = rxResource({
    stream: () => this.api.get('/api/auth/password-policy'),
  });

  /** The policy; null until it is known (or when it can't be). */
  readonly value = computed(() => (this.loaded.hasValue() ? this.loaded.value() : null));

  /** Asks again when asking failed: for a form that needs the policy. */
  ensure(): void {
    if (this.loaded.status() === 'error') {
      this.loaded.reload();
    }
  }

  /** What to tell those choosing a password. */
  readonly hint = computed(() => {
    const policy = this.value();
    return policy ? `At least ${policy.minimumLength} characters, without the user name` : '';
  });
}

/**
 * A form's rules for a password: the policy's lengths, and not holding the user name (which `userName` gives), with
 * the messages the server gives.
 */
export function passwordRules(
  path: SchemaPath<string>,
  policy: PasswordPolicy,
  userName: () => string | null | undefined,
): void {
  minLength(path, () => policy.value()?.minimumLength, {
    message: () => `A password needs at least ${policy.value()?.minimumLength} characters`,
  });
  maxLength(path, () => policy.value()?.maximumLength, {
    message: () => `A password may have at most ${policy.value()?.maximumLength} characters`,
  });
  validate(path, ({ value }) => {
    const name = userName()?.trim().toLowerCase();
    return name && value().toLowerCase().includes(name)
      ? { kind: 'holdsUserName', message: 'A password may not hold the user name' }
      : null;
  });
}
