import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatButtonToggle, MatButtonToggleGroup } from '@angular/material/button-toggle';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTab, MatTabGroup } from '@angular/material/tabs';
import { Router } from '@angular/router';
import type { Observable } from 'rxjs';
import { ApiClient, type Schema } from '../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { CodeEditor } from '../../../core/editor/code-editor';
import { gdqLanguageId } from '../../../core/editor/gdq-language';
import { queryUrlTree } from '../../../core/query/query-url';
import { Message } from '../../../core/ui/message';
import { QueryPlan } from '../../query/query-plan';
import { QuerySql } from '../../query/query-sql';
import type { WidgetQuery } from '../state/dashboard-host';

type QueryText = Schema<'QueryTextDto'>;

/** What the View query popup shows: a widget's queries, as they run with what reaches it now. */
export interface QueryDialogData {
  readonly title: string;
  readonly query: Observable<WidgetQuery>;
}

/** Which of a widget's queries: its rows', a series' top categories', a pie's total's. */
type Which = 'main' | 'categories' | 'total';

/**
 * A widget's query in GDQ, with its parameters' values; its SQL and its plan, explained when their tabs are first
 * shown; and a way to open it in the query editor, run. A bar chart by a series also has the query of its top
 * categories, and a pie with "Other" that of its total.
 */
@Component({
  selector: 'gd-query-dialog',
  imports: [
    CodeEditor,
    MatButton,
    MatButtonToggle,
    MatButtonToggleGroup,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatProgressBar,
    MatTab,
    MatTabGroup,
    Message,
    NgTemplateOutlet,
    QueryPlan,
    QuerySql,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Query of {{ data.title }}</h2>
    <mat-dialog-content class="content">
      @if (query.isLoading()) {
        <mat-progress-bar mode="indeterminate" aria-label="Asking for the query" />
      }
      <div role="alert">
        @if (query.error(); as error) {
          <gd-message kind="problem">Couldn't ask for the query: {{ message(error) }}</gd-message>
        }
      </div>
      @if (shown(); as shown) {
        @if (choices().length > 1) {
          <mat-button-toggle-group
            class="which"
            aria-label="Which query"
            hideSingleSelectionIndicator
            [value]="which()"
            (change)="which.set($event.value)"
          >
            @for (choice of choices(); track choice.which) {
              <mat-button-toggle [value]="choice.which">{{ choice.label }}</mat-button-toggle>
            }
          </mat-button-toggle-group>
        }
        <mat-tab-group
          animationDuration="0ms"
          [selectedIndex]="tab()"
          (selectedIndexChange)="tab.set($event)"
        >
          <mat-tab label="GDQ">
            <gd-code-editor
              class="editor"
              label="The widget's query"
              [text]="shown.text"
              [language]="languageId"
              [readOnly]="true"
              [wrap]="true"
              readOnlyMessage="The widget's query is read only: open it in the query editor to change it."
            />
            @if (shown.parameters.length > 0) {
              <table class="parameters" aria-label="Parameters">
                <thead>
                  <tr>
                    <th scope="col">Parameter</th>
                    <th scope="col">Type</th>
                    <th scope="col">Value</th>
                  </tr>
                </thead>
                <tbody>
                  @for (parameter of shown.parameters; track parameter.name) {
                    <tr>
                      <td class="code">{{ '$' + parameter.name }}</td>
                      <td class="code">{{ parameter.type }}</td>
                      <td>{{ valueText(parameter.value) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </mat-tab>
          <mat-tab label="SQL">
            @if (tab() === 1) {
              <ng-container [ngTemplateOutlet]="explaining" />
              @if (explained.hasValue() ? explained.value() : null; as explain) {
                <gd-query-sql [explain]="explain" />
              }
            }
          </mat-tab>
          <mat-tab label="Plan">
            @if (tab() === 2) {
              <ng-container [ngTemplateOutlet]="explaining" />
              @if (explained.hasValue() ? explained.value() : null; as explain) {
                <gd-query-plan [explain]="explain" />
              }
            }
          </mat-tab>
        </mat-tab-group>
      }
      <ng-template #explaining>
        @if (explained.isLoading()) {
          <mat-progress-bar mode="indeterminate" aria-label="Explaining the query" />
        }
        <div role="alert">
          @if (explained.error(); as error) {
            <gd-message kind="problem">
              Couldn't explain the query: {{ message(error) }}
              <button gdMessageAction matButton type="button" (click)="explained.reload()">
                Try again
              </button>
            </gd-message>
          }
        </div>
      </ng-template>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton type="button" [disabled]="!shown()" (click)="openInEditor()">
        Open in the query editor
      </button>
      <button matButton="filled" mat-dialog-close type="button">Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .content {
      min-height: 360px;
    }
    .which {
      margin-bottom: 8px;
    }
    .editor {
      display: block;
      height: 240px;
      margin-top: 8px;
      border: 1px solid var(--mat-sys-outline-variant);
    }
    .parameters {
      margin-top: 12px;
      border-collapse: collapse;
      font: var(--mat-sys-body-medium);
    }
    .parameters th,
    .parameters td {
      padding: 4px 12px 4px 0;
      text-align: start;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }
    .code {
      font-family: var(--gd-code-font-family);
    }
  `,
})
export class QueryDialog {
  protected readonly data = inject<QueryDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly ref = inject(MatDialogRef);
  protected readonly languageId = gdqLanguageId;
  protected readonly message = (error: unknown) => problemMessage(problemOf(error));

  protected readonly tab = signal(0);
  protected readonly which = signal<Which>('main');

  protected readonly query = rxResource({ stream: () => this.data.query });

  protected readonly choices = computed(() => {
    const query = this.query.hasValue() ? this.query.value() : null;
    if (!query) {
      return [];
    }
    return [
      { which: 'main' as const, label: 'Its rows' },
      ...(query.categories ? [{ which: 'categories' as const, label: 'Its top categories' }] : []),
      ...(query.total ? [{ which: 'total' as const, label: 'Its total' }] : []),
    ];
  });

  protected readonly shown = computed<QueryText | null>(() => {
    const query = this.query.hasValue() ? this.query.value() : null;
    return query ? (query[this.which()] ?? query.main) : null;
  });

  protected readonly explained = rxResource({
    params: () => (this.tab() > 0 ? this.shown() : null) ?? undefined,
    stream: ({ params }) =>
      this.api.post('/api/query/explain', {
        body: { text: params.text, parameters: params.parameters, verbose: false },
      }),
  });

  protected valueText(value: unknown): string {
    return value === null || value === undefined
      ? 'NULL'
      : typeof value === 'string'
        ? value
        : JSON.stringify(value);
  }

  protected openInEditor(): void {
    const shown = this.shown();
    if (!shown) {
      return;
    }
    const values = Object.fromEntries(
      shown.parameters.map((p) => [
        p.name,
        p.value === null || p.value === undefined
          ? null
          : typeof p.value === 'string'
            ? p.value
            : String(p.value),
      ]),
    );
    this.ref.close();
    void this.router.navigateByUrl(queryUrlTree({ id: null, text: shown.text, values, run: true }));
  }
}
