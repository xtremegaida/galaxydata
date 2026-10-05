import type { Schema } from '../app/core/api/api-client';

export type ResultColumnDto = Schema<'ResultColumnDto'>;
export type QueryPageDto = Schema<'QueryPageDto'>;
export type QueryExplainDto = Schema<'QueryExplainDto'>;
export type ExplainNodeDto = Schema<'ExplainNodeDto'>;
export type QueryValidationDto = Schema<'QueryValidationDto'>;
export type SavedQueryDto = Schema<'SavedQueryDto'>;
export type SavedQuerySummaryDto = Schema<'SavedQuerySummaryDto'>;
export type DiagnosticDto = Schema<'DiagnosticDto'>;
export type ScalarKind = Schema<'ScalarKind'>;

export const validateUrl = '/api/query/validate';
export const executeUrl = '/api/query/execute';
export const explainUrl = '/api/query/explain';
export const linkUrl = '/api/query/link';
export const savedUrl = '/api/saved-queries';

/** A column of a query's rows, read from shop.orders: text that may be null, unless said otherwise. */
export function resultColumnOf(
  name: string,
  ordinal: number,
  kind: ScalarKind = 'string',
  changes: Partial<ResultColumnDto> = {},
): ResultColumnDto {
  return {
    name,
    ordinal,
    type: { kind, nullable: true, text: `${kind === 'decimal' ? 'decimal(10,2)' : kind}?` },
    hidden: false,
    lineage: {
      kind: 'direct',
      sources: [{ column: `shop.orders.${name}`, path: null }],
      expression: null,
    },
    link: null,
    editTarget: null,
    ...changes,
  };
}

/**
 * The columns of `shop.orders.select(id, customer, total)`: its id, the customer's name (leading to the customer,
 * by its key, hidden at 3) and the total.
 */
export function orderResultColumns(): ResultColumnDto[] {
  return [
    resultColumnOf('id', 0, 'int64'),
    resultColumnOf('customer', 1, 'string', {
      link: {
        kind: 'row',
        ordinals: [3],
        target: 'shop.customers',
        navigation: 'customer',
        multiplicity: 'one',
      },
      lineage: {
        kind: 'direct',
        sources: [{ column: 'shop.customers.name', path: 'customer' }],
        expression: null,
      },
    }),
    resultColumnOf('total', 2, 'decimal'),
    resultColumnOf('id', 3, 'int64', { hidden: true }),
  ];
}

/** Rows of those columns, ids from 1001: Acme (customer 42) in turn with none. */
export function orderResultRows(count: number, from = 0): Schema<'ResultRowDto'>[] {
  return Array.from({ length: count }, (_, index) => {
    const at = from + index;
    return {
      id: null,
      v: [String(1001 + at), at % 2 ? null : 'Acme', `${at}.50`, at % 2 ? null : '42'],
    };
  });
}

/** A page of a query's rows, all there are unless said otherwise; with the schema of `columns` when given. */
export function queryPageOf(
  rows: Schema<'ResultRowDto'>[],
  changes: Partial<QueryPageDto> = {},
  columns: ResultColumnDto[] | null = orderResultColumns(),
): QueryPageDto {
  return {
    queryText: 'shop.orders.select(id, customer, total)',
    parameters: [],
    schema: columns ? { columns, rowIdentity: null } : null,
    rows,
    offset: 0,
    hasMore: false,
    total: rows.length,
    stats: { elapsedMs: 12.5, fetchedRows: rows.length, keysSent: 0, fragments: [] },
    warnings: [],
    ...changes,
  };
}

/** What checking a query found: nothing wrong, unless said, with the parameters given. */
export function validationOf(changes: Partial<QueryValidationDto> = {}): QueryValidationDto {
  return {
    success: true,
    complete: true,
    diagnostics: [],
    parameters: [],
    columns: [],
    ...changes,
  };
}

/** A diagnostic of a query's text, from `start` to `end`. */
export function diagnosticOf(
  start: number,
  end: number,
  message = "There is no 'totl' here",
  changes: Partial<DiagnosticDto> = {},
): DiagnosticDto {
  return { code: 'GDQ2001', severity: 'error', message, start, end, ...changes };
}

/** A node of a plan. */
export function nodeOf(
  id: number,
  operator: string,
  changes: Partial<ExplainNodeDto> = {},
): ExplainNodeDto {
  return {
    id,
    operator,
    detail: null,
    site: null,
    columns: [],
    estimatedRows: null,
    inputs: [],
    subqueries: [],
    ...changes,
  };
}

/** How `shop.orders.where(total > 50)` runs: a filter over a scan, in SQLite. */
export function explainOf(changes: Partial<QueryExplainDto> = {}): QueryExplainDto {
  return {
    queryText: 'shop.orders.where(total > 50)',
    parameters: [],
    summary: 'Runs as one SQLite query in shop, reading shop.orders.',
    diagnostics: [],
    schema: null,
    plan: 1,
    nodes: [
      nodeOf(0, 'Scan', { detail: 'shop.orders', site: 'shop', estimatedRows: 1000 }),
      nodeOf(1, 'Filter', { detail: 'total > 50', site: 'shop', estimatedRows: 250, inputs: [0] }),
    ],
    fragments: [
      {
        source: 'shop',
        dialect: 'SQLite',
        sql: 'SELECT o.id FROM orders AS o WHERE o.total > 50',
        parameters: [],
        strategy: 'whole result',
        table: null,
        estimatedRows: 250,
        bindJoinTemplate: null,
        bindJoinParameters: [],
      },
    ],
    mergeSql: null,
    mergeParameters: [],
    phases: null,
    text: 'Runs as one SQLite query in shop, reading shop.orders.\n\nPlan\n  Filter total > 50\n    Scan shop.orders',
    ...changes,
  };
}

/** A saved query of the user's, not shared, with its parameters' values. */
export function savedQueryOf(changes: Partial<SavedQueryDto> = {}): SavedQueryDto {
  return {
    id: 7,
    name: 'Big orders',
    description: null,
    text: 'shop.orders.where(total > $min)',
    parameters: [{ name: 'min', type: 'decimal', value: '50' }],
    owner: 'admin',
    isShared: false,
    isMine: true,
    canEdit: true,
    createdAt: '2026-10-01T08:00:00Z',
    updatedAt: '2026-10-02T09:30:00Z',
    version: 3,
    ...changes,
  };
}

/** A saved query as the list has it. */
export function summaryOf(query: SavedQueryDto): SavedQuerySummaryDto {
  const { id, name, description, owner, isShared, isMine, canEdit, updatedAt, version } = query;
  return { id, name, description, owner, isShared, isMine, canEdit, updatedAt, version };
}
