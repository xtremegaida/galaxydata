import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTab, MatTabGroup } from '@angular/material/tabs';
import { EMPTY, type Observable, map } from 'rxjs';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { Message } from '../../../core/ui/message';
import { QueryResults } from '../../query/query-results';
import type { QueryRun } from '../../query/query-results';
import type { DataConfig, WidgetData } from '../model/definition';
import type { WidgetQuery } from '../state/dashboard-host';
import { RowsTable } from '../widgets/rows-table';

/** What the Data popup shows: the widget's rows read, and (signed in) how to ask for those they were worked out from. */
export interface DataDialogData {
  readonly title: string;
  readonly data: WidgetData;
  readonly config: DataConfig;
  /** The widget's queries as they run now (the underlying rows' among them); null where they aren't seen (embedded). */
  readonly query: (() => Observable<WidgetQuery> | null) | null;
}

/**
 * A widget's data: the rows it shows, as read (nothing is asked again); and, signed in, the underlying rows, those of
 * its source that every filter and choice reaching it keeps, ungrouped, run when their tab is first shown (with
 * the query results' grid, loaded then).
 */
@Component({
  selector: 'gd-data-dialog',
  imports: [
    MatButton,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatProgressBar,
    MatTab,
    MatTabGroup,
    Message,
    NgTemplateOutlet,
    QueryResults,
    RowsTable,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Data of {{ data.title }}</h2>
    <mat-dialog-content class="content">
      @if (data.query) {
        <mat-tab-group
          animationDuration="0ms"
          [selectedIndex]="tab()"
          (selectedIndexChange)="tab.set($event)"
        >
          <mat-tab label="Rows shown">
            <ng-container *ngTemplateOutlet="shown" />
          </mat-tab>
          <mat-tab label="Underlying rows">
            @if (tab() === 1) {
              <div role="alert">
                @if (underlying.error(); as error) {
                  <gd-message kind="problem">
                    Couldn't ask for the underlying rows: {{ message(error) }}
                    <button gdMessageAction matButton type="button" (click)="underlying.reload()">
                      Try again
                    </button>
                  </gd-message>
                }
              </div>
              @if (underlying.hasValue() ? underlying.value() : null; as run) {
                @defer (on immediate) {
                  <gd-query-results
                    class="results"
                    [run]="run"
                    [links]="false"
                    [inspector]="false"
                  />
                } @loading (minimum 0ms) {
                  <mat-progress-bar mode="indeterminate" aria-label="Loading the grid" />
                }
              } @else if (underlying.isLoading()) {
                <mat-progress-bar
                  mode="indeterminate"
                  aria-label="Asking for the underlying rows"
                />
              }
            }
          </mat-tab>
        </mat-tab-group>
      } @else {
        <ng-container *ngTemplateOutlet="shown" />
      }
      <ng-template #shown>
        @if (data.data.truncated) {
          <p class="aside">The first {{ data.data.rows.length }} rows, as the widget shows them.</p>
        }
        <gd-rows-table
          class="rows"
          [data]="data.data"
          [config]="data.config"
          [label]="data.title"
        />
      </ng-template>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close type="button">Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .content {
      min-height: 320px;
    }
    .rows {
      display: block;
      max-height: 60vh;
      overflow: auto;
    }
    .results {
      height: 60vh;
    }
    .aside {
      margin: 8px 0;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class DataDialog {
  protected readonly data = inject<DataDialogData>(MAT_DIALOG_DATA);
  protected readonly tab = signal(0);
  protected readonly message = (error: unknown) => problemMessage(problemOf(error));

  protected readonly underlying = rxResource({
    params: () => (this.tab() === 1 ? true : undefined),
    stream: () =>
      (this.data.query?.() ?? EMPTY).pipe(
        map((query): QueryRun => ({
          text: query.underlying.text,
          parameters: query.underlying.parameters,
          serial: 1,
        })),
      ),
  });
}
