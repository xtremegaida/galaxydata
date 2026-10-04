import { Clipboard } from '@angular/cdk/clipboard';
import { Component, inject, input, signal } from '@angular/core';
import { FormField, type FieldTree } from '@angular/forms/signals';
import { MatIconButton } from '@angular/material/button';
import { MatError, MatFormField, MatHint, MatLabel, MatSuffix } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatTooltip } from '@angular/material/tooltip';
import { PasswordPolicy } from '../../../core/auth/password-policy';
import { Notifier } from '../../../core/notify/notifier';
import { generatePassword } from './users';

/**
 * A password an administrator gives a user: shown on asking, made up on asking, and copied, to pass on. The user
 * must change it at their next sign-in. It isn't the administrator's, so password managers aren't asked to keep it
 * (`autocomplete="off"`).
 */
@Component({
  selector: 'gd-new-password',
  imports: [
    FormField,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatSuffix,
    MatTooltip,
  ],
  template: `
    <mat-form-field class="field">
      <mat-label>{{ label() }}</mat-label>
      <input
        matInput
        [formField]="field()"
        [type]="shown() ? 'text' : 'password'"
        autocomplete="off"
        spellcheck="false"
      />
      <span matSuffix class="buttons">
        <button
          matIconButton
          type="button"
          aria-label="Show the password"
          [attr.aria-pressed]="shown()"
          (click)="shown.set(!shown())"
        >
          <mat-icon>{{ shown() ? 'visibility_off' : 'visibility' }}</mat-icon>
        </button>
        <button
          matIconButton
          type="button"
          aria-label="Make up a password"
          matTooltip="Make up a password"
          (click)="generate()"
        >
          <mat-icon>casino</mat-icon>
        </button>
        <button
          matIconButton
          type="button"
          aria-label="Copy the password"
          matTooltip="Copy the password"
          [disabled]="!field()().value()"
          (click)="copy()"
        >
          <mat-icon>content_copy</mat-icon>
        </button>
      </span>
      <mat-hint>{{ policy.hint() }}</mat-hint>
      <mat-error>{{ field()().errors()[0]?.message }}</mat-error>
    </mat-form-field>
  `,
  styles: `
    .field {
      width: 100%;
    }

    .buttons {
      display: flex;
      padding-right: 4px;
    }
  `,
})
export class NewPassword {
  private readonly clipboard = inject(Clipboard);
  private readonly notifier = inject(Notifier);

  readonly field = input.required<FieldTree<string>>();
  readonly label = input('Password');

  protected readonly policy = inject(PasswordPolicy);
  protected readonly shown = signal(false);

  constructor() {
    this.policy.ensure();
  }

  protected generate(): void {
    const state = this.field()();
    state.value.set(generatePassword());
    state.markAsDirty();
    this.shown.set(true);
  }

  protected copy(): void {
    if (this.clipboard.copy(this.field()().value())) {
      this.notifier.say('The password is copied.');
    }
  }
}
