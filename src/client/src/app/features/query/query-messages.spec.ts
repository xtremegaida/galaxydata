import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { Problem } from '../../core/api/problem';
import { textOf } from '../../../testing/pages';
import { diagnosticOf, validationOf } from '../../../testing/query';
import { type Checked, QueryMessages, positionOf, problemDiagnostics } from './query-messages';
import type { QueryRun, RunOutcome } from './query-results';

const text = 'shop.orders\n  .where(totl > 5)';
const run: QueryRun = { text, parameters: [], serial: 1 };

@Component({
  imports: [QueryMessages],
  template: `
    <gd-query-messages
      [text]="text()"
      [checked]="checked()"
      [outcome]="outcome()"
      (goTo)="went.push($event)"
    />
  `,
})
class Host {
  readonly text = signal(text);
  readonly checked = signal<Checked | null>(null);
  readonly outcome = signal<RunOutcome | null>(null);
  readonly went: number[] = [];
}

/** A problem the API answered a query with: its diagnostics, placed in the query it was about. */
function queryProblem(queryText: string): Problem {
  return {
    status: 422,
    code: 'query-invalid',
    title: "The query can't be run",
    body: {
      queryText,
      diagnostics: [{ code: 'GDQ2001', severity: 'error', message: 'No totl', start: 21, end: 25 }],
    },
  };
}

/** The texts of an element's parts, apart. */
function partsOf(element: Element): string {
  return [...element.children].map((part) => textOf(part)).join(' | ');
}

describe('QueryMessages', () => {
  it('finds where offsets are, by line and column', () => {
    expect(positionOf(text, 0)).toEqual({ line: 1, column: 1 });
    expect(positionOf(text, 21)).toEqual({ line: 2, column: 10 });
    expect(positionOf(text, 999)).toEqual({ line: 2, column: 19 });
    expect(problemDiagnostics(queryProblem(text).body, text)).toEqual([
      { code: 'GDQ2001', severity: 'error', message: 'No totl', start: 21, end: 25 },
    ]);
    // Placed in another text (the grid's composed onto it), they aren't the query's.
    expect(problemDiagnostics(queryProblem(`(${text})`).body, text)).toEqual([]);
    expect(problemDiagnostics(undefined, text)).toEqual([]);
  });

  function opened() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const sections = () => [...element.querySelectorAll('section')];
    return { fixture, host: fixture.componentInstance, element, sections };
  }

  it('says what checking found, each where it is, and goes there', () => {
    const { fixture, host, sections } = opened();
    expect(partsOf(sections()[0])).toBe('The query | Not checked yet.');
    host.checked.set({ text, validation: validationOf() });
    fixture.detectChanges();
    expect(partsOf(sections()[0])).toBe('The query | Nothing wrong found.');
    host.checked.set({ text, validation: validationOf({ complete: false }) });
    fixture.detectChanges();
    expect(partsOf(sections()[0])).toBe('The query | Nothing wrong found so far.');

    host.checked.set({
      text,
      validation: validationOf({
        success: false,
        diagnostics: [
          diagnosticOf(21, 25),
          diagnosticOf(0, 4, 'Hidden by a subtree', { severity: 'warning', code: 'GDQ2101' }),
        ],
      }),
    });
    fixture.detectChanges();
    const items = [...sections()[0].querySelectorAll('li')];
    expect(items.map((item) => item.className)).toEqual(['diagnostic error', 'diagnostic warning']);
    expect(textOf(items[0].querySelector('.text'))).toBe("error: There is no 'totl' here GDQ2001");
    const button = items[0].querySelector('button')!;
    expect(textOf(button)).toBe('Line 2, column 10');
    expect(button.getAttribute('aria-label')).toBe('Go to line 2, column 10');
    button.click();
    expect(host.went).toEqual([21]);

    // Of another text than the editor's, where it was then.
    host.text.set(`${text} `);
    fixture.detectChanges();
    expect(sections()[0].querySelector('button')).toBeNull();
    expect(textOf(sections()[0].querySelector('.where-then'))).toBe('(line 2, column 10, as run)');
  });

  it('says what the last run took, from each source', () => {
    const { fixture, host, sections } = opened();
    expect(partsOf(sections()[1])).toBe("The last run | The query hasn't run yet.");
    host.outcome.set({
      kind: 'read',
      run,
      stats: {
        elapsedMs: 1520,
        fetchedRows: 12345,
        keysSent: 3,
        fragments: [
          {
            source: 'shop',
            table: 'f1',
            rows: 12000,
            elapsedMs: 0.4,
            strategy: 'full',
            keys: 0,
            batches: 0,
          },
          {
            source: 'wh',
            table: 'f2',
            rows: 345,
            elapsedMs: 80,
            strategy: 'keys',
            keys: 3,
            batches: 1,
          },
        ],
      },
      warnings: [diagnosticOf(0, 4, 'Fetches a lot', { severity: 'warning', code: 'GDQ3101' })],
    });
    fixture.detectChanges();
    const section = sections()[1];
    expect(textOf(section.querySelector('p'))).toBe(
      'The page shown took 1.5 s: 12,345 rows fetched from the sources, 3 keys sent to look rows up by.',
    );
    expect(
      [...section.querySelectorAll('tbody tr')].map((row) =>
        [...row.children].map((cell) => textOf(cell)).join(' | '),
      ),
    ).toEqual([
      'shop | f1 | 12,000 | under 1 ms | in full',
      'wh | f2 | 345 | 80 ms | by the keys of another (3 keys in 1 batch)',
    ]);
    expect(textOf(section.querySelector('li .text'))).toBe('warning: Fetches a lot GDQ3101');
  });

  it("says why it couldn't run, and where in the query", () => {
    const { fixture, host, sections } = opened();
    host.outcome.set({ kind: 'failed', run, problem: queryProblem(text) });
    fixture.detectChanges();
    expect(textOf(sections()[1].querySelector('.failed'))).toBe(
      "It couldn't run: The query can't be run.",
    );
    expect(textOf(sections()[1].querySelector('button'))).toBe('Line 2, column 10');

    host.outcome.set({ kind: 'stopped', run });
    fixture.detectChanges();
    expect(partsOf(sections()[1])).toBe('The last run | It was stopped before its rows came.');
  });
});
