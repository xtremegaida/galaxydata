import { Component, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import {
  FormField,
  FormRoot,
  form,
  maxLength,
  pattern,
  required,
  type TreeValidationResult,
} from '@angular/forms/signals';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatOption, MatSelect } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { ProblemCode, problemMessage, problemOf, type Problem } from '../../../core/api/problem';
import { PasswordPolicy, passwordRules } from '../../../core/auth/password-policy';
import { roleLabels, type UserRole } from '../../../core/auth/roles';
import { fieldErrors, focusFirstInvalid } from '../../../core/forms/server-errors';
import { Notifier } from '../../../core/notify/notifier';
import { Message } from '../../../core/ui/message';
import { warnBeforeUnload, type HasUnsavedChanges } from '../../../core/ui/unsaved-changes';
import { NewPassword } from './new-password';
import { userLabel } from './users';

interface NewUserModel {
  userName: string;
  displayName: string;
  role: UserRole;
  password: string;
}

const empty: NewUserModel = { userName: '', displayName: '', role: 'read', password: '' };

/** Making a user, with a password they must change at their first sign-in. */
@Component({
  selector: 'gd-new-user',
  imports: [
    FormField,
    FormRoot,
    MatAnchor,
    MatButton,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
    Message,
    NewPassword,
    RouterLink,
  ],
  template: `
    <div class="admin-page">
      <a class="back" matButton routerLink="/admin/users">
        <mat-icon>arrow_back</mat-icon>
        Users
      </a>
      <header class="page-header"><h1>New user</h1></header>
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">{{ message(problem) }}</gd-message>
        }
      </div>
      <form class="form" [formRoot]="form">
        <mat-form-field>
          <mat-label>User name</mat-label>
          <input
            matInput
            [formField]="form.userName"
            autocomplete="off"
            autocapitalize="none"
            spellcheck="false"
          />
          <mat-hint>What they sign in with: letters, digits and . _ &#64; -</mat-hint>
          <mat-error>{{ form.userName().errors()[0]?.message }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Name</mat-label>
          <input matInput [formField]="form.displayName" autocomplete="off" />
          <mat-hint>How the application shows them; their user name when empty</mat-hint>
          <mat-error>{{ form.displayName().errors()[0]?.message }}</mat-error>
        </mat-form-field>
        <mat-form-field>
          <mat-label>Role</mat-label>
          <mat-select [formField]="form.role">
            @for (role of roles; track role[0]) {
              <mat-option [value]="role[0]">{{ role[1] }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <gd-new-password [field]="form.password" label="First password" />
        <div class="actions">
          <button matButton="filled" type="submit" [disabled]="form().submitting()">
            Make the user
          </button>
          <a matButton routerLink="/admin/users">Cancel</a>
        </div>
      </form>
    </div>
  `,
  styleUrl: '../admin-page.scss',
})
export class NewUser implements HasUnsavedChanges {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);
  private readonly injector = inject(Injector);
  private readonly policy = inject(PasswordPolicy);

  protected readonly problem = signal<Problem | null>(null);
  protected readonly roles = Object.entries(roleLabels) as [UserRole, string][];
  protected readonly message = problemMessage;

  private readonly model = signal<NewUserModel>(empty);
  private readonly made = signal(false);

  protected readonly form = form(
    this.model,
    (path) => {
      required(path.userName, { message: 'Give a user name' });
      pattern(path.userName, /^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$/, {
        message:
          'A user name has letters, digits and . _ @ -, starts with a letter or digit, and has at most 64 characters',
      });
      maxLength(path.displayName, 200, { message: 'A name may have at most 200 characters' });
      required(path.password, { message: 'Give a password, or make one up' });
      passwordRules(path.password, this.policy, () => this.model().userName);
    },
    {
      submission: {
        action: () => this.make(),
        onInvalid: () => focusFirstInvalid(this.fields()),
      },
    },
  );

  private readonly dirty = computed(
    () => !this.made() && JSON.stringify(this.model()) !== JSON.stringify(empty),
  );

  constructor() {
    warnBeforeUnload(() => this.dirty());
  }

  hasUnsavedChanges(): boolean {
    return this.dirty();
  }

  private fields() {
    return [this.form.userName, this.form.displayName, this.form.password];
  }

  private async make(): Promise<TreeValidationResult> {
    this.problem.set(null);
    const { userName, displayName, role, password } = this.model();
    try {
      const user = await firstValueFrom(
        this.api.post('/api/users', {
          body: { userName, displayName: displayName.trim() || null, role, password },
        }),
      );
      this.made.set(true);
      this.notifier.say(
        `${userLabel(user)} is made, to change the password at their first sign-in.`,
      );
      await this.router.navigate(['/admin/users', user.id], { replaceUrl: true });
      return undefined;
    } catch (error) {
      const errors = this.refusal(problemOf(error));
      if (errors.length > 0) {
        afterNextRender(() => focusFirstInvalid(this.fields()), { injector: this.injector });
      }
      return errors;
    }
  }

  private refusal(problem: Problem) {
    if (problem.code === ProblemCode.userNameTaken) {
      return [
        { kind: 'server', message: problem.detail ?? problem.title, fieldTree: this.form.userName },
      ];
    }
    if (problem.code === ProblemCode.weakPassword) {
      return [
        { kind: 'server', message: problem.detail ?? problem.title, fieldTree: this.form.password },
      ];
    }
    const { errors, others } = fieldErrors(problem, {
      userName: this.form.userName,
      displayName: this.form.displayName,
      password: this.form.password,
    });
    this.problem.set(errors.length > 0 && others.length === 0 ? null : problem);
    return errors;
  }
}
