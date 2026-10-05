import { NgTemplateOutlet } from '@angular/common';
import { Component, LOCALE_ID, computed, inject, input } from '@angular/core';
import { MatTab, MatTabGroup } from '@angular/material/tabs';
import type { Schema } from '../../core/api/api-client';
import { CodeEditor } from '../../core/editor/code-editor';
import { sqlLanguageOf } from '../../core/editor/sql-languages';
import type { QueryExplain } from './query-plan';

type ExplainParameter = Schema<'ExplainParameter'>;

/** SQL a source (or the merge engine) runs, as a tab shows it. */
interface ShownSql {
  readonly label: string;
  /** Where it runs and how its rows are fetched. */
  readonly about: string;
  readonly language: string;
  readonly sql: string;
  readonly parameters: readonly ExplainParameter[];
  /** The statement for each batch of keys, when its rows are fetched by another's keys. */
  readonly batch: { readonly sql: string; readonly parameters: readonly ExplainParameter[] } | null;
}

/**
 * The SQL a query runs: a tab for each source's (how its rows are fetched, its parameters, and the statement for
 * each batch of keys when they are fetched by another's), and the merge engine's that puts them together; each in an
 * editor that can't be edited.
 */
@Component({
  selector: 'gd-query-sql',
  imports: [CodeEditor, MatTab, MatTabGroup, NgTemplateOutlet],
  template: `
    @if (shown().length > 0) {
      <mat-tab-group
        mat-stretch-tabs="false"
        animationDuration="0ms"
        aria-label="The SQL each source runs"
      >
        @for (sql of shown(); track $index) {
          <mat-tab [label]="sql.label">
            <div class="sql">
              <p class="about">{{ sql.about }}</p>
              <gd-code-editor
                class="editor"
                [text]="sql.sql"
                [label]="'SQL: ' + sql.label"
                [language]="sql.language"
                [readOnly]="true"
                readOnlyMessage="The SQL the engine writes is shown, not edited"
              />
              <ng-container
                *ngTemplateOutlet="parametersTable; context: { $implicit: sql.parameters }"
              />
              @if (sql.batch; as batch) {
                <h3>For each batch of keys</h3>
                <gd-code-editor
                  class="editor"
                  [text]="batch.sql"
                  [label]="'SQL for each batch of keys: ' + sql.label"
                  [language]="sql.language"
                  [readOnly]="true"
                  readOnlyMessage="The SQL the engine writes is shown, not edited"
                />
                <ng-container
                  *ngTemplateOutlet="parametersTable; context: { $implicit: batch.parameters }"
                />
              }
            </div>
          </mat-tab>
        }
      </mat-tab-group>
    } @else {
      <p class="aside">The query runs no SQL: it can't be planned as it is.</p>
    }

    <ng-template #parametersTable let-parameters>
      @if (parameters.length > 0) {
        <table class="parameters" aria-label="Parameters">
          <thead>
            <tr>
              <th scope="col">Parameter</th>
              <th scope="col">Type</th>
              <th scope="col">Value</th>
            </tr>
          </thead>
          <tbody>
            @for (parameter of parameters; track $index) {
              <tr>
                <td>
                  <code>{{ parameter.name }}</code>
                </td>
                <td>
                  <code>{{ parameter.type }}</code>
                </td>
                <td>
                  <code>{{ parameter.value }}</code>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
    }

    .sql {
      padding: 8px 0;
    }

    .about,
    .aside {
      margin: 0 0 8px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .editor {
      height: 260px;
    }

    h3 {
      margin: 16px 0 4px;
      font: var(--mat-sys-title-small);
    }

    .parameters {
      margin-top: 8px;
      border-collapse: collapse;
      font: var(--mat-sys-body-small);
    }

    th,
    td {
      padding: 2px 12px 2px 0;
      text-align: start;
    }

    code {
      font-family: var(--gd-code-font-family);
    }
  `,
})
export class QuerySql {
  private readonly locale = inject(LOCALE_ID);

  readonly explain = input.required<QueryExplain>();

  protected readonly shown = computed<ShownSql[]>(() => {
    const explain = this.explain();
    const fragments = explain.fragments.map((fragment): ShownSql => {
      const rows =
        fragment.estimatedRows === null
          ? ''
          : `; about ${fragment.estimatedRows.toLocaleString(this.locale)} rows`;
      return {
        // A fragment's table in the merge engine names it; a query of one source has none.
        label: fragment.table ? `${fragment.table} · ${fragment.source}` : fragment.source,
        about: `${fragment.source} (${fragment.dialect}): ${fragment.strategy}${rows}.`,
        language: sqlLanguageOf(fragment.dialect),
        sql: fragment.sql,
        parameters: fragment.parameters,
        batch:
          fragment.bindJoinTemplate === null
            ? null
            : { sql: fragment.bindJoinTemplate, parameters: fragment.bindJoinParameters },
      };
    });
    return explain.mergeSql === null
      ? fragments
      : [
          ...fragments,
          {
            label: 'Merge',
            about: 'The merge engine (DuckDB): the sources’ rows put together.',
            language: sqlLanguageOf('DuckDB'),
            sql: explain.mergeSql,
            parameters: explain.mergeParameters,
            batch: null,
          },
        ];
  });
}
