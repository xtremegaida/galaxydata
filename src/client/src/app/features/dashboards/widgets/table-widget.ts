import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import type { Key, TableConfig } from '../model/definition';
import { WidgetContext } from '../model/widget-context';
import { RowsTable } from './rows-table';

/** A table widget: its rows, grouped or as they are, a page at a time; rows chosen as slices are. */
@Component({
  selector: 'gd-table-widget',
  imports: [MatIcon, MatIconButton, RowsTable],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (context.data(); as data) {
      <gd-rows-table
        [data]="data"
        [config]="config()"
        [label]="context.widget().title ?? 'Rows'"
        [selection]="context.selection()"
        [choosing]="context.choosing() && config().emits && hasKeys()"
        [choose]="choose"
      />
      <div class="pages">
        <span class="aside" aria-live="polite">{{ range() }}</span>
        <button
          matIconButton
          type="button"
          aria-label="Previous page"
          [disabled]="context.offset() === 0"
          (click)="page(-1)"
        >
          <mat-icon>chevron_left</mat-icon>
        </button>
        <button
          matIconButton
          type="button"
          aria-label="Next page"
          [disabled]="!data.truncated"
          (click)="page(1)"
        >
          <mat-icon>chevron_right</mat-icon>
        </button>
      </div>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    gd-rows-table {
      flex: 1 1 auto;
      overflow: auto;
    }
    .pages {
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: 4px;
    }
    .aside {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class TableWidget {
  protected readonly context = inject(WidgetContext);

  protected readonly config = computed(() => this.context.config() as TableConfig);

  /** Whether rows say what choosing them sends: a grouped table's dimensions, a raw table's key. */
  protected readonly hasKeys = computed(() => {
    const data = this.context.data();
    return !!data && data.columns.some((c) => c.role === 'dimension' || c.role === 'key');
  });

  protected readonly range = computed(() => {
    const data = this.context.data();
    if (!data || data.rows.length === 0) {
      return 'No rows';
    }
    const first = data.offset + 1;
    const last = data.offset + data.rows.length;
    return `${first}–${last}${data.total !== null && data.total !== undefined ? ` of ${data.total}` : data.truncated ? ' of more' : ''}`;
  });

  protected readonly choose = (key: Key, action: Parameters<WidgetContext['choose']>[1]) =>
    this.context.choose(key, action);

  protected page(step: number): void {
    const size = this.config().pageSize;
    this.context.offset.update((offset) => Math.max(0, offset + step * size));
  }
}
