import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { HttpParams } from '@angular/common/http';
import type { HttpTestingController } from '@angular/common/http/testing';
import type { Schema } from '../app/core/api/api-client';
import type { TreeNode, TreeRow } from '../app/core/catalog/catalog-tree-store';
import { catalogVersionHeader } from '../app/core/catalog/catalog-version';
import { requestTo } from './http';

export type EntityDto = Schema<'EntityDto'>;
export type CatalogDto = Schema<'CatalogDto'>;
export type TreeHitDto = Schema<'TreeHitDto'>;

/** A source's node: a SQLite connection whose schema is read, unless said otherwise. */
export function sourceNode(alias: string, changes: Partial<TreeNode> = {}): TreeNode {
  return {
    id: alias,
    kind: 'source',
    name: alias,
    hasChildren: true,
    label: null,
    sourceKind: 'sqlite',
    sourceKindName: 'SQLite',
    sourceIcon: 'database',
    status: 'ready',
    isReadOnly: false,
    ...changes,
  };
}

/** A schema's node, named by the last part of its id. */
export function schemaNode(id: string, changes: Partial<TreeNode> = {}): TreeNode {
  return {
    id,
    kind: 'schema',
    name: id.slice(id.lastIndexOf('.') + 1),
    hasChildren: true,
    ...changes,
  };
}

/** A table's node, named by the last part of its id: 1,200 rows, which the user may change. */
export function tableNode(id: string, changes: Partial<TreeNode> = {}): TreeNode {
  return {
    id,
    kind: 'table',
    name: id.slice(id.lastIndexOf('.') + 1),
    hasChildren: false,
    rows: 1200,
    editable: true,
    comment: null,
    ...changes,
  };
}

/** The address the tree asks for a node's children at (the top nodes' for null). */
export function childrenUrl(parent: string | null): string {
  return parent === null
    ? '/api/catalog/tree/children'
    : `/api/catalog/tree/children?${new HttpParams({ fromObject: { parent } }).toString()}`;
}

/** The address a search asks at. */
export function searchUrl(text: string, take = 50): string {
  return `/api/catalog/tree/search?${new HttpParams({ fromObject: { text, take } }).toString()}`;
}

/** Answers the tree's request for a node's children, with the catalog's version when given. */
export async function answerChildren(
  http: HttpTestingController,
  parent: string | null,
  nodes: TreeNode[],
  version = 'v1',
): Promise<void> {
  (await requestTo(http, childrenUrl(parent))).flush(
    { parent, nodes },
    { headers: { [catalogVersionHeader]: version } },
  );
}

/** A node found by search, under its ancestors (their ids, the top one first). */
export function hitOf(node: TreeNode, path: string[], columns: string[] | null = null): TreeHitDto {
  return { node, path, columns };
}

/** The rows a tree shows, as text: indented by level, each node's name. */
export function shownRows(rows: readonly TreeRow[]): string[] {
  return rows.map((row) => `${'  '.repeat(row.level - 1)}${row.node.name}`);
}

/** An entity as the server describes it: shop.orders, a table of shop the user may change. */
export function entityOf(changes: Partial<EntityDto> = {}): EntityDto {
  return {
    name: 'shop.orders',
    qualifiedName: 'shop.main.orders',
    kind: 'table',
    source: 'shop',
    schema: 'main',
    table: 'orders',
    comment: null,
    rowCountEstimate: 1200,
    hasTriggers: false,
    key: { name: null, columns: ['id'], isDeclared: false },
    uniqueKeys: [],
    displayColumn: 'status',
    columns: [
      {
        name: 'id',
        ordinal: 0,
        type: { kind: 'int64', nullable: false, text: 'int64' },
        nativeType: 'INTEGER',
        isKey: true,
        isIdentity: true,
        isComputed: false,
        hasDefault: false,
        isRowVersion: false,
        hidden: false,
        label: null,
        comment: null,
        canUpdate: false,
        insert: 'optional',
        readOnlyReason:
          "'id' is part of the key, which can't change: delete the row and insert it again",
      },
      {
        name: 'customer_id',
        ordinal: 1,
        type: { kind: 'int64', nullable: false, text: 'int64' },
        nativeType: 'INTEGER',
        isKey: false,
        isIdentity: false,
        isComputed: false,
        hasDefault: false,
        isRowVersion: false,
        hidden: false,
        label: 'Customer',
        comment: 'Who ordered',
        canUpdate: true,
        insert: 'required',
        readOnlyReason: null,
      },
      {
        name: 'status',
        ordinal: 2,
        type: { kind: 'string', nullable: true, text: 'string?' },
        nativeType: 'TEXT',
        isKey: false,
        isIdentity: false,
        isComputed: false,
        hasDefault: true,
        isRowVersion: false,
        hidden: false,
        label: null,
        comment: null,
        canUpdate: true,
        insert: 'optional',
        readOnlyReason: null,
      },
    ],
    navigations: [
      {
        name: 'customer',
        target: 'shop.customers',
        multiplicity: 'one',
        columns: ['customer_id'],
        targetColumns: ['id'],
        isInverse: false,
        origin: 'foreignKey',
        isEnforced: true,
        isCrossSource: false,
        hidden: false,
        inherited: false,
        overlay: null,
      },
      {
        name: 'order_lines',
        target: 'shop.order_lines',
        multiplicity: 'many',
        columns: ['id'],
        targetColumns: ['order_id'],
        isInverse: true,
        origin: 'foreignKey',
        isEnforced: false,
        isCrossSource: false,
        hidden: false,
        inherited: false,
        overlay: null,
      },
    ],
    capabilities: { canInsert: true, canUpdate: true, canDelete: true },
    query: null,
    problem: null,
    overlay: null,
    ...changes,
  };
}

/** The catalog: shop (read, 8 entities) and pg (being read), with nothing wrong, unless said otherwise. */
export function catalogOf(changes: Partial<CatalogDto> = {}): CatalogDto {
  return {
    version: 'v1',
    sources: [
      {
        alias: 'shop',
        kind: 'sqlite',
        displayName: 'The shop',
        status: 'ready',
        refreshedAt: '2026-10-04T12:00:00Z',
        isReadOnly: true,
        hasSchema: true,
        entities: 8,
        problem: null,
        kindName: 'SQLite',
        kindIcon: 'database',
      },
      {
        alias: 'pg',
        kind: 'postgres',
        displayName: null,
        status: 'ready',
        refreshedAt: '2026-10-04T12:00:00Z',
        isReadOnly: false,
        hasSchema: true,
        entities: 3,
        problem: null,
        kindName: 'PostgreSQL',
        kindIcon: 'database',
      },
    ],
    diagnostics: [],
    ...changes,
  };
}

/**
 * Gives the trees' viewports a height (jsdom lays nothing out, so they would render a few rows): tall enough for
 * `rows` rows of 32 pixels.
 */
export function viewportsFor(rows: number): void {
  vi.spyOn(CdkVirtualScrollViewport.prototype, 'getViewportSize').mockReturnValue(rows * 32);
}
