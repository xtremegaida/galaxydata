import type { Schema } from '../api/api-client';

type OverlayItemKind = Schema<'OverlayItemKind'>;

/** Each kind of the overlay's items' part of the API's and the administrators' pages' paths. */
export const kindPaths: Readonly<Record<OverlayItemKind, string>> = {
  relation: 'relations',
  navigation: 'navigations',
  virtualEntity: 'virtual-entities',
  entitySettings: 'entity-settings',
};

/** An item's page. */
export function itemLink(kind: OverlayItemKind, id: number): string[] {
  return ['/admin/overlay', kindPaths[kind], String(id)];
}

/**
 * The page for a new item of a kind, which query parameters start with what they give: `entity` (settings, overrides),
 * `navigation` (overrides), `from` (relations).
 */
export function newItemLink(kind: OverlayItemKind): string[] {
  return ['/admin/overlay', kindPaths[kind], 'new'];
}
