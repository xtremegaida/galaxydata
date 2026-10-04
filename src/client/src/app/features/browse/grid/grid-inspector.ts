import { Component, LOCALE_ID, computed, inject, input } from '@angular/core';
import { type GridColumn, type GridRow, binaryText, cellText } from './grid-columns';

/** A cell inspected: its column, and its row (null while it is loaded). */
export interface Inspected {
  readonly column: GridColumn;
  /** The column's place among the row's values. */
  readonly index: number;
  readonly row: GridRow | null;
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
 * works them out.
 */
@Component({
  selector: 'gd-grid-inspector',
  host: { role: 'region', 'aria-label': 'Inspector' },
  template: `
    @if (inspected(); as inspected) {
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
      </section>
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

    .expression {
      margin-top: 4px;
    }
  `,
})
export class GridInspector {
  private readonly locale = inject(LOCALE_ID);

  readonly inspected = input<Inspected | null>(null);

  protected readonly kinds = lineageKinds;
  protected readonly value = computed(() => {
    const inspected = this.inspected();
    if (!inspected?.row) {
      return null;
    }
    const { column, row } = inspected;
    const value = row.v[inspected.index];
    if (value === null || value === undefined) {
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
}

function byteCount(base64: string): number {
  const padding = base64.endsWith('==') ? 2 : base64.endsWith('=') ? 1 : 0;
  return Math.floor((base64.length * 3) / 4) - padding;
}
