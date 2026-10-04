import { DatePipe } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import {
  FormField,
  FormRoot,
  disabled,
  form,
  maxLength,
  type TreeValidationResult,
} from '@angular/forms/signals';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatOption, MatSelect } from '@angular/material/select';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { ProblemCode, problemMessage, problemOf, type Problem } from '../../../core/api/problem';
import { AuthStore } from '../../../core/auth/auth-store';
import { roleLabels, type UserRole } from '../../../core/auth/roles';
import { fieldErrors, focusFirstInvalid } from '../../../core/forms/server-errors';
import { Notifier } from '../../../core/notify/notifier';
import { Confirmer } from '../../../core/ui/confirmer';
import { Message } from '../../../core/ui/message';
import { warnBeforeUnload, type HasUnsavedChanges } from '../../../core/ui/unsaved-changes';
import { ResetPasswordDialog } from './reset-password-dialog';
import { userLabel, userStates, type User } from './users';

interface UserModel {
  displayName: string;
  role: UserRole;
  isDisabled: boolean;
}

function modelOf(user: User | undefined): UserModel {
  return {
    displayName: user?.displayName ?? '',
    role: user?.role ?? 'read',
    isDisabled: user?.isDisabled ?? false,
  };
}

/**
 * A user, for an administrator: their name, role and whether they are disabled, which are changed and saved;
 * resetting their password, unlocking them, and deleting them. Administrators can't demote, disable, reset or
 * delete themselves.
 */
@Component({
  selector: 'gd-user-page',
  imports: [
    DatePipe,
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
    MatProgressBar,
    MatSelect,
    MatSlideToggle,
    Message,
    RouterLink,
  ],
  templateUrl: './user-page.html',
  styleUrl: '../admin-page.scss',
})
export class UserPage implements HasUnsavedChanges {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);
  private readonly confirmer = inject(Confirmer);
  private readonly dialog = inject(MatDialog);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** The user's id (the route's). */
  readonly id = input.required<string>();

  protected readonly user = rxResource({
    params: () => Number(this.id()),
    stream: ({ params: id }) => this.api.get('/api/users/{id}', { path: { id } }),
  });
  protected readonly shown = computed(() => (this.user.hasValue() ? this.user.value() : undefined));
  protected readonly loadProblem = computed(() => {
    const error = this.user.error();
    return error ? problemOf(error) : null;
  });
  protected readonly problem = signal<Problem | null>(null);
  protected readonly busy = signal(false);
  protected readonly isSelf = computed(() => this.auth.user()?.id === this.shown()?.id);
  protected readonly roles = Object.entries(roleLabels) as [UserRole, string][];
  protected readonly label = userLabel;
  protected readonly states = userStates;
  protected readonly message = problemMessage;

  /** What is being edited: the user as loaded, until changed. */
  private readonly model = linkedSignal(() => modelOf(this.shown()));

  protected readonly form = form(
    this.model,
    (path) => {
      maxLength(path.displayName, 200, { message: 'A name may have at most 200 characters' });
      disabled(path.role, () => this.isSelf());
      disabled(path.isDisabled, () => this.isSelf());
    },
    {
      submission: {
        action: () => this.save(),
        onInvalid: () => focusFirstInvalid([this.form.displayName]),
      },
    },
  );

  /** Whether there are changes not saved. */
  protected readonly dirty = computed(() => {
    const user = this.shown();
    return user !== undefined && JSON.stringify(this.model()) !== JSON.stringify(modelOf(user));
  });

  constructor() {
    warnBeforeUnload(() => this.dirty());
  }

  hasUnsavedChanges(): boolean {
    return this.dirty();
  }

  /** Forgets the changes. */
  protected undo(): void {
    this.model.set(modelOf(this.shown()));
  }

  /** Reads the user again, forgetting the changes (once the administrator is sure). */
  protected async reload(): Promise<void> {
    if (
      this.dirty() &&
      !(await this.confirmer.confirm({
        title: 'Read it again?',
        message: 'The changes made here will be lost.',
        confirm: 'Read it again',
        destructive: true,
      }))
    ) {
      return;
    }
    this.problem.set(null);
    this.user.reload();
  }

  protected async resetPassword(user: User): Promise<void> {
    const dialog = this.dialog.open<ResetPasswordDialog, User, User>(ResetPasswordDialog, {
      data: user,
      width: '480px',
    });
    const updated = await firstValueFrom(dialog.afterClosed());
    if (updated) {
      this.replace(updated);
      this.notifier.say(`${userLabel(updated)} must change the password at their next sign-in.`);
    }
  }

  protected async unlock(user: User): Promise<void> {
    await this.act(async () => {
      this.replace(
        await firstValueFrom(this.api.post('/api/users/{id}/unlock', { path: { id: user.id } })),
      );
      this.notifier.say(`${userLabel(user)} may sign in again.`);
      // The button is gone: focus goes to the account's heading.
      afterNextRender(
        () => this.host.nativeElement.querySelector<HTMLElement>('#gd-user-facts')?.focus(),
        {
          injector: this.injector,
        },
      );
    });
  }

  protected async delete(user: User): Promise<void> {
    const sure = await this.confirmer.confirm({
      title: `Delete ${userLabel(user)}?`,
      message:
        'They can no longer sign in, and their pending changes go. Their saved queries stay, shared ones for everyone.',
      confirm: 'Delete',
      destructive: true,
    });
    if (!sure) {
      return;
    }
    await this.act(async () => {
      await firstValueFrom(
        this.api.delete('/api/users/{id}', {
          path: { id: user.id },
          query: { version: user.version },
        }),
      );
      this.model.set(modelOf(user));
      this.notifier.say(`${userLabel(user)} is deleted.`);
      await this.router.navigate(['/admin/users']);
    });
  }

  private async save(): Promise<TreeValidationResult> {
    const user = this.shown();
    if (!user) {
      return undefined;
    }
    this.problem.set(null);
    const { displayName, role, isDisabled } = this.model();
    try {
      this.replace(
        await firstValueFrom(
          this.api.put('/api/users/{id}', {
            path: { id: user.id },
            body: {
              displayName: displayName.trim() || null,
              role,
              isDisabled,
              version: user.version,
            },
          }),
        ),
        false,
      );
      this.notifier.say('Saved.');
      return undefined;
    } catch (error) {
      const problem = problemOf(error);
      const { errors, others } = fieldErrors(problem, { displayName: this.form.displayName });
      this.problem.set(errors.length > 0 && others.length === 0 ? null : problem);
      if (errors.length > 0) {
        afterNextRender(() => focusFirstInvalid([this.form.displayName]), {
          injector: this.injector,
        });
      }
      return errors;
    }
  }

  /** Does something to the user, showing what goes wrong. */
  private async act(action: () => Promise<void>): Promise<void> {
    this.problem.set(null);
    this.busy.set(true);
    try {
      await action();
    } catch (error) {
      this.problem.set(problemOf(error));
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * The user as the server now has them. Changes not saved stay (unlocking or resetting the password doesn't
   * touch them), to be saved to the new version.
   */
  private replace(user: User, keepEdits = true): void {
    const edits = keepEdits && this.dirty() ? this.model() : null;
    this.user.set(user);
    if (edits) {
      this.model.set(edits);
    }
  }

  /** Whether a problem is that the user was changed by someone else since it was read. */
  protected stale(problem: Problem): boolean {
    return problem.code === ProblemCode.concurrencyConflict;
  }
}
