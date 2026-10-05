import { NgTemplateOutlet } from '@angular/common';
import { Component, LOCALE_ID, computed, inject, input, output } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import type { Schema } from '../../core/api/api-client';
import { problemMessage } from '../../core/api/problem';
import type { Diagnostic } from './query-datasource';
import type { RunOutcome } from './query-results';
import { timeText } from './time-text';

export type QueryValidation = Schema<'QueryValidationDto'>;

/** A query checked: the text it was checked as, and what was found. */
export interface Checked {
  readonly text: string;
  readonly validation: QueryValidation;
}

/** A diagnostic as the messages list it: where it is in the text (from 1), when it can be gone to. */
export interface PlacedDiagnostic {
  readonly diagnostic: Diagnostic;
  readonly line: number;
  readonly column: number;
  /** Whether it is in the text as it is now, so the editor can go there. */
  readonly current: boolean;
}

/** Where an offset of a text is: its line and column, from 1. */
export function positionOf(text: string, offset: number): { line: number; column: number } {
  const before = text.slice(0, Math.max(0, Math.min(offset, text.length)));
  const lines = before.split('\n');
  return { line: lines.length, column: (lines.at(-1)?.length ?? 0) + 1 };
}

/** The diagnostics of a problem the API answered with, when they are placed in `text` (the query as written). */
export function problemDiagnostics(
  body: Readonly<Record<string, unknown>> | undefined,
  text: string,
): Diagnostic[] {
  if (!body || body['queryText'] !== text || !Array.isArray(body['diagnostics'])) {
    return [];
  }
  return (body['diagnostics'] as Record<string, unknown>[]).map((diagnostic) => ({
    code: String(diagnostic['code'] ?? ''),
    severity:
      diagnostic['severity'] === 'warning' || diagnostic['severity'] === 'info'
        ? diagnostic['severity']
        : 'error',
    message: String(diagnostic['message'] ?? ''),
    start: typeof diagnostic['start'] === 'number' ? diagnostic['start'] : 0,
    end: typeof diagnostic['end'] === 'number' ? diagnostic['end'] : 0,
  }));
}

type FetchStrategy = Schema<'FetchStrategy'>;

/** How a source's rows were fetched, in words. */
const strategies: Readonly<Record<FetchStrategy, string>> = {
  full: 'in full',
  keys: 'by the keys of another',
  skipped: 'not at all: there were no keys to look up',
  value: 'as a value, first',
};

const severityIcons: Readonly<Record<Diagnostic['severity'], string>> = {
  error: 'error',
  warning: 'warning',
  info: 'info',
};

/**
 * What is said of the query: what checking it as it is typed finds wrong (each where it is, gone to on asking), and
 * what came of its last run: what it took (the rows fetched, the keys sent, each source's part), its warnings, or why
 * it couldn't run.
 */
@Component({
  selector: 'gd-query-messages',
  imports: [MatButton, MatIcon, NgTemplateOutlet],
  template: `
    <section aria-labelledby="gd-messages-query">
      <h3 id="gd-messages-query">The query</h3>
      @if (checked(); as checked) {
        @if (found().length === 0) {
          <p>
            {{
              checked.validation.complete ? 'Nothing wrong found.' : 'Nothing wrong found so far.'
            }}
          </p>
        } @else {
          <ul class="diagnostics">
            @for (placed of found(); track $index) {
              <ng-container
                [ngTemplateOutlet]="diagnosticTemplate"
                [ngTemplateOutletContext]="{ $implicit: placed }"
              />
            }
          </ul>
        }
      } @else {
        <p class="aside">Not checked yet.</p>
      }
    </section>

    <section aria-labelledby="gd-messages-run">
      <h3 id="gd-messages-run">The last run</h3>
      @if (outcome(); as outcome) {
        @switch (outcome.kind) {
          @case ('read') {
            <p>{{ statsText() }}</p>
            @if (outcome.stats.fragments.length > 0) {
              <table class="fragments" aria-label="What each source fetched">
                <thead>
                  <tr>
                    <th scope="col">Source</th>
                    <th scope="col">Into</th>
                    <th scope="col">Rows</th>
                    <th scope="col">Time</th>
                    <th scope="col">How</th>
                  </tr>
                </thead>
                <tbody>
                  @for (fragment of outcome.stats.fragments; track $index) {
                    <tr>
                      <td>{{ fragment.source }}</td>
                      <td>
                        <code>{{ fragment.table }}</code>
                      </td>
                      <td class="number">{{ number(fragment.rows) }}</td>
                      <td class="number">{{ time(fragment.elapsedMs) }}</td>
                      <td>
                        {{ strategyText(fragment.strategy) }}
                        @if (fragment.keys > 0) {
                          ({{ number(fragment.keys) }} keys in {{ number(fragment.batches) }}
                          {{ fragment.batches === 1 ? 'batch' : 'batches' }})
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }
            @if (runFound().length > 0) {
              <ul class="diagnostics">
                @for (placed of runFound(); track $index) {
                  <ng-container
                    [ngTemplateOutlet]="diagnosticTemplate"
                    [ngTemplateOutletContext]="{ $implicit: placed }"
                  />
                }
              </ul>
            }
          }
          @case ('failed') {
            <p class="failed">It couldn't run: {{ message(outcome.problem) }}</p>
            @if (runFound().length > 0) {
              <ul class="diagnostics">
                @for (placed of runFound(); track $index) {
                  <ng-container
                    [ngTemplateOutlet]="diagnosticTemplate"
                    [ngTemplateOutletContext]="{ $implicit: placed }"
                  />
                }
              </ul>
            }
          }
          @case ('stopped') {
            <p>It was stopped before its rows came.</p>
          }
        }
      } @else {
        <p class="aside">The query hasn't run yet.</p>
      }
    </section>

    <ng-template #diagnosticTemplate let-placed>
      <li class="diagnostic" [class]="placed.diagnostic.severity">
        <mat-icon class="icon" aria-hidden="true">{{
          iconOf(placed.diagnostic.severity)
        }}</mat-icon>
        <span class="text">
          <span class="cdk-visually-hidden">{{ placed.diagnostic.severity }}:</span>
          {{ placed.diagnostic.message }}
          <span class="code">{{ placed.diagnostic.code }}</span>
        </span>
        @if (placed.current) {
          <button
            matButton
            type="button"
            class="where"
            [attr.aria-label]="'Go to line ' + placed.line + ', column ' + placed.column"
            (click)="goTo.emit(placed.diagnostic.start)"
          >
            Line {{ placed.line }}, column {{ placed.column }}
          </button>
        } @else {
          <span class="where-then"
            >(line {{ placed.line }}, column {{ placed.column }}, as run)</span
          >
        }
      </li>
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
      overflow: auto;
      font: var(--mat-sys-body-medium);
    }

    h3 {
      margin: 12px 0 4px;
      font: var(--mat-sys-title-small);
    }

    p {
      margin: 4px 0;
    }

    .aside,
    .where-then,
    .code {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .diagnostics {
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .diagnostic {
      display: flex;
      align-items: center;
      gap: 8px;
      min-height: 36px;
    }

    .icon {
      flex: none;
      font-size: 20px;
      width: 20px;
      height: 20px;
    }

    .error .icon,
    .failed {
      color: var(--mat-sys-error);
    }

    .warning .icon {
      color: var(--mat-sys-tertiary);
    }

    .text {
      flex: 1;
    }

    .fragments {
      border-collapse: collapse;
      font: var(--mat-sys-body-small);
    }

    th,
    td {
      padding: 2px 12px 2px 0;
      text-align: start;
    }

    .number {
      text-align: end;
      font-variant-numeric: tabular-nums;
    }

    code {
      font-family: var(--gd-code-font-family);
    }
  `,
})
export class QueryMessages {
  private readonly locale = inject(LOCALE_ID);

  /** The query as it is now. */
  readonly text = input.required<string>();
  /** What checking it found, last. */
  readonly checked = input<Checked | null>(null);
  /** What came of its last run. */
  readonly outcome = input<RunOutcome | null>(null);
  /** The editor is to go to an offset of the text as it is now. */
  readonly goTo = output<number>();

  protected readonly message = problemMessage;

  protected readonly found = computed(() => {
    const checked = this.checked();
    return checked ? this.placed(checked.validation.diagnostics, checked.text) : [];
  });
  protected readonly runFound = computed(() => {
    const outcome = this.outcome();
    switch (outcome?.kind) {
      case 'read':
        return this.placed(outcome.warnings, outcome.run.text);
      case 'failed':
        return this.placed(
          problemDiagnostics(outcome.problem.body, outcome.run.text),
          outcome.run.text,
        );
      default:
        return [];
    }
  });
  protected readonly statsText = computed(() => {
    const outcome = this.outcome();
    if (outcome?.kind !== 'read') {
      return '';
    }
    const stats = outcome.stats;
    const keys =
      stats.keysSent > 0 ? `, ${this.number(stats.keysSent)} keys sent to look rows up by` : '';
    return `The page shown took ${this.time(stats.elapsedMs)}: ${this.number(stats.fetchedRows)} rows fetched from the sources${keys}.`;
  });

  protected iconOf(severity: Diagnostic['severity']): string {
    return severityIcons[severity] ?? 'error';
  }

  protected number(value: number): string {
    return value.toLocaleString(this.locale);
  }

  protected time(ms: number): string {
    return timeText(ms, this.locale);
  }

  protected strategyText(strategy: FetchStrategy): string {
    return strategies[strategy];
  }

  private placed(diagnostics: readonly Diagnostic[], text: string): PlacedDiagnostic[] {
    const current = text === this.text();
    return diagnostics.map((diagnostic) => ({
      diagnostic,
      ...positionOf(text, diagnostic.start),
      current,
    }));
  }
}
