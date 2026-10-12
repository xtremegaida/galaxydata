import {
  type Cells,
  type MinSizes,
  type Placement,
  compact,
  derive,
  effectiveLayout,
  moveTo,
  place,
  resizeTo,
} from '../layout/grid-layout';
import {
  type Breakpoint,
  type Definition,
  type Filter,
  type Link,
  type Source,
  type Widget,
  type WidgetConfig,
  configOf,
  isData,
  newId,
} from './definition';
import { cellsOn, gridsOf, onTab, perTab, stack, tabOf, tabsOf, widgetsOn } from './tabs';

/**
 * Edits of a dashboard's definition, each a pure function giving a new one (the editor applies them, and undoes
 * them by keeping the ones before). Ids are kept unique; what refers to a widget, source or filter removed goes
 * with it. With tabs, each tab is a grid of its own: an edit of a widget's cells lays out its tab's alone.
 */

type RefreshPolicy = Definition['refresh'];

/** The breakpoint designed: the widest, whose layout the others are derived from. */
export function designedOf(definition: Definition): Breakpoint {
  return definition.layout.breakpoints.at(-1)!;
}

/**
 * A widget added: an id from its kind, on a tab (the first, when the one given isn't there; none without tabs),
 * placed where it first fits in its tab's designed layout (its size, given for 12 columns, scaled to the designed
 * ones), and in each laid out narrower one.
 */
export function addWidget(
  definition: Definition,
  config: WidgetConfig,
  size: { w: number; h: number },
  title: string | null = null,
  tab: string | null = null,
): { definition: Definition; id: string } {
  const id = newId(
    config.kind,
    definition.widgets.map((w) => w.id),
  );
  const tabs = tabsOf(definition);
  const on = tabs.length === 0 ? null : tabs.some((t) => t.id === tab) ? tab : tabs[0].id;
  const designed = designedOf(definition);
  const scaled = { w: Math.max(1, Math.round((size.w * designed.columns) / 12)), h: size.h };
  const cell = place(cellsOn(definition, definition.layout.items, on), scaled, designed.columns);
  const overrides = Object.fromEntries(
    Object.entries(definition.layout.overrides).map(([bp, custom]) => [
      bp,
      {
        ...custom,
        items: {
          ...custom.items,
          [id]: place(
            cellsOn(definition, custom.items, on),
            {
              w: Math.min(
                custom.columns,
                Math.max(1, Math.round((scaled.w * custom.columns) / designed.columns)),
              ),
              h: size.h,
            },
            custom.columns,
          ),
        },
      },
    ]),
  );
  return {
    id,
    definition: {
      ...definition,
      widgets: [...definition.widgets, onTab({ id, title, config }, on)],
      layout: {
        ...definition.layout,
        items: { ...definition.layout.items, [id]: cell },
        overrides,
      },
    },
  };
}

/** A copy of a widget, on its tab, placed after the first gap that fits it. */
export function duplicateWidget(
  definition: Definition,
  id: string,
): { definition: Definition; id: string } {
  const widget = definition.widgets.find((w) => w.id === id);
  const cell = definition.layout.items[id];
  if (!widget || !cell) {
    return { definition, id };
  }
  const designed = designedOf(definition);
  return addWidget(
    definition,
    structuredClone(widget.config) as WidgetConfig,
    { w: Math.round((cell.w * 12) / designed.columns), h: cell.h },
    widget.title ? `${widget.title} (copy)` : null,
    tabOf(definition, widget),
  );
}

/** A widget removed, with its cells, the filters' exceptions and the listening that named it. */
export function removeWidget(definition: Definition, id: string): Definition {
  const without = <T>(record: Readonly<Record<string, T>>) =>
    Object.fromEntries(Object.entries(record).filter(([key]) => key !== id));
  return {
    ...definition,
    widgets: definition.widgets
      .filter((w) => w.id !== id)
      .map((w) => {
        const config = configOf(w);
        return isData(config) && config.listens.widgets.includes(id)
          ? {
              ...w,
              config: {
                ...config,
                listens: {
                  ...config.listens,
                  widgets: config.listens.widgets.filter((x) => x !== id),
                },
              },
            }
          : w;
      }),
    filters: definition.filters.map((f) =>
      f.except.includes(id) ? { ...f, except: f.except.filter((x) => x !== id) } : f,
    ),
    layout: {
      ...definition.layout,
      items: without(definition.layout.items),
      overrides: Object.fromEntries(
        Object.entries(definition.layout.overrides).map(([bp, custom]) => [
          bp,
          {
            ...custom,
            items: without(custom.items),
            hidden: custom.hidden.filter((x) => x !== id),
          },
        ]),
      ),
    },
  };
}

/** A widget changed: its title, or its config. */
export function updateWidget(
  definition: Definition,
  id: string,
  change: Partial<Omit<Widget, 'id'>>,
): Definition {
  return {
    ...definition,
    widgets: definition.widgets.map((w) => (w.id === id ? { ...w, ...change } : w)),
  };
}

/** The cells laid out at a breakpoint, as edits change them: the designed ones, or a narrower one's by hand. */
export function cellsAt(definition: Definition, breakpoint: string): Cells | null {
  const designed = designedOf(definition);
  if (breakpoint === designed.id) {
    return definition.layout.items;
  }
  return definition.layout.overrides[breakpoint]?.items ?? null;
}

/**
 * The cells of the widgets given replaced at a breakpoint: the designed ones, or those of its override (which it
 * must have); the others' (another tab's) as they were.
 */
export function withCells(definition: Definition, breakpoint: string, cells: Cells): Definition {
  const designed = designedOf(definition);
  if (breakpoint === designed.id) {
    return {
      ...definition,
      layout: { ...definition.layout, items: { ...definition.layout.items, ...cells } },
    };
  }
  const custom = definition.layout.overrides[breakpoint];
  if (!custom) {
    return definition;
  }
  return {
    ...definition,
    layout: {
      ...definition.layout,
      overrides: {
        ...definition.layout.overrides,
        [breakpoint]: { ...custom, items: { ...custom.items, ...cells } },
      },
    },
  };
}

function columnsAt(definition: Definition, breakpoint: string): number {
  return (
    definition.layout.breakpoints.find((b) => b.id === breakpoint)?.columns ??
    designedOf(definition).columns
  );
}

/** The cells laid out at a breakpoint of a widget's tab (all of them, without tabs). */
function tabCellsAt(definition: Definition, breakpoint: string, id: string): Cells | null {
  const cells = cellsAt(definition, breakpoint);
  return cells && cellsOn(definition, cells, tabOf(definition, id));
}

/** A widget moved at a breakpoint laid out (the designed one, or one customized); others on its tab make room. */
export function moveWidget(
  definition: Definition,
  breakpoint: string,
  id: string,
  to: { x: number; y: number },
): Definition {
  const cells = tabCellsAt(definition, breakpoint, id);
  return cells
    ? withCells(definition, breakpoint, moveTo(cells, id, to, columnsAt(definition, breakpoint)))
    : definition;
}

/** A widget resized at a breakpoint laid out. */
export function resizeWidget(
  definition: Definition,
  breakpoint: string,
  id: string,
  size: { w: number; h: number },
  min?: { w: number; h: number },
): Definition {
  const cells = tabCellsAt(definition, breakpoint, id);
  return cells
    ? withCells(
        definition,
        breakpoint,
        resizeTo(cells, id, size, columnsAt(definition, breakpoint), min),
      )
    : definition;
}

/** A narrower breakpoint laid out by hand from now: its derived layout (each tab's), kept as its own. */
export function customize(
  definition: Definition,
  breakpoint: string,
  mins: MinSizes = {},
): Definition {
  const target = definition.layout.breakpoints.find((b) => b.id === breakpoint);
  if (
    !target ||
    target.id === designedOf(definition).id ||
    definition.layout.overrides[breakpoint]
  ) {
    return definition;
  }
  const cells = Object.assign(
    {},
    ...gridsOf(definition).map(
      (tab) =>
        effectiveLayout(
          definition.layout,
          breakpoint,
          widgetsOn(definition, tab).map((w) => w.id),
          mins,
        ).cells,
    ),
  ) as Record<string, Placement>;
  return {
    ...definition,
    layout: {
      ...definition.layout,
      overrides: {
        ...definition.layout.overrides,
        [breakpoint]: { columns: target.columns, items: cells, hidden: [] },
      },
    },
  };
}

/** A narrower breakpoint derived from the designed layout again. */
export function resetBreakpoint(definition: Definition, breakpoint: string): Definition {
  if (!(breakpoint in definition.layout.overrides)) {
    return definition;
  }
  const overrides = { ...definition.layout.overrides };
  delete overrides[breakpoint];
  return { ...definition, layout: { ...definition.layout, overrides } };
}

/** A widget hidden (or shown again) at a breakpoint laid out by hand. */
export function hideAt(
  definition: Definition,
  breakpoint: string,
  id: string,
  hidden: boolean,
): Definition {
  const custom = definition.layout.overrides[breakpoint];
  if (!custom || custom.hidden.includes(id) === hidden) {
    return definition;
  }
  const items = { ...custom.items };
  if (hidden) {
    delete items[id];
  }
  return {
    ...definition,
    layout: {
      ...definition.layout,
      overrides: {
        ...definition.layout.overrides,
        [breakpoint]: {
          ...custom,
          items,
          hidden: hidden ? [...custom.hidden, id] : custom.hidden.filter((x) => x !== id),
        },
      },
    },
  };
}

/** The layout's breakpoints replaced: overrides of those gone go, those whose columns changed are derived anew. */
export function setBreakpoints(
  definition: Definition,
  breakpoints: readonly Breakpoint[],
): Definition {
  const ids = new Set(breakpoints.map((b) => b.id));
  const designedBefore = designedOf(definition);
  const designedAfter = breakpoints.at(-1)!;
  // The designed layout keeps its cells, scaled when its columns change (and compact, as stored layouts are), tab
  // by tab.
  const items: Cells =
    designedAfter.columns === designedBefore.columns
      ? definition.layout.items
      : perTab(definition, definition.layout.items, (cells) =>
          compact(derive(cells, designedBefore.columns, designedAfter.columns)),
        );
  return {
    ...definition,
    layout: {
      ...definition.layout,
      breakpoints: [...breakpoints],
      items,
      overrides: Object.fromEntries(
        Object.entries(definition.layout.overrides).filter(
          ([bp]) => ids.has(bp) && bp !== designedAfter.id,
        ),
      ),
    },
  };
}

export function setLayoutSizes(
  definition: Definition,
  sizes: { rowHeight?: number; gap?: number },
): Definition {
  return { ...definition, layout: { ...definition.layout, ...sizes } };
}

/** A source added for an entity: an id from its name's last part. */
export function addSource(
  definition: Definition,
  entity: string,
  label: string,
): { definition: Definition; id: string } {
  const id = newId(
    entity.split('.').at(-1) ?? 'source',
    definition.sources.map((s) => s.id),
  );
  return {
    id,
    definition: { ...definition, sources: [...definition.sources, { id, entity, label }] },
  };
}

export function updateSource(
  definition: Definition,
  id: string,
  change: Partial<Omit<Source, 'id'>>,
): Definition {
  return {
    ...definition,
    sources: definition.sources.map((s) => (s.id === id ? { ...s, ...change } : s)),
  };
}

/** Whether a source is used: by a widget or a filter (it can't be removed till they don't). */
export function sourceUsedBy(definition: Definition, id: string): string[] {
  const widgets = definition.widgets
    .filter((w) => {
      const config = configOf(w);
      return isData(config) && config.source === id;
    })
    .map((w) => w.title ?? w.id);
  const filters = definition.filters.filter((f) => f.field.source === id).map((f) => f.label);
  return [...widgets, ...filters];
}

/** A source removed, with its links. */
export function removeSource(definition: Definition, id: string): Definition {
  return {
    ...definition,
    sources: definition.sources.filter((s) => s.id !== id),
    links: definition.links.filter((l) => l.from !== id && l.to !== id),
  };
}

/** Whether linking two sources would close a cycle (the links are a forest: two sources are related one way at most). */
export function wouldCycle(definition: Definition, from: string, to: string): boolean {
  const roots = new Map<string, string>();
  const root = (id: string): string => {
    let at = id;
    while (roots.has(at) && roots.get(at) !== at) {
      at = roots.get(at)!;
    }
    return at;
  };
  for (const link of definition.links) {
    roots.set(root(link.from), root(link.to));
  }
  return from === to || root(from) === root(to);
}

/** A link between two sources: by a path of navigations from `from`'s entity to `to`'s. */
export function addLink(definition: Definition, link: Link): Definition {
  return wouldCycle(definition, link.from, link.to)
    ? definition
    : { ...definition, links: [...definition.links, link] };
}

export function removeLink(definition: Definition, from: string, to: string): Definition {
  return {
    ...definition,
    links: definition.links.filter((l) => !(l.from === from && l.to === to)),
  };
}

/** A filter added, its id from its label. */
export function addFilter(
  definition: Definition,
  filter: Omit<Filter, 'id'>,
): { definition: Definition; id: string } {
  const id = newId(
    filter.label || filter.field.column || 'filter',
    definition.filters.map((f) => f.id),
  );
  return { id, definition: { ...definition, filters: [...definition.filters, { ...filter, id }] } };
}

export function updateFilter(
  definition: Definition,
  id: string,
  change: Partial<Omit<Filter, 'id'>>,
): Definition {
  return {
    ...definition,
    filters: definition.filters.map((f) => {
      if (f.id !== id) {
        return f;
      }
      const next = { ...f, ...change };
      // A hidden filter is never the viewers' to change.
      return next.visible ? next : { ...next, editable: false };
    }),
  };
}

/**
 * A filter's id from its label, while it is new (not saved: once it is, addresses name it by its id). Its id is
 * the label's, unless another filter has it.
 */
export function renameNewFilter(definition: Definition, id: string, label: string): Definition {
  const others = definition.filters.filter((f) => f.id !== id).map((f) => f.id);
  const next = label.trim() ? newId(label, others) : id;
  return next === id
    ? definition
    : {
        ...definition,
        filters: definition.filters.map((f) => (f.id === id ? { ...f, id: next } : f)),
      };
}

export function removeFilter(definition: Definition, id: string): Definition {
  return { ...definition, filters: definition.filters.filter((f) => f.id !== id) };
}

/** Whether a filter applies to a widget, set either way. */
export function setFilterApplies(
  definition: Definition,
  filter: string,
  widget: string,
  applies: boolean,
): Definition {
  return {
    ...definition,
    filters: definition.filters.map((f) =>
      f.id !== filter || f.except.includes(widget) !== applies
        ? f
        : { ...f, except: applies ? f.except.filter((x) => x !== widget) : [...f.except, widget] },
    ),
  };
}

export function setRefresh(definition: Definition, refresh: RefreshPolicy): Definition {
  return { ...definition, refresh };
}

/** The tabs replaced, every widget on one of them (or none, without tabs: the key left out, as for one grid). */
function withTabs(definition: Definition, tabs: Definition['tabs']): Definition {
  const next: Definition = { ...definition };
  if (tabs && tabs.length > 0) {
    next.tabs = tabs;
  } else {
    delete next.tabs;
  }
  return next;
}

/** The cells of the designed layout and of each laid out by hand, changed alike (with their columns). */
function everyLayout(
  definition: Definition,
  change: (cells: Cells, columns: number) => Cells,
): Definition {
  return {
    ...definition,
    layout: {
      ...definition.layout,
      items: change(definition.layout.items, designedOf(definition).columns),
      overrides: Object.fromEntries(
        Object.entries(definition.layout.overrides).map(([bp, custom]) => [
          bp,
          { ...custom, items: change(custom.items, custom.columns) },
        ]),
      ),
    },
  };
}

/** Tabs from now: one, of the label given, holding every widget where it is. */
export function useTabs(
  definition: Definition,
  label: string,
): { definition: Definition; id: string } {
  const tabs = tabsOf(definition);
  if (tabs.length > 0) {
    return { definition, id: tabs[0].id };
  }
  const id = newId(label || 'tab', []);
  return {
    id,
    definition: {
      ...withTabs(definition, [{ id, label }]),
      widgets: definition.widgets.map((widget) => onTab(widget, id)),
    },
  };
}

/** A tab added, last: its id from its label, kept when it is renamed. Without tabs, the first, holding every widget. */
export function addTab(
  definition: Definition,
  label: string,
): { definition: Definition; id: string } {
  const tabs = tabsOf(definition);
  if (tabs.length === 0) {
    return useTabs(definition, label);
  }
  const id = newId(
    label || 'tab',
    tabs.map((tab) => tab.id),
  );
  return { id, definition: withTabs(definition, [...tabs, { id, label }]) };
}

export function renameTab(definition: Definition, id: string, label: string): Definition {
  return withTabs(
    definition,
    tabsOf(definition).map((tab) => (tab.id === id ? { ...tab, label } : tab)),
  );
}

/** A tab moved among the others: `by` places earlier (negative) or later. */
export function moveTab(definition: Definition, id: string, by: number): Definition {
  const tabs = [...tabsOf(definition)];
  const at = tabs.findIndex((tab) => tab.id === id);
  const to = Math.max(0, Math.min(tabs.length - 1, at + by));
  if (at < 0 || to === at) {
    return definition;
  }
  const [moved] = tabs.splice(at, 1);
  tabs.splice(to, 0, moved);
  return withTabs(definition, tabs);
}

/** A widget moved to another tab: placed where it first fits in that tab's grid, at every breakpoint laid out. */
export function moveToTab(definition: Definition, id: string, tab: string): Definition {
  const from = tabOf(definition, id);
  if (from === null || from === tab || !tabsOf(definition).some((t) => t.id === tab)) {
    return definition;
  }
  const moved: Definition = {
    ...definition,
    widgets: definition.widgets.map((widget) => (widget.id === id ? onTab(widget, tab) : widget)),
  };
  return everyLayout(moved, (cells, columns) => {
    const cell = cells[id];
    if (!cell) {
      return cells;
    }
    const rest = Object.fromEntries(Object.entries(cells).filter(([other]) => other !== id));
    return { ...rest, [id]: place(cellsOn(moved, rest, tab), cell, columns) };
  });
}

/**
 * A tab removed: its widgets with it, or moved to the tab before it (else after), below that tab's widgets as they
 * were arranged. The only tab removed, the dashboard has no tabs (its widgets stay).
 */
export function removeTab(
  definition: Definition,
  id: string,
  widgets: 'remove' | 'move',
): Definition {
  const tabs = tabsOf(definition);
  const at = tabs.findIndex((tab) => tab.id === id);
  if (at < 0) {
    return definition;
  }
  if (tabs.length === 1) {
    return dropTabs(definition);
  }
  const others = tabs.filter((tab) => tab.id !== id);
  const on = widgetsOn(definition, id).map((widget) => widget.id);
  if (widgets === 'remove') {
    return withTabs(
      on.reduce((d, widget) => removeWidget(d, widget), definition),
      others,
    );
  }
  const into = (tabs[at - 1] ?? tabs[at + 1]).id;
  const stacked = everyLayout(definition, (cells) => ({
    ...cells,
    ...stack([cellsOn(definition, cells, into), cellsOn(definition, cells, id)]),
  }));
  return withTabs(
    {
      ...stacked,
      widgets: definition.widgets.map((widget) =>
        on.includes(widget.id) ? onTab(widget, into) : widget,
      ),
    },
    others,
  );
}

/** No tabs from now: their grids one below the other, in their order, as one. */
export function dropTabs(definition: Definition): Definition {
  const tabs = tabsOf(definition);
  if (tabs.length === 0) {
    return definition;
  }
  const stacked = everyLayout(definition, (cells) =>
    stack(tabs.map((tab) => cellsOn(definition, cells, tab.id))),
  );
  return withTabs(
    { ...stacked, widgets: definition.widgets.map((widget) => onTab(widget, null)) },
    null,
  );
}

/** A placement's text, as screen readers hear a widget's place. */
export function placementText(cell: Placement): string {
  return `column ${cell.x + 1}, row ${cell.y + 1}, ${cell.w} by ${cell.h}`;
}
