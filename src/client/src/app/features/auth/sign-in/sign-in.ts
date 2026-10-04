import { Component, Injector, afterNextRender, inject, input, signal } from '@angular/core';
import {
  FormField,
  FormRoot,
  form,
  required,
  type TreeValidationResult,
} from '@angular/forms/signals';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatError, MatFormField, MatLabel, MatSuffix } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { ProblemCode, problemMessage, problemOf, type Problem } from '../../../core/api/problem';
import { AuthStore } from '../../../core/auth/auth-store';
import { fieldErrors, focusFirstInvalid } from '../../../core/forms/server-errors';
import { AuthLayout } from '../auth-layout';

interface SignInModel {
  userName: string;
  password: string;
}

/** Signing in, then going where the user was going (`returnUrl`). */
@Component({
  selector: 'gd-sign-in',
  imports: [
    AuthLayout,
    FormField,
    FormRoot,
    MatButton,
    MatError,
    MatFormField,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatSuffix,
  ],
  templateUrl: './sign-in.html',
  styleUrl: '../auth-page.scss',
})
export class SignIn {
  private readonly auth = inject(AuthStore);
  private readonly injector = inject(Injector);

  /** Where to go once signed in (the query's `returnUrl`). */
  readonly returnUrl = input<string>();

  /** Why the user is asked to sign in again. */
  protected readonly notice = signal<string | null>(null);
  /** What went wrong: signing in, or asking for the session before. */
  protected readonly problem = signal<Problem | null>(null);
  protected readonly passwordShown = signal(false);
  protected readonly message = problemMessage;
  protected readonly unreachable = ProblemCode.unreachable;

  private readonly model = signal<SignInModel>({ userName: '', password: '' });

  protected readonly form = form(
    this.model,
    (path) => {
      required(path.userName, { message: 'Enter your user name' });
      required(path.password, { message: 'Enter your password' });
    },
    {
      submission: {
        action: () => this.signIn(),
        onInvalid: () => focusFirstInvalid(this.fields()),
      },
    },
  );

  constructor() {
    // Put in the page's live regions once they are there, so that they are announced.
    afterNextRender(() => {
      this.notice.set(this.auth.notice());
      this.problem.set(this.auth.problem());
    });
  }

  /**
   * Asks for the session again, after it couldn't be asked. The user may be signed in already: then the store
   * goes where the page was to go.
   */
  protected async tryAgain(): Promise<void> {
    this.problem.set(null);
    await this.auth.load();
    this.problem.set(this.auth.problem());
  }

  private fields() {
    return [this.form.userName, this.form.password];
  }

  private async signIn(): Promise<TreeValidationResult> {
    this.problem.set(null);
    const { userName, password } = this.model();
    try {
      await this.auth.signIn(userName, password, this.returnUrl());
      return undefined;
    } catch (error) {
      const problem = problemOf(error);
      const { errors, others } = fieldErrors(problem, {
        userName: this.form.userName,
        password: this.form.password,
      });
      this.problem.set(errors.length > 0 && others.length === 0 ? null : problem);
      if (errors.length > 0) {
        afterNextRender(() => focusFirstInvalid(this.fields()), { injector: this.injector });
      }
      return errors;
    }
  }
}
