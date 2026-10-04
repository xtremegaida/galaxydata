import { Component, inject, signal } from '@angular/core';
import { FormRoot, form, required, type TreeValidationResult } from '@angular/forms/signals';
import { MatButton } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { ProblemCode, problemMessage, problemOf, type Problem } from '../../../core/api/problem';
import { PasswordPolicy, passwordRules } from '../../../core/auth/password-policy';
import { focusFirstInvalid } from '../../../core/forms/server-errors';
import { Message } from '../../../core/ui/message';
import { NewPassword } from './new-password';
import { userLabel, type User } from './users';

/**
 * Resetting a user's password: the administrator gives one (made up, or their own), to pass on; the user must
 * change it at their next sign-in, and their sessions end. Closes with the user as they now are.
 */
@Component({
  selector: 'gd-reset-password-dialog',
  imports: [
    FormRoot,
    MatButton,
    MatDialogActions,
    MatDialogContent,
    MatDialogTitle,
    Message,
    NewPassword,
  ],
  template: `
    <form [formRoot]="form">
      <h2 mat-dialog-title>Reset {{ label }}'s password</h2>
      <mat-dialog-content>
        <p class="explanation">
          They must change it when they next sign in, and the sessions they have end now. Pass it on
          to them yourself.
        </p>
        <div role="alert">
          @if (problem(); as problem) {
            <gd-message kind="problem">{{ message(problem) }}</gd-message>
          }
        </div>
        <gd-new-password [field]="form.password" label="New password" />
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button matButton type="button" [disabled]="form().submitting()" (click)="cancel()">
          Cancel
        </button>
        <button matButton="filled" type="submit" [disabled]="form().submitting()">
          Reset the password
        </button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .explanation {
      margin: 0 0 16px;
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class ResetPasswordDialog {
  private readonly api = inject(ApiClient);
  private readonly dialog = inject<MatDialogRef<ResetPasswordDialog, User>>(MatDialogRef);
  private readonly user = inject<User>(MAT_DIALOG_DATA);
  private readonly policy = inject(PasswordPolicy);

  protected readonly label = userLabel(this.user);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly message = problemMessage;

  private readonly model = signal({ password: '' });

  protected readonly form = form(
    this.model,
    (path) => {
      required(path.password, { message: 'Give a password, or make one up' });
      passwordRules(path.password, this.policy, () => this.user.userName);
    },
    {
      submission: {
        action: () => this.reset(),
        onInvalid: () => focusFirstInvalid([this.form.password]),
      },
    },
  );

  /** Closes without resetting; not while the reset is asked, as it may have happened. */
  protected cancel(): void {
    if (!this.form().submitting()) {
      this.dialog.close();
    }
  }

  private async reset(): Promise<TreeValidationResult> {
    this.problem.set(null);
    // Once asked, the reset happens: the dialog stays until it is known how it went.
    this.dialog.disableClose = true;
    try {
      const updated = await firstValueFrom(
        this.api.post('/api/users/{id}/reset-password', {
          path: { id: this.user.id },
          body: { password: this.model().password },
        }),
      );
      this.dialog.close(updated);
      return undefined;
    } catch (error) {
      const problem = problemOf(error);
      if (problem.code === ProblemCode.weakPassword) {
        return [
          {
            kind: 'server',
            message: problem.detail ?? problem.title,
            fieldTree: this.form.password,
          },
        ];
      }
      this.problem.set(problem);
      return undefined;
    } finally {
      this.dialog.disableClose = false;
    }
  }
}
