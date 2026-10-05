import { NgTemplateOutlet } from '@angular/common';
import { Component, LOCALE_ID, computed, inject, input } from '@angular/core';
import type { Schema } from '../../core/api/api-client';

export type QueryExplain = Schema<'QueryExplainDto'>;
type ExplainNode = Schema<'ExplainNodeDto'>;

/** A node of a plan with its inputs and subqueries, as a tree. */
export interface PlanNode {
  readonly node: ExplainNode;
  readonly inputs: readonly PlanNode[];
  readonly subqueries: readonly {
    readonly number: number;
    readonly kind: string;
    readonly plan: PlanNode;
  }[];
}

/** The deepest a plan is shown (plans are made of chains of methods, a level each). */
const maxDepth = 400;

/** A plan's nodes (a list, inputs by id) as a tree from its root; null without a plan. */
export function planTreeOf(explain: QueryExplain): PlanNode | null {
  const byId = new Map(explain.nodes.map((node) => [node.id, node]));
  const treeOf = (id: number, depth: number): PlanNode | null => {
    const node = byId.get(id);
    if (!node || depth > maxDepth) {
      return null;
    }
    return {
      node,
      inputs: node.inputs
        .map((input) => treeOf(input, depth + 1))
        .filter((input): input is PlanNode => input !== null),
      subqueries: node.subqueries.flatMap((subquery) => {
        const plan = treeOf(subquery.plan, depth + 1);
        return plan ? [{ number: subquery.number, kind: subquery.kind, plan }] : [];
      }),
    };
  };
  return explain.plan === null ? null : treeOf(explain.plan, 0);
}

/** The sites a plan's operators run at, in the order first met. */
export function sitesOf(tree: PlanNode | null): string[] {
  const sites: string[] = [];
  const visit = (node: PlanNode) => {
    const site = node.node.site;
    if (site !== null && !sites.includes(site)) {
      sites.push(site);
    }
    node.inputs.forEach(visit);
    node.subqueries.forEach((subquery) => visit(subquery.plan));
  };
  if (tree) {
    visit(tree);
  }
  return sites;
}

/**
 * How a query would run: its summary, then its plan as a tree of operators (each with what it does, where it runs,
 * and how many rows it is thought to give), its subqueries under the operators that use them; the plan after each
 * phase of the optimizer, when asked; and the whole explanation as `gdq explain` writes it.
 */
@Component({
  selector: 'gd-query-plan',
  imports: [NgTemplateOutlet],
  template: `
    @let explained = explain();
    <p class="summary">{{ explained.summary }}</p>
    @if (tree(); as tree) {
      @if (sites().length > 1) {
        <p class="sites">
          Runs in
          @for (site of sites(); track site; let last = $last) {
            <span class="site" [attr.data-site]="siteIndex(site)">{{ site }}</span>
            {{ last ? '' : ' ' }}
          }
        </p>
      }
      <ul class="plan" aria-label="Plan">
        <ng-container *ngTemplateOutlet="nodeTemplate; context: { $implicit: tree }" />
      </ul>
    } @else {
      <p class="aside">The query has no plan: it can't be planned as it is.</p>
    }

    @if (explained.phases; as phases) {
      <h3>The optimizer's phases</h3>
      @for (phase of phases; track $index) {
        <details>
          <summary>
            {{ phase.name }}
            @if (phase.rules.length > 0) {
              <span class="aside">({{ rulesText(phase.rules) }})</span>
            }
          </summary>
          <pre>{{ phase.plan }}</pre>
        </details>
      }
    }

    <details class="text">
      <summary>As text</summary>
      <pre>{{ explained.text }}</pre>
    </details>

    <ng-template #nodeTemplate let-plan>
      <li>
        <div class="node">
          <span class="operator">{{ plan.node.operator }}</span>
          @if (plan.node.detail) {
            <code class="detail">{{ plan.node.detail }}</code>
          }
          @if (plan.node.site && sites().length > 1) {
            <span class="site" [attr.data-site]="siteIndex(plan.node.site)">{{
              plan.node.site
            }}</span>
          }
          @if (plan.node.estimatedRows !== null) {
            <span class="rows">{{ rowsText(plan.node.estimatedRows) }}</span>
          }
        </div>
        @if (plan.inputs.length > 0 || plan.subqueries.length > 0) {
          <ul>
            @for (subquery of plan.subqueries; track subquery.number) {
              <li class="subquery">
                <span class="label">Subquery {{ subquery.number }} ({{ subquery.kind }})</span>
                <ul>
                  <ng-container
                    *ngTemplateOutlet="nodeTemplate; context: { $implicit: subquery.plan }"
                  />
                </ul>
              </li>
            }
            @for (input of plan.inputs; track $index) {
              <ng-container *ngTemplateOutlet="nodeTemplate; context: { $implicit: input }" />
            }
          </ul>
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

    .summary {
      margin: 8px 0;
    }

    .plan,
    .plan ul {
      margin: 0;
      padding-inline-start: 0;
      list-style: none;
    }

    .plan ul {
      margin-inline-start: 8px;
      padding-inline-start: 12px;
      border-inline-start: 1px solid var(--mat-sys-outline-variant);
    }

    .node {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: 4px 8px;
      padding: 2px 0;
    }

    .operator {
      font-weight: 500;
    }

    .detail,
    pre {
      font-family: var(--gd-code-font-family);
      font-size: 13px;
    }

    .detail {
      overflow-wrap: anywhere;
    }

    .rows,
    .aside {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .label {
      color: var(--mat-sys-on-surface-variant);
      font-style: italic;
    }

    .site {
      padding: 0 6px;
      border-radius: var(--mat-sys-corner-small);
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
      font: var(--mat-sys-label-small);
    }

    .site[data-site='1'] {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }

    .site[data-site='2'] {
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }

    h3 {
      margin: 16px 0 4px;
      font: var(--mat-sys-title-small);
    }

    details {
      margin: 4px 0;
    }

    .text {
      margin-top: 16px;
    }

    pre {
      margin: 4px 0;
      padding: 8px;
      overflow: auto;
      border-radius: var(--mat-sys-corner-small);
      background: var(--mat-sys-surface-container);
    }
  `,
})
export class QueryPlan {
  private readonly locale = inject(LOCALE_ID);

  readonly explain = input.required<QueryExplain>();

  protected readonly tree = computed(() => planTreeOf(this.explain()));
  protected readonly sites = computed(() => sitesOf(this.tree()));

  /** A site's place among the plan's (for its colour, three of them). */
  protected siteIndex(site: string): number {
    return this.sites().indexOf(site) % 3;
  }

  protected rowsText(rows: number): string {
    return rows === 1 ? 'about 1 row' : `about ${rows.toLocaleString(this.locale)} rows`;
  }

  /** The rules a phase applied, each once with how many times. */
  protected rulesText(rules: readonly string[]): string {
    const counts = new Map<string, number>();
    rules.forEach((rule) => counts.set(rule, (counts.get(rule) ?? 0) + 1));
    return [...counts].map(([rule, count]) => (count > 1 ? `${rule} ×${count}` : rule)).join(', ');
  }
}
