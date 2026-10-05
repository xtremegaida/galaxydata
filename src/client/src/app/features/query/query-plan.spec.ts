import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { textOf } from '../../../testing/pages';
import { explainOf, nodeOf } from '../../../testing/query';
import { type QueryExplain, QueryPlan, planTreeOf, sitesOf } from './query-plan';

/** Across two sources: a join in the merge engine of shop's orders and wh's shipments, a subquery of shop. */
function federated(): QueryExplain {
  return explainOf({
    summary: 'Runs across 2 sources (shop, wh), put together in the merge engine.',
    plan: 4,
    nodes: [
      nodeOf(0, 'Scan', { detail: 'shop.orders', site: 'shop', estimatedRows: 1000 }),
      nodeOf(1, 'Scan', { detail: 'wh.shipments', site: 'wh', estimatedRows: 1 }),
      nodeOf(2, 'Scan', { detail: 'shop.customers', site: 'shop' }),
      nodeOf(3, 'Join', { detail: 'inner on id', site: 'merge', inputs: [0, 1] }),
      nodeOf(4, 'Filter', {
        detail: 'total > s1',
        site: 'merge',
        inputs: [3],
        subqueries: [{ number: 1, kind: 'scalar', plan: 2 }],
      }),
    ],
  });
}

/** The texts of an element's parts, apart. */
function partsOf(element: Element): string {
  return [...element.children].map((part) => textOf(part)).join(' | ');
}

@Component({
  imports: [QueryPlan],
  template: `<gd-query-plan [explain]="explain()" />`,
})
class Host {
  readonly explain = signal(explainOf());
}

describe('QueryPlan', () => {
  it('makes the plan a tree, inputs and subqueries under the operators that use them', () => {
    const tree = planTreeOf(federated())!;
    expect(tree.node.operator).toBe('Filter');
    expect(tree.subqueries.map((subquery) => [subquery.number, subquery.plan.node.detail])).toEqual(
      [[1, 'shop.customers']],
    );
    expect(tree.inputs[0].inputs.map((input) => input.node.detail)).toEqual([
      'shop.orders',
      'wh.shipments',
    ]);
    expect(sitesOf(tree)).toEqual(['merge', 'shop', 'wh']);
    // Without a plan, none; inputs it hasn't are left out.
    expect(planTreeOf(explainOf({ plan: null }))).toBeNull();
    expect(
      planTreeOf(explainOf({ nodes: [nodeOf(1, 'Filter', { inputs: [9] })] }))?.inputs,
    ).toEqual([]);
    expect(sitesOf(null)).toEqual([]);
    // A plan that leads round to itself is shown so far.
    const round = planTreeOf(explainOf({ plan: 1, nodes: [nodeOf(1, 'Filter', { inputs: [1] })] }));
    let depth = 0;
    for (let node = round; node; node = node.inputs[0]) {
      depth++;
    }
    expect(depth).toBe(401);
  });

  function opened(explain = explainOf()) {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.explain.set(explain);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the summary and the operators, each with what it does and the rows it is thought to give', () => {
    const element = opened();
    expect(textOf(element.querySelector('.summary'))).toBe(
      'Runs as one SQLite query in shop, reading shop.orders.',
    );
    const nodes = [...element.querySelectorAll('.node')].map(partsOf);
    expect(nodes).toEqual([
      'Filter | total > 50 | about 250 rows',
      'Scan | shop.orders | about 1,000 rows',
    ]);
    expect(element.querySelector('ul.plan')?.getAttribute('aria-label')).toBe('Plan');
    // One site: none marked.
    expect(element.querySelector('.site')).toBeNull();
    expect(textOf(element.querySelector('details.text pre'))).toContain('Scan shop.orders');
  });

  it('marks where each operator runs when they run in more than one place, and shows subqueries', () => {
    const element = opened(federated());
    expect(textOf(element.querySelector('.sites'))).toBe('Runs in merge shop wh');
    expect([...element.querySelectorAll('.node')].map(partsOf)).toEqual([
      'Filter | total > s1 | merge',
      'Scan | shop.customers | shop',
      'Join | inner on id | merge',
      'Scan | shop.orders | shop | about 1,000 rows',
      'Scan | wh.shipments | wh | about 1 row',
    ]);
    expect(textOf(element.querySelector('.subquery .label'))).toBe('Subquery 1 (scalar)');
    const sites = [...element.querySelectorAll('.sites .site')].map((site) =>
      site.getAttribute('data-site'),
    );
    expect(sites).toEqual(['0', '1', '2']);
  });

  it("shows the plan after each of the optimizer's phases when asked, and says when there is no plan", () => {
    const element = opened(
      explainOf({
        plan: null,
        nodes: [],
        phases: [
          { name: 'lowered', rules: [], plan: 'Filter\n  Scan' },
          { name: 'pushdown', rules: ['PushFilter', 'PushFilter', 'Prune'], plan: 'Scan' },
        ],
      }),
    );
    expect(textOf(element.querySelector('.aside'))).toBe(
      "The query has no plan: it can't be planned as it is.",
    );
    expect(textOf(element.querySelector('h3'))).toBe("The optimizer's phases");
    expect(
      [...element.querySelectorAll('h3 ~ details summary')].map((summary) => textOf(summary)),
    ).toEqual(['lowered', 'pushdown (PushFilter ×2, Prune)', 'As text']);
  });
});
