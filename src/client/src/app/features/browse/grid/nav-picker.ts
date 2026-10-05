import { Component, inject, signal } from '@angular/core';
import { MatButton } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { type BrowseCrumb, crumbOf } from '../../../core/browse/browse-url';
import type { BrowseSource } from './browse-datasource';
import { BrowseGrid, type ChosenRow } from './browse-grid';
import type { GridReference } from './grid-links';

/** What the picker chooses for: the entity whose rows refer, and the reference its columns hold. */
export interface NavPickerData {
  readonly entity: string;
  readonly reference: GridReference;
  /** Whether the reference may refer to no row (its columns all may be NULL). */
  readonly nullable: boolean;
}

/** What was chosen: a row of the reference's target, with the columns its values are of; or no row. */
export type NavPicked = ChosenRow | { readonly none: true };

/**
 * Chooses the row a reference refers to: the target's rows in a grid (filtered, sorted and paged as in browsing,
 * without links or changes), one chosen and confirmed (Choose, a double click, or Enter on it); or none, when the
 * reference may refer to none.
 */
@Component({
  selector: 'gd-nav-picker',
  imports: [
    BrowseGrid,
    MatButton,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
  ],
  template: `
    <h2 mat-dialog-title>Choose a row of {{ data.reference.target }}</h2>
    <mat-dialog-content class="content">
      <p class="aside">
        The row <code>{{ data.reference.navigation }}</code> of {{ data.entity }} refers to.
        Double-click it, or choose it and Choose.
      </p>
      <gd-browse-grid
        class="rows"
        [source]="source"
        [crumb]="crumb()"
        [inspectable]="false"
        (crumbChange)="crumb.set($event)"
        (rowChosen)="chosen.set($event)"
        (rowActivated)="choose($event)"
      />
    </mat-dialog-content>
    <mat-dialog-actions>
      @if (data.nullable) {
        <button matButton type="button" class="none" (click)="none()">No row</button>
      }
      <button matButton type="button" [mat-dialog-close]="undefined">Cancel</button>
      <button
        matButton="filled"
        type="button"
        disabledInteractive
        [disabled]="!chosen()"
        (click)="choose(chosen())"
      >
        Choose
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .content {
      display: flex;
      flex-direction: column;
      height: 70vh;
      max-height: none;
    }

    .aside {
      margin: 0 0 8px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    code {
      font-family: var(--gd-code-font-family);
    }

    .rows {
      flex: 1;
      min-height: 0;
    }

    .none {
      margin-inline-end: auto;
    }
  `,
})
export class NavPicker {
  protected readonly data = inject<NavPickerData>(MAT_DIALOG_DATA);
  private readonly dialog = inject<MatDialogRef<NavPicker, NavPicked>>(MatDialogRef);

  protected readonly source: BrowseSource = { entity: this.data.reference.target };
  protected readonly crumb = signal<BrowseCrumb>(crumbOf(this.data.reference.target));
  protected readonly chosen = signal<ChosenRow | null>(null);

  protected choose(chosen: ChosenRow | null): void {
    if (chosen) {
      this.dialog.close(chosen);
    }
  }

  protected none(): void {
    this.dialog.close({ none: true });
  }
}
