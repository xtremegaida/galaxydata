import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { settle } from '../../../testing/http';
import { FakeMonaco, fakeMonacoProviders } from '../../../testing/monaco';
import { textOf } from '../../../testing/pages';
import { explainOf } from '../../../testing/query';
import type { QueryExplain } from './query-plan';
import { QuerySql } from './query-sql';

@Component({
  imports: [QuerySql],
  template: `<gd-query-sql [explain]="explain()" />`,
})
class Host {
  readonly explain = signal<QueryExplain>(explainOf());
}

/** shop's orders fetched in full, wh's shipments by their keys, put together in the merge engine. */
function federated(): QueryExplain {
  const base = explainOf();
  return explainOf({
    fragments: [
      { ...base.fragments[0], table: 'f1', strategy: 'full fetch into f1', estimatedRows: 1000 },
      {
        source: 'wh',
        dialect: 'PostgreSQL',
        sql: 'SELECT s.order_id FROM shipments AS s',
        parameters: [{ name: '@p0', type: 'int64', value: '5' }],
        strategy: 'into f2, by the keys of f1.id when they are few enough, else in full',
        table: 'f2',
        estimatedRows: null,
        bindJoinTemplate: 'SELECT s.order_id FROM shipments AS s WHERE s.order_id IN (@k)',
        bindJoinParameters: [{ name: '@k', type: 'int64', value: 'the keys' }],
      },
    ],
    mergeSql: 'SELECT * FROM f1 JOIN f2 ON f1.id = f2.order_id',
    mergeParameters: [],
  });
}

describe('QuerySql', () => {
  async function opened(explain: QueryExplain, monaco = new FakeMonaco()) {
    TestBed.configureTestingModule({
      providers: [
        fakeMonacoProviders(monaco),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.explain.set(explain);
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement, monaco };
  }

  it("shows a query of one source's SQL, in an editor that can't be edited", async () => {
    const { element, monaco } = await opened(explainOf());
    expect([...element.querySelectorAll('[role=tab]')].map((tab) => textOf(tab))).toEqual(['shop']);
    expect(
      element.querySelector('[role=tablist]')?.closest('mat-tab-group')?.getAttribute('aria-label'),
    ).toBe('The SQL each source runs');
    expect(textOf(element.querySelector('.about'))).toBe(
      'shop (SQLite): whole result; about 250 rows.',
    );
    expect(monaco.last.model.getValue()).toBe('SELECT o.id FROM orders AS o WHERE o.total > 50');
    expect(monaco.last.model.language).toBe('sql');
    expect(monaco.last.options).toMatchObject({ readOnly: true, ariaLabel: 'SQL: shop' });
    expect(element.querySelector('table')).toBeNull();
  });

  it("shows each source's SQL, how its rows are fetched, its parameters, and the merge engine's", async () => {
    const { fixture, element, monaco } = await opened(federated());
    const tabs = [...element.querySelectorAll<HTMLElement>('[role=tab]')];
    expect(tabs.map((tab) => textOf(tab))).toEqual(['f1 · shop', 'f2 · wh', 'Merge']);
    tabs[1].click();
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();
    expect(textOf(element.querySelector('.mat-mdc-tab-body-active .about'))).toBe(
      'wh (PostgreSQL): into f2, by the keys of f1.id when they are few enough, else in full.',
    );
    expect(textOf(element.querySelector('.mat-mdc-tab-body-active h3'))).toBe(
      'For each batch of keys',
    );
    const tables = [...element.querySelectorAll('.mat-mdc-tab-body-active table')].map((table) =>
      [...table.querySelectorAll('tbody tr')].map((row) =>
        [...row.children].map((cell) => textOf(cell)).join(' | '),
      ),
    );
    expect(tables).toEqual([['@p0 | int64 | 5'], ['@k | int64 | the keys']]);
    const live = monaco.live.map((editor) => [editor.model.getValue(), editor.model.language]);
    expect(live).toContainEqual(['SELECT s.order_id FROM shipments AS s', 'pgsql']);
    expect(live).toContainEqual([
      'SELECT s.order_id FROM shipments AS s WHERE s.order_id IN (@k)',
      'pgsql',
    ]);

    tabs[2].click();
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();
    expect(textOf(element.querySelector('.mat-mdc-tab-body-active .about'))).toBe(
      'The merge engine (DuckDB): the sources’ rows put together.',
    );
    expect(monaco.live.map((editor) => editor.model.getValue())).toContain(
      'SELECT * FROM f1 JOIN f2 ON f1.id = f2.order_id',
    );
  });

  it('says when the query runs no SQL', async () => {
    const { element } = await opened(explainOf({ fragments: [], plan: null, nodes: [] }));
    expect(textOf(element)).toBe("The query runs no SQL: it can't be planned as it is.");
  });
});
