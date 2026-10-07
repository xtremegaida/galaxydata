import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  input,
} from '@angular/core';
import { MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatTooltip } from '@angular/material/tooltip';
import { cellText } from '../../browse/grid/grid-columns';
import { categoryText, formatNumber } from '../charts/chart-values';
import {
  type Bucket,
  type DataConfig,
  type Key,
  type NumberFormat,
  type Selection,
  type WidgetColumn,
  type WidgetData,
  keyText,
} from '../model/definition';
import type { SelectionAction } from '../model/widget-context';

const numeric = new Set(['int16', 'int32', 'int64', 'decimal', 'single', 'double']);

/**
 * A widget's rows as a table: a table widget's, and a chart's to read with a keyboard (each row a slice, with
 * buttons that choose it, add it, or leave it out). Values as the chart labels them: periods as they are read,
 * numbers in the locale, exactly.
 */
@Component({
  selector: 'gd-rows-table',
  imports: [MatIcon, MatIconButton, MatTooltip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="wrap">
      <table class="rows" [attr.aria-label]="label()">
        <thead>
          <tr>
            @for (column of shown(); track column.name) {
              <th scope="col" [class.number]="isNumber(column)">
                @if (swatch()(null, column); as color) {
                  <span class="swatch" aria-hidden="true" [style.background]="color"></span>
                }
                {{ column.label }}
              </th>
            }
            @if (choosing()) {
              <th scope="col" class="actions"><span class="hidden">Choose</span></th>
            }
          </tr>
        </thead>
        <tbody>
          @for (row of rows(); track $index) {
            <tr
              [class.chosen]="state(row) === 'chosen'"
              [class.left-out]="state(row) === 'out'"
              [class.faded]="state(row) === 'faded'"
            >
              @for (column of shown(); track column.name) {
                <td [class.number]="isNumber(column)">
                  @if (swatch()(row, column); as color) {
                    <span class="swatch" aria-hidden="true" [style.background]="color"></span>
                  }
                  {{ text(row, column) }}
                </td>
              }
              @if (choosing()) {
                <td class="actions">
                  <button
                    matIconButton
                    type="button"
                    matTooltip="Choose only this"
                    [attr.aria-label]="'Choose only ' + rowName(row)"
                    (click)="choose()(keyOf(row), 'replace')"
                  >
                    <mat-icon>filter_alt</mat-icon>
                  </button>
                  <button
                    matIconButton
                    type="button"
                    matTooltip="Add to those chosen"
                    [attr.aria-label]="'Add ' + rowName(row)"
                    (click)="choose()(keyOf(row), 'add')"
                  >
                    <mat-icon>add</mat-icon>
                  </button>
                  <button
                    matIconButton
                    type="button"
                    matTooltip="Leave out"
                    [attr.aria-label]="'Leave out ' + rowName(row)"
                    (click)="choose()(keyOf(row), 'exclude')"
                  >
                    <mat-icon>block</mat-icon>
                  </button>
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
  styles: `
    :host {
      display: block;
      min-height: 0;
    }
    .wrap {
      overflow: auto;
      max-height: 100%;
    }
    .rows {
      border-collapse: collapse;
      width: 100%;
      font: var(--mat-sys-body-medium);
    }
    th,
    td {
      padding: 4px 8px;
      text-align: start;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
      white-space: nowrap;
    }
    th {
      position: sticky;
      top: 0;
      background: var(--mat-sys-surface-container-low);
      font: var(--mat-sys-title-small);
    }
    .number {
      text-align: end;
      font-variant-numeric: tabular-nums;
    }
    .actions {
      width: 1%;
      padding: 0;
    }
    .actions button {
      /* 32px targets (WCAG's least is 24px); Material's 48px ones would stick out of the table and scroll it. */
      --mat-icon-button-state-layer-size: 32px;
      --mat-icon-button-touch-target-display: none;
      padding: 4px;
    }
    .swatch {
      display: inline-block;
      width: 10px;
      height: 10px;
      margin-inline-end: 6px;
      border-radius: 50%;
      /* A ring, so a pale colour stands apart from the table. */
      box-shadow: 0 0 0 1px var(--mat-sys-outline-variant);
    }
    .chosen td {
      font-weight: 600;
    }
    .faded td {
      color: var(--mat-sys-on-surface-variant);
    }
    .left-out td:not(.actions) {
      text-decoration: line-through;
      color: var(--mat-sys-on-surface-variant);
    }
    .hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
    }
  `,
})
export class RowsTable {
  private readonly locale = inject(LOCALE_ID);

  readonly data = input.required<WidgetData>();
  readonly config = input.required<DataConfig>();
  readonly label = input('Rows');
  readonly selection = input<Selection | null>(null);
  /** Whether rows have buttons to choose them. */
  readonly choosing = input(false);
  readonly choose = input<(key: Key, action: SelectionAction) => void>(() => undefined);
  /** A chart's colours: of a row's cell (a header's when the row is null), shown beside it; none for a table widget. */
  readonly swatch = input<(row: readonly unknown[] | null, column: WidgetColumn) => string | null>(
    () => null,
  );

  /** The columns shown: all but a raw table's key, which choosing its rows sends. */
  protected readonly shown = computed(() => this.data().columns.filter((c) => c.role !== 'key'));

  protected readonly rows = computed(() => this.data().rows);

  /** The columns a row's key is made of: dimensions (and a series), or a raw table's key. */
  private readonly keyColumns = computed(() => {
    const columns = this.data().columns.map((c, i) => ({ c, i }));
    const key = columns.filter(({ c }) => c.role === 'key');
    if (key.length > 0) {
      return key.map(({ i }) => i);
    }
    return columns
      .filter(({ c }) => c.role === 'dimension' || c.role === 'series')
      .map(({ i }) => i);
  });

  protected isNumber(column: WidgetColumn): boolean {
    return column.role === 'measure' || numeric.has(column.type.kind);
  }

  protected keyOf(row: readonly unknown[]): Key {
    return this.keyColumns().map((i) => row[i]);
  }

  protected rowName(row: readonly unknown[]): string {
    const columns = this.data().columns;
    return this.keyColumns()
      .map((i) => this.text(row, columns[i]))
      .join(', ');
  }

  protected state(row: readonly unknown[]): 'chosen' | 'out' | 'faded' | null {
    const selection = this.selection();
    if (!selection || selection.keys.length === 0) {
      return null;
    }
    const text = keyText(this.keyOf(row));
    const listed = selection.keys.some((k) => keyText(k) === text);
    return selection.mode === 'include' ? (listed ? 'chosen' : 'faded') : listed ? 'out' : null;
  }

  protected text(row: readonly unknown[], column: WidgetColumn): string {
    const value = row[this.data().columns.indexOf(column)];
    if (column.role === 'measure') {
      return formatNumber(value, this.formatOf(column), this.locale);
    }
    if (column.role === 'dimension' || column.role === 'series') {
      return categoryText(value, column.type, this.bucketOf(column), this.locale);
    }
    if (column.format || numeric.has(column.type.kind)) {
      return value === null ? 'NULL' : formatNumber(value, this.formatOf(column), this.locale);
    }
    return cellText(value, column.type);
  }

  /** A column's number format: its own, or a decimal's scale (amounts keep their cents, in a column alike). */
  private formatOf(column: WidgetColumn): NumberFormat | null {
    if (column.format) {
      return column.format;
    }
    const scale = column.type.kind === 'decimal' ? column.type.scale : null;
    return scale === null || scale === undefined
      ? null
      : { decimals: scale, prefix: null, suffix: null, compact: false };
  }

  private bucketOf(column: WidgetColumn): Bucket | null {
    const config = this.config();
    switch (config.kind) {
      case 'pie':
        return config.dimension.bucket;
      case 'bar':
      case 'line':
        return (column.role === 'series' ? config.series?.bucket : config.dimension.bucket) ?? null;
      case 'table':
        return config.dimensions[column.index]?.bucket ?? null;
    }
  }
}
