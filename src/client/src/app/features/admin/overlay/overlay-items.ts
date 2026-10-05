import { InjectionToken } from '@angular/core';
import type { Schema } from '../../../core/api/api-client';

export { itemLink, kindPaths } from '../../../core/catalog/overlay-paths';

export type Overlay = Schema<'OverlayDto'>;
export type Relation = Schema<'RelationDto'>;
export type RelationInput = Schema<'RelationInput'>;
export type NavigationOverride = Schema<'NavigationOverrideDto'>;
export type NavigationOverrideInput = Schema<'NavigationOverrideInput'>;
export type VirtualEntity = Schema<'VirtualEntityDto'>;
export type VirtualEntityInput = Schema<'VirtualEntityInput'>;
export type EntitySettings = Schema<'EntitySettingsDto'>;
export type EntitySettingsInput = Schema<'EntitySettingsInput'>;
export type ColumnSetting = Schema<'ColumnSettingDto'>;
export type OverlayIssue = Schema<'OverlayIssueDto'>;
export type OverlayCheck = Schema<'OverlayCheckDto'>;
export type OverlayItemKind = Schema<'OverlayItemKind'>;
export type Severity = Schema<'DiagnosticSeverity'>;

/** Any item of the overlay, as the server gives it. */
export type OverlayItem = Relation | NavigationOverride | VirtualEntity | EntitySettings;

/**
 * How long the pages wait, in milliseconds, for typing to pause: before trying an item against the catalog (`check`),
 * and before looking up the entities and columns it names (`lookup`).
 */
export const OVERLAY_WAITS = new InjectionToken<{
  readonly check: number;
  readonly lookup: number;
}>('OVERLAY_WAITS', { factory: () => ({ check: 400, lookup: 250 }) });

/** Each kind, in messages: "the relation", "the virtual entity". */
export const kindNouns: Readonly<Record<OverlayItemKind, string>> = {
  relation: 'relation',
  navigation: 'navigation override',
  virtualEntity: 'virtual entity',
  entitySettings: 'entity settings',
};

/** A relation in words: `shop.orders (customer_id) → crm.customers (id)`. */
export function relationText(relation: {
  readonly from: string;
  readonly fromColumns: readonly string[];
  readonly to: string;
  readonly toColumns: readonly string[];
}): string {
  return `${relation.from} (${relation.fromColumns.join(', ')}) → ${relation.to} (${relation.toColumns.join(', ')})`;
}

/** What an override does to its navigation: "Renamed buyer", "Hidden", "Renamed buyer, and hidden". */
export function overrideText(override: {
  readonly renameTo?: string | null;
  readonly hidden: boolean;
}): string {
  if (override.renameTo && override.hidden) {
    return `Renamed ${override.renameTo}, and hidden`;
  }
  return override.renameTo ? `Renamed ${override.renameTo}` : 'Hidden';
}

/** What an entity's settings set, a part each: "Key: id", "Shown by name", "Hidden", "3 columns' settings". */
export function settingsParts(settings: {
  readonly key?: readonly string[] | null;
  readonly displayColumn?: string | null;
  readonly hidden: boolean;
  readonly columns?: readonly ColumnSetting[] | null;
}): string[] {
  const parts: string[] = [];
  if (settings.key?.length) {
    parts.push(`Key: ${settings.key.join(', ')}`);
  }
  if (settings.displayColumn) {
    parts.push(`Shown by ${settings.displayColumn}`);
  }
  if (settings.hidden) {
    parts.push('Hidden');
  }
  const columns = settings.columns?.length ?? 0;
  if (columns > 0) {
    parts.push(columns === 1 ? "1 column's settings" : `${columns} columns' settings`);
  }
  return parts.length > 0 ? parts : ['Nothing set'];
}

/** What an item is about, to name it: a relation's text, a virtual entity's name, an entity, a navigation. */
export function itemName(kind: OverlayItemKind, item: OverlayItem): string {
  switch (kind) {
    case 'relation':
      return relationText(item as Relation);
    case 'navigation': {
      const override = item as NavigationOverride;
      return `${override.entity}.${override.navigation}`;
    }
    case 'virtualEntity':
      return (item as VirtualEntity).name;
    case 'entitySettings':
      return (item as EntitySettings).entity;
  }
}

/** An item, named for a sentence's start: "The relation …", "The settings of shop.orders"; by its id when it isn't known. */
export function itemTitle(
  kind: OverlayItemKind,
  item: OverlayItem | undefined,
  id: number,
): string {
  if (!item) {
    return `The ${kindNouns[kind]} ${id}`;
  }
  switch (kind) {
    case 'relation':
      return `The relation ${itemName(kind, item)}`;
    case 'navigation':
      return `The override of ${itemName(kind, item)}`;
    case 'virtualEntity':
      return `The virtual entity ${itemName(kind, item)}`;
    case 'entitySettings':
      return `The settings of ${itemName(kind, item)}`;
  }
}

/** The items of a kind in the overlay. */
export function itemsOf(overlay: Overlay, kind: OverlayItemKind): readonly OverlayItem[] {
  switch (kind) {
    case 'relation':
      return overlay.relations;
    case 'navigation':
      return overlay.navigations;
    case 'virtualEntity':
      return overlay.virtualEntities;
    case 'entitySettings':
      return overlay.entitySettings;
  }
}

/** The worst of issues' severities, or null when there are none. */
export function worstOf(issues: readonly OverlayIssue[]): Severity | null {
  if (issues.some((issue) => issue.severity === 'error')) {
    return 'error';
  }
  if (issues.some((issue) => issue.severity === 'warning')) {
    return 'warning';
  }
  return issues.length > 0 ? 'info' : null;
}

/** Each severity's icon. */
export const severityIcons: Readonly<Record<Severity, string>> = {
  error: 'error',
  warning: 'warning',
  info: 'info',
};

/** Each severity, as screen readers say it before the message. */
export const severityWords: Readonly<Record<Severity, string>> = {
  error: 'Error',
  warning: 'Warning',
  info: 'Note',
};

/** Names given as a list's items, trimmed, blanks left out; null when there are none (the item doesn't set them). */
export function namesOf(names: readonly string[]): string[] | null {
  const given = names.map((name) => name.trim()).filter((name) => name.length > 0);
  return given.length > 0 ? given : null;
}

/** Text given, trimmed; null when blank. */
export function trimmed(text: string): string | null {
  const trimmed = text.trim();
  return trimmed.length > 0 ? trimmed : null;
}
