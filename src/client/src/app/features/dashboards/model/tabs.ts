import { type Cells, type Placement, rowsOf } from '../layout/grid-layout';
import type { Definition, Tab, Widget } from './definition';

/**
 * A dashboard's tabs: each a grid of its own, holding the widgets on it. Cells stay keyed by widget id, each in its
 * tab's grid, so a dashboard is laid out tab by tab and no edit of one tab's grid moves another's. A dashboard
 * without tabs is one grid, whose tab is null here.
 */

/** The most tabs a dashboard may have (the server's `Dashboards:MaxTabs`). */
export const maxTabs = 20;

/** A dashboard's tabs, in order; none for a dashboard of one grid. */
export function tabsOf(definition: Definition): readonly Tab[] {
  return definition.tabs ?? [];
}

/** The grids a dashboard is laid out in: its tabs' ids, in order; null alone without tabs. */
export function gridsOf(definition: Definition): (string | null)[] {
  const tabs = tabsOf(definition);
  return tabs.length > 0 ? tabs.map((tab) => tab.id) : [null];
}

/** The tab a widget is on: null without tabs; with them, the first for one whose tab isn't there (or isn't said). */
export function tabOf(definition: Definition, widget: string | Widget): string | null {
  const tabs = tabsOf(definition);
  if (tabs.length === 0) {
    return null;
  }
  const tab = (
    typeof widget === 'string' ? definition.widgets.find((w) => w.id === widget) : widget
  )?.tab;
  return tabs.some((t) => t.id === tab) ? tab! : tabs[0].id;
}

/** The widgets on a tab, in the definition's order: every widget, without tabs (for null). */
export function widgetsOn(definition: Definition, tab: string | null): Widget[] {
  return definition.widgets.filter((widget) => tabOf(definition, widget) === tab);
}

/** Of the cells given, those of the widgets on a tab (cells of widgets that aren't there count as the first tab's). */
export function cellsOn(
  definition: Definition,
  cells: Cells,
  tab: string | null,
): Record<string, Placement> {
  return Object.fromEntries(Object.entries(cells).filter(([id]) => tabOf(definition, id) === tab));
}

/** Cells changed tab by tab: each tab's given to `change` alone, then put back together. */
export function perTab(
  definition: Definition,
  cells: Cells,
  change: (cells: Cells, tab: string | null) => Cells,
): Record<string, Placement> {
  const groups = new Map<string | null, Record<string, Placement>>();
  for (const [id, cell] of Object.entries(cells)) {
    const tab = tabOf(definition, id);
    const group = groups.get(tab) ?? {};
    group[id] = cell;
    groups.set(tab, group);
  }
  return Object.assign({}, ...[...groups].map(([tab, own]) => change(own, tab))) as Record<
    string,
    Placement
  >;
}

/** Grids one below the other, in order: each one's cells moved down by the rows of those above it. */
export function stack(grids: readonly Cells[]): Record<string, Placement> {
  const stacked: Record<string, Placement> = {};
  let rows = 0;
  for (const cells of grids) {
    for (const [id, cell] of Object.entries(cells)) {
      stacked[id] = { ...cell, y: cell.y + rows };
    }
    rows += rowsOf(cells);
  }
  return stacked;
}

/** A widget on a tab; without one (null), with no tab said at all, as a dashboard without tabs writes its widgets. */
export function onTab(widget: Widget, tab: string | null): Widget {
  if (tab !== null) {
    return widget.tab === tab ? widget : { ...widget, tab };
  }
  if (!('tab' in widget)) {
    return widget;
  }
  const rest = { ...widget };
  delete rest.tab;
  return rest;
}

/** A definition as one grid would have it: without tabs, nor widgets on them (what queries don't depend on). */
export function untabbed(definition: Definition): Definition {
  if (!('tabs' in definition) && !definition.widgets.some((widget) => 'tab' in widget)) {
    return definition;
  }
  const rest: Definition = {
    ...definition,
    widgets: definition.widgets.map((widget) => onTab(widget, null)),
  };
  delete rest.tabs;
  return rest;
}
