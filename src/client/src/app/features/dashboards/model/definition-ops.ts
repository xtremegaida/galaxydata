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

/**
 * Edits of a dashboard's definition, each a pure function giving a new one (the editor applies them, and undoes
 * them by keeping the ones before). Ids are kept unique; what refers to a widget, source or filter removed goes
 * with it.
 */

type RefreshPolicy = Definition['refresh'];

/** The breakpoint designed: the widest, whose layout the others are derived from. */
export function designedOf(definition: Definition): Breakpoint {
  return definition.layout.breakpoints.at(-1)!;
}

/**
 * A widget added: an id from its kind, placed where it first fits in the designed layout (its size, given for 12
 * columns, scaled to the designed ones), and in each laid out narrower one.
 */
export function addWidget(
  definition: Definition,
  config: WidgetConfig,
  size: { w: number; h: number },
  title: string | null = null,
): { definition: Definition; id: string } {
  const id = newId(
    config.kind,
    definition.widgets.map((w) => w.id),
  );
  const designed = designedOf(definition);
  const scaled = { w: Math.max(1, Math.round((size.w * designed.columns) / 12)), h: size.h };
  const cell = place(definition.layout.items, scaled, designed.columns);
  const overrides = Object.fromEntries(
    Object.entries(definition.layout.overrides).map(([bp, custom]) => [
      bp,
      {
        ...custom,
        items: {
          ...custom.items,
          [id]: place(
            custom.items,
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
      widgets: [...definition.widgets, { id, title, config }],
      layout: {
        ...definition.layout,
        items: { ...definition.layout.items, [id]: cell },
        overrides,
      },
    },
  };
}

/** A copy of a widget, placed after the first gap that fits it. */
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

/** The cells at a breakpoint replaced: the designed ones, or those of its override (which it must have). */
export function withCells(definition: Definition, breakpoint: string, cells: Cells): Definition {
  const designed = designedOf(definition);
  if (breakpoint === designed.id) {
    return { ...definition, layout: { ...definition.layout, items: cells } };
  }
  const custom = definition.layout.overrides[breakpoint];
  if (!custom) {
    return definition;
  }
  return {
    ...definition,
    layout: {
      ...definition.layout,
      overrides: { ...definition.layout.overrides, [breakpoint]: { ...custom, items: cells } },
    },
  };
}

function columnsAt(definition: Definition, breakpoint: string): number {
  return (
    definition.layout.breakpoints.find((b) => b.id === breakpoint)?.columns ??
    designedOf(definition).columns
  );
}

/** A widget moved at a breakpoint laid out (the designed one, or one customized); others there make room. */
export function moveWidget(
  definition: Definition,
  breakpoint: string,
  id: string,
  to: { x: number; y: number },
): Definition {
  const cells = cellsAt(definition, breakpoint);
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
  const cells = cellsAt(definition, breakpoint);
  return cells
    ? withCells(
        definition,
        breakpoint,
        resizeTo(cells, id, size, columnsAt(definition, breakpoint), min),
      )
    : definition;
}

/** A narrower breakpoint laid out by hand from now: its derived layout, kept as its own. */
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
  const { cells } = effectiveLayout(
    definition.layout,
    breakpoint,
    definition.widgets.map((w) => w.id),
    mins,
  );
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
  // The designed layout keeps its cells, scaled when its columns change (and compact, as stored layouts are).
  const items: Cells =
    designedAfter.columns === designedBefore.columns
      ? definition.layout.items
      : compact(derive(definition.layout.items, designedBefore.columns, designedAfter.columns));
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

/** A placement's text, as screen readers hear a widget's place. */
export function placementText(cell: Placement): string {
  return `column ${cell.x + 1}, row ${cell.y + 1}, ${cell.w} by ${cell.h}`;
}
