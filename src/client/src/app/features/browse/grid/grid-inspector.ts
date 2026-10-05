import { Component, LOCALE_ID, computed, inject, input, output } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { type GridColumn, type GridRow, binaryText, cellText } from './grid-columns';
import type { RowState } from './grid-edits';
import {
  type GridCollection,
  type GridReference,
  collectionText,
  refersToNone,
} from './grid-links';

/** A cell inspected: its column's, or a collection's of rows that refer to its row. */
export type Inspected = InspectedColumn | InspectedCollection;

/** A cell of one of the rows' columns, and its row (null while it is loaded). */
export interface InspectedColumn {
  readonly kind: 'column';
  readonly column: GridColumn;
  /** The column's place among the row's values. */
  readonly index: number;
  readonly row: GridRow | null;
  /** What the column's values refer to, if they do. */
  readonly reference: GridReference | null;
  /** Whether the cell has a link to follow. */
  readonly linked: boolean;
  /** The cell's value as it will be (its change's, else the row's); undefined for a new row's default. */
  readonly value: unknown;
  /** The display value of the row its values refer to (given with a change of them), if there is one. */
  readonly display: string | null;
  /** The cell's change and its row's, when the rows may be changed. */
  readonly edit: CellEdit | null;
}

/** What is changed of a cell and its row, not committed yet. */
export interface CellEdit {
  readonly state: RowState;
  /** Whether the cell's value is changed. */
  readonly changed: boolean;
  /** The value the row had when the cell was first changed. */
  readonly original: unknown;
  /** Whether the row was changed elsewhere since: its value read now isn't the original. */
  readonly conflict: boolean;
  /** Whether the cell may be given a value. */
  readonly editable: boolean;
}

/** A cell of a collection's column: the rows that refer to its row. */
export interface InspectedCollection {
  readonly kind: 'collection';
  readonly collection: GridCollection;
  readonly row: GridRow | null;
  readonly linked: boolean;
}

const lineageKinds: Readonly<Record<GridColumn['lineage']['kind'], string>> = {
  direct: 'Read from a column',
  computed: 'Worked out',
  aggregated: 'Aggregated over a group',
  constant: 'A constant',
  union: 'Put together from',
  unknown: "Where its values come from isn't known",
};

/**
 * What the cell the keyboard is on (or clicked) holds, whole (the grid shows what fits), and where its column's
 * values come from: the columns of the tables they are read from, through which navigations, and the expression that
 * works them out. A cell whose values refer to a row says which; a collection's, what rows it leads to.
 */
@Component({
  selector: 'gd-grid-inspector',
  imports: [MatButton],
  host: { role: 'region', 'aria-label': 'Inspector' },
  template: `
    @if (collection(); as inspected) {
      @let collection = inspected.collection;
      <h2 class="name">
        <code>{{ collection.navigation }}</code>
      </h2>
      <p class="type">
        {{ collection.multiplicity === 'many' ? 'Rows of' : 'A row of' }}
        <code>{{ collection.target }}</code>
      </p>
      <section aria-labelledby="gd-inspector-collection">
        <h3 id="gd-inspector-collection">Leads to</h3>
        <p>{{ collectionText(collection) }}</p>
        @if (inspected.linked) {
          <p class="aside">Enter, or a click on the link, shows {{ them(collection) }}.</p>
        }
      </section>
    } @else if (column(); as inspected) {
      @let column = inspected.column;
      <h2 class="name">
        <code>{{ column.name }}</code>
      </h2>
      <p class="type">
        <code>{{ column.type.text }}</code>
        @if (column.isKey) {
          <span class="badge">key</span>
        }
      </p>
      <section aria-labelledby="gd-inspector-value">
        <h3 id="gd-inspector-value">Value</h3>
        @if (value(); as value) {
          <pre class="value" [class.null]="value.null">{{ value.text }}</pre>
          @if (value.size) {
            <p class="aside">{{ value.size }}</p>
          }
        } @else {
          <p class="aside">The row is being loaded.</p>
        }
        @if (inspected.edit; as edit) {
          @switch (edit.state) {
            @case ('new') {
              <p class="note">A new row: added when the changes are committed.</p>
            }
            @case ('deleted') {
              <p class="note">To be deleted when the changes are committed.</p>
            }
          }
          @if (edit.changed && edit.state !== 'new') {
            <p class="note">
              Changed, not committed: it was <code>{{ text(edit.original) }}</code
              >.
            </p>
          }
          @if (edit.conflict) {
            <p class="note conflict">
              Changed elsewhere since: it is <code>{{ readText() }}</code> now. Committing would
              change nothing, and say so.
            </p>
          }
          @if (edit.changed || edit.state === 'deleted') {
            <button matButton type="button" class="revert" (click)="revert.emit()">
              {{
                edit.state === 'deleted'
                  ? 'Restore the row'
                  : edit.state === 'new'
                    ? 'Clear the value'
                    : 'Revert'
              }}
            </button>
          }
          @if (!edit.editable && edit.state !== 'deleted' && reason(); as reason) {
            <p class="aside">{{ reason }}</p>
          }
        }
      </section>
      @if (inspected.reference; as reference) {
        <section aria-labelledby="gd-inspector-reference">
          <h3 id="gd-inspector-reference">Refers to</h3>
          <p>
            A row of <code>{{ reference.target }}</code
            >, through <code>{{ reference.navigation }}</code
            >.
          </p>
          @if (noRow(); as noRow) {
            <p class="aside">{{ noRow }}</p>
          } @else {
            @if (display(); as display) {
              <pre class="display">{{ display }}</pre>
            }
            @if (inspected.linked) {
              <p class="aside">Enter, or a click on the link, shows the row.</p>
            } @else if (inspected.edit?.state === 'new' || inspected.edit?.changed) {
              <p class="aside">It leads to the row once the change is committed.</p>
            } @else if (inspected.row && inspected.row.id === null) {
              <p class="aside">Rows without a key lead nowhere.</p>
            }
          }
          @if (inspected.edit?.editable) {
            <p class="aside">F2, or a double click, chooses the row it refers to.</p>
          }
        </section>
      }
      <section aria-labelledby="gd-inspector-lineage">
        <h3 id="gd-inspector-lineage">Where it comes from</h3>
        <p>{{ kinds[column.lineage.kind] }}</p>
        @if (column.lineage.sources.length > 0) {
          <ul class="sources">
            @for (source of column.lineage.sources; track $index) {
              <li>
                <code>{{ source.column }}</code>
                @if (source.path) {
                  <span class="aside"
                    >through <code>{{ source.path }}</code></span
                  >
                }
              </li>
            }
          </ul>
        }
        @if (column.lineage.expression) {
          <pre class="expression">{{ column.lineage.expression }}</pre>
        }
      </section>
    } @else {
      <p class="empty">
        Choose a cell to see what it holds, and where its column's values come from.
      </p>
    }
  `,
  styles: `
    :host {
      display: block;
      box-sizing: border-box;
      padding: 12px 16px;
      overflow: auto;
      border-left: 1px solid var(--mat-sys-outline-variant);
      font: var(--mat-sys-body-medium);
    }

    :host([hidden]) {
      display: none;
    }

    h2 {
      margin: 0;
      font: var(--mat-sys-title-medium);
      overflow-wrap: anywhere;
    }

    h3 {
      margin: 16px 0 4px;
      font: var(--mat-sys-title-small);
    }

    p {
      margin: 0;
    }

    .type {
      margin-top: 4px;
      color: var(--mat-sys-on-surface-variant);
    }

    code,
    pre {
      font-family: var(--gd-code-font-family);
    }

    .badge {
      margin-inline-start: 8px;
      padding: 1px 6px;
      border-radius: var(--mat-sys-corner-small);
      background: var(--mat-sys-surface-container-highest);
      font: var(--mat-sys-label-small);
    }

    pre {
      margin: 0;
      padding: 8px;
      max-height: 240px;
      overflow: auto;
      border-radius: var(--mat-sys-corner-small);
      background: var(--mat-sys-surface-container);
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }

    .null {
      color: var(--mat-sys-on-surface-variant);
      font-style: italic;
    }

    .aside,
    .empty {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .sources {
      margin: 4px 0;
      padding-inline-start: 20px;
      overflow-wrap: anywhere;
    }

    .expression,
    .display {
      margin-top: 4px;
    }

    .note {
      margin-top: 8px;
    }

    .conflict {
      color: var(--mat-sys-error);
    }

    .revert {
      margin-top: 4px;
    }
  `,
})
export class GridInspector {
  private readonly locale = inject(LOCALE_ID);

  readonly inspected = input<Inspected | null>(null);
  /** The cell's change is to be reverted (its row restored, when it is to be deleted). */
  readonly revert = output<void>();

  protected readonly kinds = lineageKinds;
  protected readonly collectionText = collectionText;
  protected readonly them = them;
  protected readonly column = computed(() => {
    const inspected = this.inspected();
    return inspected?.kind === 'column' ? inspected : null;
  });
  protected readonly collection = computed(() => {
    const inspected = this.inspected();
    return inspected?.kind === 'collection' ? inspected : null;
  });
  /** Why the cell's values refer to no row: a null among them (as they will be, for this cell's). */
  protected readonly noRow = computed(() => {
    const inspected = this.column();
    const reference = inspected?.reference;
    if (!inspected?.row || !reference) {
      return null;
    }
    // A new row's value not given is the column's default, not NULL.
    const none =
      inspected.value === null ||
      (!inspected.edit?.changed &&
        inspected.edit?.state !== 'new' &&
        refersToNone(reference, inspected.row));
    if (!none) {
      return null;
    }
    return reference.columns.length === 1
      ? 'Its value is NULL: it refers to no row.'
      : 'One of its values is NULL: it refers to no row.';
  });
  /** The display value of the row the cell's values refer to. */
  protected readonly display = computed(() => this.column()?.display ?? null);
  /** The value read now, beside one changed elsewhere since. */
  protected readonly readText = computed(() => {
    const inspected = this.column();
    return inspected?.row ? this.text(inspected.row.v[inspected.index]) : '';
  });
  /** Why the cell can't be given a value. */
  protected readonly reason = computed(() => {
    const inspected = this.column();
    if (!inspected) {
      return null;
    }
    return inspected.column.readOnlyReason ?? `${inspected.column.name} can't be changed here.`;
  });
  protected readonly value = computed(() => {
    const inspected = this.column();
    if (!inspected?.row) {
      return null;
    }
    const { column, value } = inspected;
    if (value === undefined) {
      const text =
        column.insert === 'required' ? 'No value yet: it needs one' : "The column's default";
      return { text, null: true, size: '' };
    }
    if (value === null) {
      return { text: 'NULL', null: true, size: '' };
    }
    if (column.type.kind === 'binary') {
      const bytes = byteCount(String(value));
      return {
        text: binaryText(String(value), 256),
        null: false,
        size: `${bytes.toLocaleString(this.locale)} ${bytes === 1 ? 'byte' : 'bytes'}`,
      };
    }
    const text = cellText(value, column.type);
    const size =
      column.type.kind === 'string'
        ? `${[...text].length.toLocaleString(this.locale)} ${[...text].length === 1 ? 'character' : 'characters'}`
        : '';
    return { text, null: false, size };
  });

  protected text(value: unknown): string {
    const column = this.column()?.column;
    return column ? cellText(value ?? null, column.type) : String(value);
  }
}

function them(collection: GridCollection): string {
  return collection.multiplicity === 'many' ? 'them' : 'it';
}

function byteCount(base64: string): number {
  const padding = base64.endsWith('==') ? 2 : base64.endsWith('=') ? 1 : 0;
  return Math.floor((base64.length * 3) / 4) - padding;
}
