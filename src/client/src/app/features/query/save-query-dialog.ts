import { Component, inject, signal } from '@angular/core';
import {
  FormField,
  FormRoot,
  form,
  maxLength,
  required,
  validate,
  type TreeValidationResult,
} from '@angular/forms/signals';
import { MatButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import type { Schema } from '../../core/api/api-client';
import { type Problem, problemMessage } from '../../core/api/problem';
import { fieldErrors, focusFirstInvalid } from '../../core/forms/server-errors';
import { Message } from '../../core/ui/message';

export type SavedQuery = Schema<'SavedQueryDto'>;

/** What a saved query is called and who sees it. */
export interface QueryDetails {
  readonly name: string;
  readonly description: string;
  readonly isShared: boolean;
}

/** What saving came to: the query saved, or why it wasn't. */
export type SaveOutcome = { readonly saved: SavedQuery } | { readonly problem: Problem };

/** What the dialog asks for, and how it saves. */
export interface SaveQueryData {
  readonly title: string;
  readonly action: string;
  readonly details: QueryDetails;
  readonly save: (details: QueryDetails) => Promise<SaveOutcome>;
}

/** The longest name and description the server keeps. */
const nameLength = 200;
const descriptionLength = 1000;

/**
 * Saving a query: its name (its owner's alone, whatever the case), what it is for, and whether everyone may see it.
 * The page says how it is saved (a new query, a copy, the details of one); a name taken, or another problem, is
 * said in the dialog, which closes with the query saved.
 */
@Component({
  selector: 'gd-save-query-dialog',
  imports: [
    FormField,
    FormRoot,
    MatButton,
    MatCheckbox,
    MatDialogActions,
    MatDialogContent,
    MatDialogTitle,
    MatError,
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
    Message,
  ],
  template: `
    <form [formRoot]="form">
      <h2 mat-dialog-title>{{ data.title }}</h2>
      <mat-dialog-content>
        <div role="alert">
          @if (problem(); as problem) {
            <gd-message kind="problem">{{ problem }}</gd-message>
          }
        </div>
        <mat-form-field class="field">
          <mat-label>Name</mat-label>
          <input matInput [formField]="form.name" autocomplete="off" />
          <mat-hint>Yours alone: another of yours can't have it, whatever its case</mat-hint>
          <mat-error>{{ form.name().errors()[0]?.message }}</mat-error>
        </mat-form-field>
        <mat-form-field class="field">
          <mat-label>Description</mat-label>
          <textarea matInput [formField]="form.description" rows="3"></textarea>
          <mat-hint>What the query is for (if you like)</mat-hint>
          <mat-error>{{ form.description().errors()[0]?.message }}</mat-error>
        </mat-form-field>
        <mat-checkbox [formField]="form.isShared"
          >Shared: everyone may see it and run it</mat-checkbox
        >
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button matButton type="button" [disabled]="form().submitting()" (click)="cancel()">
          Cancel
        </button>
        <button matButton="filled" type="submit" [disabled]="form().submitting()">
          {{ data.action }}
        </button>
      </mat-dialog-actions>
    </form>
  `,
  styles: `
    .field {
      display: block;
      margin-bottom: 8px;
    }
  `,
})
export class SaveQueryDialog {
  protected readonly data = inject<SaveQueryData>(MAT_DIALOG_DATA);
  private readonly dialog = inject<MatDialogRef<SaveQueryDialog, SavedQuery>>(MatDialogRef);

  protected readonly problem = signal<string | null>(null);
  private readonly model = signal({ ...this.data.details });

  protected readonly form = form(
    this.model,
    (path) => {
      required(path.name, { message: 'Name the query' });
      validate(path.name, ({ value }) =>
        value().trim().length > nameLength
          ? { kind: 'maxLength', message: `A name has ${nameLength} characters at most` }
          : null,
      );
      maxLength(path.description, descriptionLength, {
        message: `A description has ${descriptionLength} characters at most`,
      });
    },
    {
      submission: {
        action: () => this.save(),
        onInvalid: () => focusFirstInvalid([this.form.name, this.form.description]),
      },
    },
  );

  /** Closes without saving; not while saving, as it may have happened. */
  protected cancel(): void {
    if (!this.form().submitting()) {
      this.dialog.close();
    }
  }

  private async save(): Promise<TreeValidationResult> {
    this.problem.set(null);
    // Once asked, the save happens: the dialog stays until it is known how it went.
    this.dialog.disableClose = true;
    try {
      const details = this.model();
      const outcome = await this.data.save({
        name: details.name.trim(),
        description: details.description.trim(),
        isShared: details.isShared,
      });
      if ('saved' in outcome) {
        this.dialog.close(outcome.saved);
        return undefined;
      }
      const problem = outcome.problem;
      if (problem.code === 'query-name-taken') {
        return [
          { kind: 'server', message: problem.detail ?? problem.title, fieldTree: this.form.name },
        ];
      }
      const errors = fieldErrors(problem, {
        name: this.form.name,
        'query.name': this.form.name,
        description: this.form.description,
        'query.description': this.form.description,
      });
      this.problem.set(
        errors.others.length > 0
          ? errors.others.join(' ')
          : errors.errors.length > 0
            ? null
            : problemMessage(problem),
      );
      return errors.errors;
    } finally {
      this.dialog.disableClose = false;
    }
  }
}
