import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTooltip } from '@angular/material/tooltip';
import { RouterLink, type UrlTree } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { isSessionProblem, problemMessage } from '../../core/api/problem';
import { entityUrl } from '../../core/browse/browse-url';
import { issueText, rowLabelOf, valueText } from '../../core/changes/change-labels';
import {
  type ChangeIssue,
  type ChangeRow,
  type ClearScope,
  type Outcome,
  type PendingChange,
  PendingChanges,
} from '../../core/changes/pending-changes';
import { Confirmer } from '../../core/ui/confirmer';
import { Message } from '../../core/ui/message';

/** A column changed: its name, its value as it will be, and the value it had (none in a new row). */
interface ColumnChange {
  readonly name: string;
  readonly value: string;
  readonly original: string | null;
  readonly null: boolean;
}

/** A row's change, as the drawer lists it. */
interface RowChange {
  readonly id: number;
  readonly label: string;
  readonly kind: PendingChange['kind'];
  readonly row: ChangeRow | null;
  readonly columns: readonly ColumnChange[];
  /** What is shown for the rows references then refer to, by navigation. */
  readonly display: readonly { readonly navigation: string; readonly value: string }[];
  /** Why the change can't be committed as it is, as the last preview found. */
  readonly issues: readonly string[];
}

interface EntityChanges {
  readonly entity: string;
  readonly url: UrlTree;
  readonly rows: readonly RowChange[];
}

interface SourceChanges {
  readonly source: string;
  readonly count: number;
  readonly entities: readonly EntityChanges[];
}

const kindLabels: Readonly<Record<PendingChange['kind'], string>> = {
  insert: 'New',
  update: 'Changed',
  delete: 'To be deleted',
};

/**
 * The user's pending changes, by connection, entity and row: the values changed (as they were, and will be), new
 * rows, rows to be deleted, and why some can't be committed as they are (as the last preview found); each reverted
 * (a column's, a row's), or those of an entity, a connection or all cleared; and all previewed and committed (in
 * a dialog of its own, loaded when first opened). They are kept on the server until they are committed or reverted.
 */
@Component({
  selector: 'gd-changes-drawer',
  imports: [MatButton, MatIcon, MatIconButton, MatProgressBar, MatTooltip, Message, RouterLink],
  host: { role: 'region', 'aria-labelledby': 'gd-changes-title' },
  template: `
    <header class="head">
      <h2 id="gd-changes-title" tabindex="-1" #heading>Pending changes</h2>
      <button matIconButton type="button" aria-label="Close" (click)="closed.emit()">
        <mat-icon>close</mat-icon>
      </button>
    </header>
    @if (saving()) {
      <mat-progress-bar mode="indeterminate" aria-label="Saving the changes" />
    }
    <div role="alert">
      @if (loadProblem(); as problem) {
        <gd-message kind="problem">
          Couldn't read the changes: {{ message(problem) }}
          <button gdMessageAction matButton type="button" (click)="reload()">Try again</button>
        </gd-message>
      }
      @if (refused(); as failure) {
        <gd-message kind="problem">
          {{ failure }}
          <button gdMessageAction matButton type="button" (click)="refused.set(null)">
            Dismiss
          </button>
        </gd-message>
      }
    </div>
    <p class="summary" role="status">{{ summary() }}</p>
    @if (sources().length > 0) {
      <div class="actions">
        <button
          matButton="filled"
          type="button"
          disabledInteractive
          [disabled]="committing()"
          (click)="commit()"
        >
          <mat-icon>publish</mat-icon>
          Preview and commit
        </button>
        <button matButton type="button" (click)="clear({}, 'all the changes')">
          <mat-icon>delete_sweep</mat-icon>
          Clear all
        </button>
      </div>
    }
    @for (source of sources(); track source.source) {
      <section class="source" [attr.aria-labelledby]="'gd-changes-source-' + $index">
        <div class="group-head">
          <h3 [id]="'gd-changes-source-' + $index">
            <mat-icon aria-hidden="true">database</mat-icon>
            {{ source.source || 'Saving…' }}
          </h3>
          @if (source.source) {
            <button
              matButton
              type="button"
              [attr.aria-label]="'Clear the changes to ' + source.source"
              (click)="clear({ source: source.source }, 'the changes to ' + source.source)"
            >
              Clear
            </button>
          }
        </div>
        @for (entity of source.entities; track entity.entity) {
          <section class="entity" [attr.aria-label]="entity.entity">
            <div class="group-head">
              <h4>
                <a [routerLink]="entity.url">{{ entity.entity }}</a>
              </h4>
              <button
                matButton
                type="button"
                [attr.aria-label]="'Clear the changes to ' + entity.entity"
                (click)="clear({ entity: entity.entity }, 'the changes to ' + entity.entity)"
              >
                Clear
              </button>
            </div>
            <ul class="rows">
              @for (row of entity.rows; track row.id) {
                <li class="row" [class]="'kind-' + row.kind">
                  <div class="row-head">
                    <span class="row-label">{{ row.label }}</span>
                    <span class="badge">{{ kinds[row.kind] }}</span>
                    @if (row.row) {
                      <button
                        matIconButton
                        type="button"
                        class="small"
                        [attr.aria-label]="
                          revertLabel(row) + ': ' + entity.entity + ' ' + row.label
                        "
                        [matTooltip]="revertLabel(row)"
                        (click)="revertRow(entity.entity, row)"
                      >
                        <mat-icon>{{ row.kind === 'insert' ? 'delete' : 'undo' }}</mat-icon>
                      </button>
                    }
                  </div>
                  @if (row.columns.length > 0) {
                    <ul class="columns">
                      @for (column of row.columns; track column.name) {
                        <li>
                          <code class="name">{{ column.name }}</code
                          ><span class="cdk-visually-hidden">: </span>
                          @if (column.original !== null) {
                            <span class="was">{{ column.original }}</span>
                            <span aria-hidden="true">→</span>
                            <span class="cdk-visually-hidden"> becomes </span>
                          }
                          <span class="value" [class.null]="column.null">{{ column.value }}</span>
                          @if (row.row && row.kind !== 'delete') {
                            <button
                              matIconButton
                              type="button"
                              class="small"
                              [attr.aria-label]="
                                'Revert ' + column.name + ' of ' + entity.entity + ' ' + row.label
                              "
                              matTooltip="Revert"
                              (click)="revertColumn(entity.entity, row, column.name)"
                            >
                              <mat-icon>close</mat-icon>
                            </button>
                          }
                        </li>
                      }
                    </ul>
                  }
                  @for (shown of row.display; track shown.navigation) {
                    <p class="display">
                      <code>{{ shown.navigation }}</code
                      >: {{ shown.value }}
                    </p>
                  }
                  @if (row.issues.length > 0) {
                    <ul class="issues">
                      @for (issue of row.issues; track $index) {
                        <li>
                          <mat-icon aria-hidden="true">error</mat-icon>
                          <span
                            ><span class="cdk-visually-hidden">Can't be committed: </span
                            >{{ issue }}</span
                          >
                        </li>
                      }
                    </ul>
                  }
                </li>
              }
            </ul>
          </section>
        }
      </section>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 4px;
      box-sizing: border-box;
      height: 100%;
      padding: 8px 16px 16px;
      overflow: auto;
      font: var(--mat-sys-body-medium);
    }

    .head,
    .group-head,
    .row-head {
      display: flex;
      align-items: center;
      gap: 8px;
    }

    .head {
      justify-content: space-between;
    }

    h2 {
      margin: 0;
      font: var(--mat-sys-title-large);
    }

    h3,
    h4 {
      flex: 1;
      display: flex;
      align-items: center;
      gap: 6px;
      margin: 0;
      overflow-wrap: anywhere;
    }

    h3 {
      font: var(--mat-sys-title-medium);
    }

    h4 {
      font: var(--mat-sys-title-small);
    }

    h4 a {
      color: var(--mat-sys-primary);
    }

    .summary {
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
    }

    .source {
      margin-top: 12px;
    }

    .entity {
      margin: 8px 0 0 8px;
    }

    .rows,
    .columns {
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .row {
      margin: 4px 0;
      padding: 4px 8px;
      border-inline-start: 3px solid var(--mat-sys-tertiary);
      border-radius: var(--mat-sys-corner-extra-small);
      background: var(--mat-sys-surface-container-low);
    }

    .kind-insert {
      border-color: var(--mat-sys-primary);
    }

    .kind-delete {
      border-color: var(--mat-sys-error);
    }

    .row-label {
      flex: 1;
      font-weight: 500;
      overflow-wrap: anywhere;
    }

    .badge {
      padding: 1px 6px;
      border-radius: var(--mat-sys-corner-small);
      background: var(--mat-sys-surface-container-highest);
      font: var(--mat-sys-label-small);
    }

    .columns li {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px 6px;
    }

    code,
    .was,
    .value {
      font-family: var(--gd-code-font-family);
      overflow-wrap: anywhere;
    }

    .was {
      color: var(--mat-sys-on-surface-variant);
      text-decoration: line-through;
    }

    .null {
      color: var(--mat-sys-on-surface-variant);
      font-style: italic;
    }

    .display {
      margin: 2px 0 0;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .issues {
      margin: 2px 0 0;
      padding: 0;
      list-style: none;
      color: var(--mat-sys-error);
      font: var(--mat-sys-body-small);

      li {
        display: flex;
        align-items: flex-start;
        gap: 4px;
      }

      mat-icon {
        flex: none;
        font-size: 16px;
        width: 16px;
        height: 16px;
      }
    }

    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }

    .small {
      --mat-icon-button-state-layer-size: 32px;
      --mat-icon-button-icon-size: 18px;
      padding: 4px;
    }
  `,
})
export class ChangesDrawer {
  private readonly changes = inject(PendingChanges);
  private readonly confirmer = inject(Confirmer);
  private readonly dialog = inject(MatDialog);

  private readonly injector = inject(Injector);
  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  /** Whether the drawer is open: opened, the keyboard goes to its heading. */
  readonly opened = input(false);
  /** The drawer is to close. */
  readonly closed = output<void>();

  protected readonly kinds = kindLabels;
  protected readonly message = problemMessage;
  protected readonly saving = this.changes.saving;
  protected readonly loadProblem = this.changes.problem;
  /** An action that couldn't be done, and why. */
  protected readonly refused = signal<string | null>(null);
  /** Set while the commit dialog is open (or loading): it opens once at a time. */
  protected readonly committing = signal(false);
  private destroyed = false;
  protected readonly sources = computed(() =>
    groupsOf(this.changes.changes(), (change) => this.changes.issuesOf(change)),
  );
  protected readonly summary = computed(() => {
    const count = this.changes.count();
    if (!this.changes.loaded()) {
      return this.loadProblem() ? '' : 'Reading the changes…';
    }
    return count === 0
      ? 'No changes: rows changed in browsing wait here until they are committed.'
      : count === 1
        ? '1 row changed, not committed.'
        : `${count} rows changed, not committed.`;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.destroyed = true));
    effect(() => {
      if (this.opened()) {
        afterNextRender(() => this.heading().nativeElement.focus(), { injector: this.injector });
      }
    });
  }

  protected reload(): void {
    this.changes.reload();
  }

  /** Previews the changes, and commits them, in a dialog; closed, the keyboard comes back to the drawer. */
  protected async commit(): Promise<void> {
    if (this.committing()) {
      return;
    }
    this.committing.set(true);
    try {
      let dialog: typeof import('./commit-dialog');
      try {
        dialog = await import('./commit-dialog');
      } catch {
        this.refused.set("Couldn't open the dialog to commit the changes: try again.");
        return;
      }
      const { CommitDialog } = dialog;
      if (this.destroyed) {
        return;
      }
      await firstValueFrom(
        this.dialog
          .open(CommitDialog, {
            width: '90vw',
            maxWidth: '1100px',
            maxHeight: '90vh',
            autoFocus: 'dialog',
            disableClose: true,
          })
          .afterClosed(),
      );
    } finally {
      this.committing.set(false);
    }
    this.keepKeyboard();
  }

  protected revertLabel(row: RowChange): string {
    switch (row.kind) {
      case 'insert':
        return 'Drop the new row';
      case 'delete':
        return 'Restore the row';
      default:
        return 'Revert the row';
    }
  }

  protected revertRow(entity: string, row: RowChange): void {
    if (row.row) {
      void this.changes.revert(entity, row.row).then((outcome) => this.after(outcome));
      this.keepKeyboard();
    }
  }

  protected revertColumn(entity: string, row: RowChange, column: string): void {
    if (row.row) {
      void this.changes.revert(entity, row.row, [column]).then((outcome) => this.after(outcome));
      this.keepKeyboard();
    }
  }

  /** Clears changes, once the user says so. */
  protected async clear(scope: ClearScope, what: string): Promise<void> {
    const confirmed = await this.confirmer.confirm({
      title: 'Clear the changes?',
      message: `This reverts ${what}: they won't be committed.`,
      confirm: 'Clear',
      destructive: true,
    });
    if (confirmed) {
      const cleared = this.changes.clear(scope);
      this.keepKeyboard();
      this.after(await cleared);
    }
  }

  /**
   * The keyboard goes to the drawer's heading when the button it was on goes with what it reverted (or the
   * confirmation gives it back to a button gone), rather than to the page.
   */
  private keepKeyboard(): void {
    const element = this.heading().nativeElement;
    afterNextRender(
      () => {
        const active = element.ownerDocument.activeElement;
        if (!active || active === element.ownerDocument.body || !active.isConnected) {
          element.focus();
        }
      },
      { injector: this.injector },
    );
  }

  private after(outcome: Outcome): void {
    if (!outcome.done && !isSessionProblem(outcome.problem)) {
      this.refused.set(`Couldn't revert: ${outcome.reasons.join(' ')}`);
    } else if (outcome.done) {
      this.refused.set(null);
    }
  }
}

/** The changes by connection, then entity (each in the order first changed), then row. */
function groupsOf(
  changes: readonly PendingChange[],
  issuesOf: (change: PendingChange) => readonly ChangeIssue[],
): SourceChanges[] {
  const sources = new Map<string, Map<string, PendingChange[]>>();
  for (const change of changes) {
    const entities = sources.get(change.source) ?? new Map<string, PendingChange[]>();
    sources.set(change.source, entities);
    const rows = entities.get(change.entity) ?? [];
    entities.set(change.entity, rows);
    rows.push(change);
  }
  return [...sources].map(([source, entities]) => ({
    source,
    count: [...entities.values()].reduce((sum, rows) => sum + rows.length, 0),
    entities: [...entities].map(([entity, rows]) => {
      return {
        entity,
        url: entityUrl(entity),
        rows: rows.map((change) => rowOf(change, rows, issuesOf(change))),
      };
    }),
  }));
}

function rowOf(
  change: PendingChange,
  changes: readonly PendingChange[],
  issues: readonly ChangeIssue[],
): RowChange {
  const row: ChangeRow | null =
    change.tempId !== null
      ? { tempId: change.tempId }
      : change.key && change.rowId
        ? { key: change.key, rowId: change.rowId }
        : null;
  const label = rowLabelOf(change, changes);
  const names = change.kind === 'delete' ? [] : Object.keys(change.values);
  return {
    id: change.id,
    label,
    kind: change.kind,
    row,
    columns: names.map((name) => ({
      name,
      value: valueText(change.values[name]),
      original: change.kind === 'insert' ? null : valueText(change.original[name]),
      null: change.values[name] === null,
    })),
    // A reference that refers to no row has nothing shown for it.
    display: Object.entries(change.display)
      .filter(([, value]) => value !== null && value !== undefined)
      .map(([navigation, value]) => ({ navigation, value: valueText(value) })),
    issues: issues.map(issueText),
  };
}
