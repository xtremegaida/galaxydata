import { Component, Injectable, inject } from '@angular/core';
import { MatButton } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogTitle,
} from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

/** What to ask. */
export interface Confirmation {
  readonly title: string;
  readonly message: string;
  /** The button that goes on: "Delete". */
  readonly confirm: string;
  /** The button that doesn't: "Cancel". */
  readonly cancel?: string;
  /** Whether going on can't be undone (the button shows it). */
  readonly destructive?: boolean;
}

@Component({
  selector: 'gd-confirm-dialog',
  imports: [MatButton, MatDialogActions, MatDialogClose, MatDialogContent, MatDialogTitle],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content [id]="data.messageId">{{ data.message }}</mat-dialog-content>
    <mat-dialog-actions align="end">
      <!-- First, so focus starts on the safer choice. -->
      <button matButton [mat-dialog-close]="false">{{ data.cancel ?? 'Cancel' }}</button>
      <button matButton="filled" [class.destructive]="data.destructive" [mat-dialog-close]="true">
        {{ data.confirm }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .destructive {
      --mat-button-filled-container-color: var(--mat-sys-error);
      --mat-button-filled-label-text-color: var(--mat-sys-on-error);
    }
  `,
})
export class ConfirmDialog {
  protected readonly data = inject<Confirmation & { messageId: string }>(MAT_DIALOG_DATA);
}

let nextId = 0;

/** Asks the user whether to go on. */
@Injectable({ providedIn: 'root' })
export class Confirmer {
  private readonly dialog = inject(MatDialog);

  /** Whether the user chose to go on. */
  async confirm(confirmation: Confirmation): Promise<boolean> {
    const messageId = `gd-confirm-message-${nextId++}`;
    const dialog = this.dialog.open<ConfirmDialog, Confirmation & { messageId: string }, boolean>(
      ConfirmDialog,
      {
        data: { ...confirmation, messageId },
        role: 'alertdialog',
        ariaDescribedBy: messageId,
        width: '440px',
      },
    );
    return (await firstValueFrom(dialog.afterClosed())) === true;
  }
}
