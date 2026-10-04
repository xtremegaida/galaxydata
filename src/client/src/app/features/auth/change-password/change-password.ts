import {
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import {
  FormField,
  FormRoot,
  form,
  required,
  validate,
  type TreeValidationResult,
  type ValidationError,
} from '@angular/forms/signals';
import { MatButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { Router } from '@angular/router';
import { ProblemCode, problemMessage, problemOf, type Problem } from '../../../core/api/problem';
import { AuthStore } from '../../../core/auth/auth-store';
import { PasswordPolicy, passwordRules } from '../../../core/auth/password-policy';
import { safeReturnUrl } from '../../../core/auth/return-url';
import { fieldErrors, focusFirstInvalid } from '../../../core/forms/server-errors';
import { Notifier } from '../../../core/notify/notifier';
import { Message } from '../../../core/ui/message';
import { AuthLayout } from '../auth-layout';

interface PasswordModel {
  currentPassword: string;
  newPassword: string;
  confirmation: string;
}

/**
 * Changing one's password: when the user chooses to, or first, when it must be (an administrator set it). The
 * user's other sessions end; this one goes on, to `returnUrl`.
 */
@Component({
  selector: 'gd-change-password',
  imports: [
    AuthLayout,
    Message,
    FormField,
    FormRoot,
    MatButton,
    MatCheckbox,
    MatError,
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
  ],
  templateUrl: './change-password.html',
  styleUrl: '../auth-page.scss',
})
export class ChangePassword {
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);
  private readonly injector = inject(Injector);

  /** Where to go once changed, or on cancelling (the query's `returnUrl`). */
  readonly returnUrl = input<string>();

  protected readonly user = this.auth.user;
  /** Whether the password must be changed before anything else. */
  protected readonly mustChange = computed(() => this.user()?.mustChangePassword ?? false);
  protected readonly policy = inject(PasswordPolicy);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly passwordsShown = signal(false);
  protected readonly message = problemMessage;

  private readonly model = signal<PasswordModel>({
    currentPassword: '',
    newPassword: '',
    confirmation: '',
  });

  protected readonly form = form(
    this.model,
    (path) => {
      required(path.currentPassword, { message: 'Enter your current password' });
      required(path.newPassword, { message: 'Enter a new password' });
      passwordRules(path.newPassword, this.policy, () => this.user()?.userName);
      validate(path.newPassword, ({ value, valueOf }) =>
        value() !== '' && value() === valueOf(path.currentPassword)
          ? { kind: 'unchanged', message: 'It must differ from the current one' }
          : null,
      );
      required(path.confirmation, { message: 'Enter the new password again' });
      validate(path.confirmation, ({ value, valueOf }) =>
        value() !== '' && value() !== valueOf(path.newPassword)
          ? { kind: 'mismatch', message: "The passwords don't match" }
          : null,
      );
    },
    {
      submission: {
        action: () => this.change(),
        onInvalid: () => focusFirstInvalid(this.fields()),
      },
    },
  );

  constructor() {
    this.policy.ensure();
  }

  protected async cancel(): Promise<void> {
    await this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
  }

  protected async signOut(): Promise<void> {
    try {
      await this.auth.signOut();
    } catch (error) {
      this.notifier.problem(problemOf(error));
    }
  }

  private fields() {
    return [this.form.currentPassword, this.form.newPassword, this.form.confirmation];
  }

  private async change(): Promise<TreeValidationResult> {
    this.problem.set(null);
    const { currentPassword, newPassword } = this.model();
    try {
      await this.auth.changePassword(currentPassword, newPassword, this.returnUrl());
      this.notifier.say('Your password is changed.');
      return undefined;
    } catch (error) {
      const errors = this.refusal(problemOf(error));
      if (errors.length > 0) {
        afterNextRender(() => focusFirstInvalid(this.fields()), { injector: this.injector });
      }
      return errors;
    }
  }

  /** What the server refused, on the fields it was about; the rest above the form. */
  private refusal(problem: Problem): ValidationError.WithFieldTree[] {
    if (problem.code === ProblemCode.wrongPassword) {
      return [{ kind: 'server', message: problem.title, fieldTree: this.form.currentPassword }];
    }
    if (problem.code === ProblemCode.weakPassword) {
      return [
        {
          kind: 'server',
          message: problem.detail ?? problem.title,
          fieldTree: this.form.newPassword,
        },
      ];
    }
    const { errors, others } = fieldErrors(problem, {
      currentPassword: this.form.currentPassword,
      newPassword: this.form.newPassword,
    });
    this.problem.set(errors.length > 0 && others.length === 0 ? null : problem);
    return errors;
  }
}
