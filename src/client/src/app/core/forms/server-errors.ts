import type { ReadonlyFieldTree, ValidationError } from '@angular/forms/signals';
import type { Problem } from '../api/problem';

/** A problem's errors by field, as a form's submission gives them back. */
export interface FieldErrors {
  /** The errors of the form's fields, to return from its submission's action. */
  readonly errors: ValidationError.WithFieldTree[];
  /** The messages of fields the form hasn't, to show with the form. */
  readonly others: readonly string[];
}

/**
 * A problem's errors by field (`invalid-request`), on a form's fields, named as the API's JSON names them
 * (`userName`, `settings.Password`).
 */
export function fieldErrors(
  problem: Problem,
  fields: Readonly<Record<string, ReadonlyFieldTree<unknown>>>,
): FieldErrors {
  const errors: ValidationError.WithFieldTree[] = [];
  const others: string[] = [];
  for (const [name, messages] of Object.entries(problem.errors ?? {})) {
    const fieldTree = Object.hasOwn(fields, name) ? fields[name] : undefined;
    for (const message of messages) {
      if (fieldTree) {
        errors.push({ kind: 'server', message, fieldTree });
      } else {
        others.push(message);
      }
    }
  }
  return { errors, others };
}

/** Focuses the first of a form's fields, in their order on the page, that has an error. */
export function focusFirstInvalid(fields: readonly ReadonlyFieldTree<unknown>[]): void {
  fields
    .find((field) => field().invalid())?.()
    .focusBoundControl();
}
