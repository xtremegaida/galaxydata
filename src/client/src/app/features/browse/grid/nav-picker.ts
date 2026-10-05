import { Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatProgressBar } from '@angular/material/progress-bar';
import { ApiClient } from '../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { type BrowseCrumb, crumbOf } from '../../../core/browse/browse-url';
import type { BrowseSource } from './browse-datasource';
import { BrowseGrid, type ChosenRow } from './browse-grid';
import { keyOf } from './grid-columns';
import type { GridReference } from './grid-links';
import { BROWSE_PAGE_SIZE } from './grid-settings';

/** What the picker chooses for: the entity whose rows refer, and the reference its columns hold. */
export interface NavPickerData {
  readonly entity: string;
  readonly reference: GridReference;
  /** Whether the reference may refer to no row (its columns all may be NULL). */
  readonly nullable: boolean;
  /**
   * The values its columns hold now, in the order of its target's columns (`reference.targetColumns`), for the
   * picker to open at the row they refer to; null when they refer to none (a NULL, a new row's default).
   */
  readonly values: readonly unknown[] | null;
}

/** What was chosen: a row of the reference's target, with the columns its values are of; or no row. */
export type NavPicked = ChosenRow | { readonly none: true };

/**
 * Chooses the row a reference refers to: the target's rows in a grid (filtered, sorted and paged as in browsing,
 * without links or changes), one chosen and confirmed (Choose, a double click, or Enter on it); or none, when the
 * reference may refer to none. It opens at the row the reference refers to (found first: `/api/browse/position`),
 * chosen, the keyboard on it.
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
    MatProgressBar,
  ],
  template: `
    <h2 mat-dialog-title>Choose a row of {{ data.reference.target }}</h2>
    <mat-dialog-content class="content">
      <p class="aside">
        The row <code>{{ data.reference.navigation }}</code> of {{ data.entity }} refers to.
        Double-click it, or choose it and Choose.
      </p>
      <p class="aside" role="status">{{ note() }}</p>
      @if (position.isLoading()) {
        <mat-progress-bar mode="indeterminate" aria-label="Finding the row it refers to" />
      } @else {
        <gd-browse-grid
          class="rows"
          [source]="source"
          [crumb]="crumb()"
          [inspectable]="false"
          [focusChosen]="opened()"
          (crumbChange)="crumb.set($event)"
          (rowChosen)="chosen.set($event)"
          (rowActivated)="choose($event)"
        />
      }
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
  private readonly api = inject(ApiClient);
  private readonly pageSize = inject(BROWSE_PAGE_SIZE);

  protected readonly source: BrowseSource = { entity: this.data.reference.target };
  /** Whether the dialog has opened, and taken the keyboard: the row it refers to may take it then. */
  protected readonly opened = signal(false);
  /** Where the row it refers to is, found before the grid is made, which then opens at its page. */
  protected readonly position = rxResource({
    params: () => this.data.values ?? undefined,
    stream: ({ params: values }) =>
      this.api.post('/api/browse/position', {
        body: {
          entity: this.data.reference.target,
          columns: [...this.data.reference.targetColumns],
          values: [...values],
        },
      }),
  });
  /** The grid's state: at first, the page of the row it refers to, that row chosen. */
  protected readonly crumb = linkedSignal<BrowseCrumb>(() => {
    const start = crumbOf(this.data.reference.target);
    const at = this.position.hasValue() ? this.position.value() : null;
    return at?.id
      ? {
          ...start,
          page: at.index === null ? 0 : Math.floor(at.index / this.pageSize),
          row: keyOf(at.id),
        }
      : start;
  });
  /** Why it doesn't open at the row it refers to, when it refers to one. */
  protected readonly note = computed(() => {
    const error = this.position.error();
    if (error) {
      return `The row it refers to couldn't be found: ${problemMessage(problemOf(error))}`;
    }
    const at = this.position.hasValue() ? this.position.value() : null;
    if (at?.id === null) {
      return `No row of ${this.data.reference.target} has the values it refers to.`;
    }
    return at?.index === null
      ? "Where the row it refers to is among these can't be told: it is chosen if it is on this page."
      : '';
  });
  protected readonly chosen = signal<ChosenRow | null>(null);

  constructor() {
    this.dialog.afterOpened().subscribe(() => this.opened.set(true));
  }

  protected choose(chosen: ChosenRow | null): void {
    if (chosen) {
      this.dialog.close(chosen);
    }
  }

  protected none(): void {
    this.dialog.close({ none: true });
  }
}
