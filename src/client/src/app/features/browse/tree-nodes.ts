import type { TreeNode } from '../../core/catalog/catalog-tree-store';

/** Whether a node is an entity, whose rows can be browsed: a table, a view or a virtual entity. */
export function isEntity(node: TreeNode): boolean {
  return node.kind === 'table' || node.kind === 'view' || node.kind === 'virtual';
}

const kinds: Readonly<Record<TreeNode['kind'], string>> = {
  source: 'connection',
  schema: 'schema',
  folder: 'folder of virtual entities',
  table: 'table',
  view: 'view',
  virtual: 'virtual entity',
};

/** What a node is, in words. */
export function kindOf(node: TreeNode): string {
  return kinds[node.kind];
}

/** A node's icon (a Material Symbols name). */
export function iconOf(node: TreeNode): string {
  switch (node.kind) {
    case 'source':
      return node.sourceKind === 'excel' ? 'table_view' : 'database';
    case 'schema':
      return 'folder';
    case 'folder':
      return 'folder_special';
    case 'table':
      return 'table';
    case 'view':
      return 'table_eye';
    case 'virtual':
      return 'dataset';
  }
}

const sourceKinds: Readonly<Record<string, string>> = {
  postgres: 'PostgreSQL',
  sqlserver: 'SQL Server',
  sqlite: 'SQLite',
  duckdb: 'DuckDB',
  excel: 'Excel folder',
};

/** The name of a kind of connection (as the server's kinds name them), or its id when it is one the client doesn't know. */
export function sourceKindName(kind: string): string {
  return sourceKinds[kind] ?? kind;
}

/** A row count as it is read at a glance (950, 12K, 1.2M): the databases' estimates, which needn't be exact. */
export function compactCount(count: number, locale: string): string {
  return new Intl.NumberFormat(locale, { notation: 'compact', maximumFractionDigits: 1 }).format(
    count,
  );
}

/** A part of a text, and whether it is what was looked for. */
export interface TextPart {
  readonly text: string;
  readonly match: boolean;
}

/** A text in parts: those that are `wanted` (ignoring case), and those between. */
export function matchParts(text: string, wanted: string): TextPart[] {
  const needle = wanted.trim().toLowerCase();
  if (!needle) {
    return [{ text, match: false }];
  }
  const haystack = text.toLowerCase();
  // A letter whose lower case is longer (İ) would put the parts out of step with the text.
  if (haystack.length !== text.length) {
    return [{ text, match: false }];
  }
  const parts: TextPart[] = [];
  let from = 0;
  for (let at = haystack.indexOf(needle); at >= 0; at = haystack.indexOf(needle, from)) {
    if (at > from) {
      parts.push({ text: text.slice(from, at), match: false });
    }
    parts.push({ text: text.slice(at, at + needle.length), match: true });
    from = at + needle.length;
  }
  if (from < text.length) {
    parts.push({ text: text.slice(from), match: false });
  }
  return parts;
}
