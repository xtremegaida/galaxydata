import fc from 'fast-check';
import { salesDefinition, tabbedDefinition } from '../../../../testing/dashboards';
import { type Cells, collides } from '../layout/grid-layout';
import { History } from '../editor/history';
import { rowCount, tableDefaults, textDefaults } from './widget-defaults';
import type { Definition } from './definition';
import {
  addFilter,
  addLink,
  addSource,
  addTab,
  addWidget,
  customize,
  designedOf,
  dropTabs,
  duplicateWidget,
  hideAt,
  moveTab,
  moveToTab,
  moveWidget,
  removeSource,
  removeTab,
  removeWidget,
  renameNewFilter,
  renameTab,
  resetBreakpoint,
  resizeWidget,
  setBreakpoints,
  setFilterApplies,
  sourceUsedBy,
  updateFilter,
  useTabs,
  wouldCycle,
} from './definition-ops';
import { cellsOn, tabOf } from './tabs';
import { changesBetween } from './changes';
import { sliceOf } from './slice';

/** The sales dashboard, with a filter on each source, one of them not applying to the pie. */
function withFilters(): Definition {
  const base = salesDefinition();
  return {
    ...base,
    filters: [
      {
        id: 'status',
        label: 'Status',
        field: { source: 'orders', path: [], column: 'status' },
        kind: 'values',
        value: null,
        visible: true,
        editable: true,
        multiple: true,
        except: ['by-city'],
      },
      {
        id: 'city',
        label: 'City',
        field: { source: 'customers', path: [], column: 'city' },
        kind: 'values',
        value: null,
        visible: true,
        editable: true,
        multiple: true,
        except: [],
      },
    ],
  };
}

describe("a dashboard's edits", () => {
  it('adds a widget where it first fits, at every breakpoint laid out, its id from its kind', () => {
    const customized = customize(salesDefinition(), 'medium');
    const { definition, id } = addWidget(customized, textDefaults(), { w: 6, h: 2 });
    expect(id).toBe('text');
    expect(definition.layout.items['text']).toEqual({ x: 0, y: 7, w: 6, h: 2 });
    expect(definition.layout.overrides['medium'].items['text']).toEqual({ x: 0, y: 7, w: 3, h: 2 });
    expect(addWidget(definition, textDefaults(), { w: 6, h: 2 }).id).toBe('text-2');
  });

  it('copies a widget with its title said so', () => {
    const { definition, id } = duplicateWidget(salesDefinition(), 'by-status');
    expect(definition.widgets.find((w) => w.id === id)?.title).toBe('Orders by status (copy)');
    expect(definition.layout.items[id]).toEqual({ x: 0, y: 7, w: 6, h: 6 });
  });

  it('removes a widget with all that names it: cells, exceptions, listening', () => {
    let definition = customize(withFilters(), 'medium');
    definition = hideAt(definition, 'medium', 'by-city', true);
    const bar = definition.widgets.find((w) => w.id === 'by-status')!;
    definition = {
      ...definition,
      widgets: definition.widgets.map((w) =>
        w.id === 'by-status'
          ? { ...bar, config: { ...bar.config, listens: { mode: 'chosen', widgets: ['by-city'] } } }
          : w,
      ),
    } as Definition;
    const removed = removeWidget(definition, 'by-city');
    expect(removed.widgets.map((w) => w.id)).toEqual(['title', 'by-status']);
    expect(Object.keys(removed.layout.items)).toEqual(['title', 'by-status']);
    expect(removed.layout.overrides['medium'].hidden).toEqual([]);
    expect(removed.filters[0].except).toEqual([]);
    expect((removed.widgets[1].config as { listens: unknown }).listens).toEqual({
      mode: 'chosen',
      widgets: [],
    });
  });

  it('lays a narrower breakpoint out by hand, moves widgets there only, and derives it again', () => {
    const designed = salesDefinition();
    // Not customized: moving there changes nothing.
    expect(moveWidget(designed, 'medium', 'by-city', { x: 0, y: 0 })).toBe(designed);
    const customized = customize(designed, 'medium');
    expect(customized.layout.overrides['medium']).toEqual({
      columns: 6,
      items: {
        title: { x: 0, y: 0, w: 6, h: 1 },
        'by-status': { x: 0, y: 1, w: 3, h: 6 },
        'by-city': { x: 3, y: 1, w: 3, h: 6 },
      },
      hidden: [],
    });
    const moved = moveWidget(customized, 'medium', 'by-city', { x: 0, y: 0 });
    expect(moved.layout.overrides['medium'].items['by-city']).toEqual({ x: 0, y: 0, w: 3, h: 6 });
    expect(moved.layout.items).toEqual(designed.layout.items);
    expect(resetBreakpoint(moved, 'medium').layout.overrides).toEqual({});
  });

  it('keeps the designed cells when breakpoints change, scaling them to new columns', () => {
    const definition = salesDefinition();
    const six = setBreakpoints(definition, [
      { id: 'narrow', label: 'Narrow', minWidth: 0, columns: 1 },
      { id: 'wide', label: 'Wide', minWidth: 900, columns: 6 },
    ]);
    expect(six.layout.items).toEqual({
      title: { x: 0, y: 0, w: 6, h: 1 },
      'by-status': { x: 0, y: 1, w: 3, h: 6 },
      'by-city': { x: 3, y: 1, w: 3, h: 6 },
    });
    // An override of a breakpoint gone goes with it.
    const customized = customize(definition, 'medium');
    expect(setBreakpoints(customized, six.layout.breakpoints).layout.overrides).toEqual({});
  });

  it('adds sources and links, and refuses links that would close a cycle', () => {
    let { definition } = addSource(salesDefinition(), 'shop.order_lines', 'Lines');
    expect(definition.sources.at(-1)).toEqual({
      id: 'order-lines',
      entity: 'shop.order_lines',
      label: 'Lines',
    });
    expect(wouldCycle(definition, 'customers', 'orders')).toBe(true);
    definition = addLink(definition, { from: 'order-lines', to: 'orders', path: ['order'] });
    expect(definition.links).toHaveLength(2);
    expect(wouldCycle(definition, 'order-lines', 'customers')).toBe(true);
    expect(
      addLink(definition, { from: 'order-lines', to: 'customers', path: ['order', 'customer'] }),
    ).toBe(definition);
    expect(sourceUsedBy(definition, 'orders')).toEqual(['Orders by status']);
    expect(removeSource(definition, 'order-lines').links).toEqual(salesDefinition().links);
  });

  it('adds filters with ids from their labels; hidden ones are never editable', () => {
    const { definition, id } = addFilter(salesDefinition(), {
      label: 'Order status',
      field: { source: 'orders', path: [], column: 'status' },
      kind: 'values',
      value: null,
      visible: true,
      editable: true,
      multiple: true,
      except: [],
    });
    expect(id).toBe('order-status');
    expect(updateFilter(definition, id, { visible: false }).filters[0]).toMatchObject({
      visible: false,
      editable: false,
    });
    // While new, its id follows its label (another's id kept apart), so addresses read well.
    const renamed = renameNewFilter(definition, id, 'Status');
    expect(renamed.filters.map((f) => f.id)).toEqual(['status']);
    expect(
      renameNewFilter(
        addFilter(renamed, { ...renamed.filters[0], label: 'x' }).definition,
        'x',
        'Status',
      ).filters.map((f) => f.id),
    ).toEqual(['status', 'status-2']);
    expect(renameNewFilter(renamed, 'status', '  ')).toBe(renamed);
    const excepted = setFilterApplies(definition, id, 'by-status', false);
    expect(excepted.filters[0].except).toEqual(['by-status']);
    expect(setFilterApplies(excepted, id, 'by-status', true).filters[0].except).toEqual([]);
  });
});

describe("a widget's slice", () => {
  it('holds what its rows depend on: its sources, the filters reaching it, what it listens to', () => {
    const slice = sliceOf(withFilters(), 'by-city');
    expect(slice.widgets.map((w) => w.id)).toEqual(['by-city', 'by-status']);
    expect(slice.filters.map((f) => f.id)).toEqual(['city']);
    expect(slice.sources.map((s) => s.id)).toEqual(['orders', 'customers']);
    expect(slice.layout.items).toEqual({
      'by-city': { x: 0, y: 0, w: 1, h: 1 },
      'by-status': { x: 0, y: 1, w: 1, h: 1 },
    });
    // Of the widget listened to, what it chooses by; not its title or measures.
    expect(slice.widgets[1]).toMatchObject({ title: null, config: { measures: [rowCount()] } });
  });

  it("doesn't change with what its rows don't depend on", () => {
    const definition = withFilters();
    const before = sliceOf(definition, 'by-city');
    const moved = customize(moveWidget(definition, 'wide', 'by-city', { x: 0, y: 0 }), 'medium');
    const renamed = {
      ...moved,
      widgets: moved.widgets.map((w) => (w.id === 'by-status' ? { ...w, title: 'Renamed' } : w)),
    };
    expect(sliceOf(renamed, 'by-city')).toEqual(before);
    const text = { ...renamed, widgets: [...renamed.widgets] };
    expect(
      sliceOf(addWidget(text, tableDefaults('orders'), { w: 6, h: 6 }).definition, 'by-city')
        .widgets,
    ).toHaveLength(3);
  });

  it("doesn't change with its colours: the palettes, and what bars are coloured by", () => {
    const definition = withFilters();
    const before = sliceOf(definition, 'by-status');
    const colored: Definition = {
      ...definition,
      palette: 3,
      widgets: definition.widgets.map((w) =>
        w.config.kind === 'bar'
          ? { ...w, config: { ...w.config, palette: 4, colorBy: 'category' } }
          : w,
      ),
    } as Definition;
    const slice = sliceOf(colored, 'by-status');
    expect(slice).toEqual(before);
    expect('palette' in slice).toBe(false);
    expect('palette' in slice.widgets[0].config).toBe(false);
  });

  it('is the text widget alone for text', () => {
    expect(sliceOf(salesDefinition(), 'title').widgets.map((w) => w.id)).toEqual(['title']);
  });
});

describe('the history of edits', () => {
  it('undoes and redoes, joining edits of one key made soon after one another', () => {
    let now = 0;
    const history = new History<string>(() => now);
    history.record('Typed', 'a', 'title');
    now = 500;
    history.record('Typed', 'ab', 'title');
    now = 2000;
    history.record('Moved', 'abc', 'move');
    expect(history.undoLabel()).toBe('Moved');
    expect(history.undo('abcd')).toEqual({ value: 'abc', label: 'Moved' });
    expect(history.undo('abc')).toEqual({ value: 'a', label: 'Typed' });
    expect(history.undo('a')).toBeNull();
    expect(history.redo('a')).toEqual({ value: 'abc', label: 'Typed' });
    expect(history.redo('abc')).toEqual({ value: 'abcd', label: 'Moved' });
    // A new edit forgets what was undone.
    history.undo('abcd');
    history.record('Removed', 'abc');
    expect(history.redoLabel()).toBeNull();
  });

  it('keeps the last hundred edits', () => {
    const history = new History<number>(() => 0);
    for (let i = 0; i < 150; i++) {
      history.record('Edit', i);
    }
    let undone = 0;
    while (history.undo(-1)) {
      undone++;
    }
    expect(undone).toBe(100);
  });
});

describe('what publishing says changed', () => {
  it("says when its charts' palette changed (a chart's own, with the chart)", () => {
    const before = salesDefinition();
    expect(changesBetween(before, { ...before, palette: 3 })).toEqual([
      "Changed its charts' palette.",
    ]);
    const pie = before.widgets.map((w) =>
      w.config.kind === 'pie' ? { ...w, config: { ...w.config, palette: 4 } } : w,
    );
    expect(changesBetween(before, { ...before, widgets: pie } as Definition)).toEqual([
      'Changed Customers by city.',
    ]);
    expect(changesBetween(before, { ...before })).toEqual(['Nothing: it is as published.']);
  });
});

describe("a dashboard's tabs", () => {
  /** Whether no two cells overlap, and every one is within the columns. */
  function laidOut(cells: Cells, columns: number): boolean {
    const all = Object.values(cells);
    return (
      all.every((c) => c.x >= 0 && c.y >= 0 && c.w >= 1 && c.x + c.w <= columns) &&
      all.every((a, i) => all.slice(i + 1).every((b) => !collides(a, b)))
    );
  }

  it('starts with one tab holding every widget where it was, and stops as it was', () => {
    const sales = salesDefinition();
    const { definition, id } = useTabs(sales, 'Overview');
    expect(id).toBe('overview');
    expect(definition.tabs).toEqual([{ id: 'overview', label: 'Overview' }]);
    expect(definition.widgets.map((w) => w.tab)).toEqual(['overview', 'overview', 'overview']);
    expect(definition.layout).toEqual(sales.layout);
    const dropped = dropTabs(definition);
    expect(dropped).toEqual(sales);
    expect('tabs' in dropped).toBe(false);
    expect(dropped.widgets.some((w) => 'tab' in w)).toBe(false);
  });

  it('adds tabs with ids from their labels, kept when they are renamed, and moves them', () => {
    const { definition, id } = addTab(tabbedDefinition(), 'Orders');
    expect(id).toBe('orders-2');
    const renamed = renameTab(definition, id, 'Returns');
    expect(renamed.tabs?.at(-1)).toEqual({ id: 'orders-2', label: 'Returns' });
    expect(moveTab(renamed, 'orders-2', -2).tabs?.map((t) => t.id)).toEqual([
      'orders-2',
      'orders',
      'customers',
    ]);
    expect(moveTab(renamed, 'orders', -1)).toBe(renamed);
    const first = addTab(salesDefinition(), 'First').definition;
    expect(first.widgets.every((w) => w.tab === 'first')).toBe(true);
  });

  it("adds a widget to a tab's grid, the first when none is said, and copies it on its tab", () => {
    const tabbed = tabbedDefinition();
    const added = addWidget(tabbed, textDefaults(), { w: 6, h: 2 }, null, 'customers');
    expect(added.definition.widgets.at(-1)?.tab).toBe('customers');
    expect(added.definition.layout.items[added.id]).toEqual({ x: 6, y: 0, w: 6, h: 2 });
    const first = addWidget(tabbed, textDefaults(), { w: 6, h: 2 });
    expect(first.definition.widgets.at(-1)?.tab).toBe('orders');
    expect(first.definition.layout.items[first.id]).toEqual({ x: 6, y: 1, w: 6, h: 2 });
    const plain = addWidget(salesDefinition(), textDefaults(), { w: 6, h: 2 }).definition;
    expect('tab' in plain.widgets[3]).toBe(false);
    const copy = duplicateWidget(tabbed, 'by-city');
    expect(copy.definition.widgets.at(-1)?.tab).toBe('customers');
    expect(copy.definition.layout.items[copy.id]).toEqual({ x: 6, y: 0, w: 6, h: 6 });
  });

  it("moves and resizes a widget among its own tab's alone, whatever the other tabs' cells", () => {
    const tabbed = tabbedDefinition();
    const designed = designedOf(tabbed);
    fc.assert(
      fc.property(
        fc.array(
          fc.record({
            id: fc.constantFrom('title', 'by-status', 'by-city'),
            resize: fc.boolean(),
            x: fc.integer({ min: 0, max: 13 }),
            y: fc.integer({ min: 0, max: 12 }),
          }),
          { maxLength: 8 },
        ),
        (edits) => {
          let definition = tabbed;
          for (const edit of edits) {
            const before = definition;
            definition = edit.resize
              ? resizeWidget(definition, designed.id, edit.id, { w: edit.x + 1, h: edit.y + 1 })
              : moveWidget(definition, designed.id, edit.id, { x: edit.x, y: edit.y });
            const other = tabOf(definition, edit.id) === 'orders' ? 'customers' : 'orders';
            expect(cellsOn(definition, definition.layout.items, other)).toEqual(
              cellsOn(before, before.layout.items, other),
            );
          }
          for (const tab of ['orders', 'customers']) {
            expect(laidOut(cellsOn(definition, definition.layout.items, tab), 12)).toBe(true);
          }
        },
      ),
      { numRuns: 200 },
    );
  });

  it('lays narrower breakpoints out tab by tab: derived, customized, and with new columns', () => {
    const tabbed = tabbedDefinition();
    const customized = customize(tabbed, 'medium');
    expect(customized.layout.overrides['medium'].items).toEqual({
      title: { x: 0, y: 0, w: 6, h: 1 },
      'by-status': { x: 0, y: 1, w: 3, h: 6 },
      'by-city': { x: 0, y: 0, w: 3, h: 6 },
    });
    const breakpoints = tabbed.layout.breakpoints.map((b) =>
      b.id === 'wide' ? { ...b, columns: 6 } : b,
    );
    expect(setBreakpoints(tabbed, breakpoints).layout.items).toEqual({
      title: { x: 0, y: 0, w: 6, h: 1 },
      'by-status': { x: 0, y: 1, w: 3, h: 6 },
      'by-city': { x: 0, y: 0, w: 3, h: 6 },
    });
  });

  it("moves a widget to another tab's grid, where it first fits, at every breakpoint laid out", () => {
    const tabbed = customize(tabbedDefinition(), 'medium');
    const moved = moveToTab(tabbed, 'by-status', 'customers');
    expect(tabOf(moved, 'by-status')).toBe('customers');
    expect(moved.layout.items['by-status']).toEqual({ x: 6, y: 0, w: 6, h: 6 });
    expect(moved.layout.overrides['medium'].items['by-status']).toEqual({
      x: 3,
      y: 0,
      w: 3,
      h: 6,
    });
    expect(moveToTab(tabbed, 'by-status', 'orders')).toBe(tabbed);
    expect(moveToTab(tabbed, 'by-status', 'nowhere')).toBe(tabbed);
  });

  it('removes a tab with its widgets, or moves them below the next one; the last, and tabs go', () => {
    const tabbed = customize(tabbedDefinition(), 'medium');
    const removed = removeTab(tabbed, 'customers', 'remove');
    expect(removed.tabs?.map((t) => t.id)).toEqual(['orders']);
    expect(removed.widgets.map((w) => w.id)).toEqual(['title', 'by-status']);
    expect(removed.layout.overrides['medium'].items['by-city']).toBeUndefined();
    const merged = removeTab(tabbed, 'customers', 'move');
    expect(merged.widgets.map((w) => w.tab)).toEqual(['orders', 'orders', 'orders']);
    expect(merged.layout.items['by-city']).toEqual({ x: 0, y: 7, w: 6, h: 6 });
    expect(merged.layout.overrides['medium'].items['by-city']).toEqual({
      x: 0,
      y: 7,
      w: 3,
      h: 6,
    });
    const first = removeTab(tabbed, 'orders', 'move');
    expect(first.widgets.map((w) => w.tab)).toEqual(['customers', 'customers', 'customers']);
    expect(first.layout.items['title']).toEqual({ x: 0, y: 6, w: 12, h: 1 });
    const last = removeTab(removed, 'orders', 'remove');
    expect('tabs' in last).toBe(false);
    expect(last.widgets.map((w) => w.id)).toEqual(['title', 'by-status']);
  });

  it('stops using tabs with their grids one below the other, in their order', () => {
    const tabbed = moveTab(tabbedDefinition(), 'customers', -1);
    const one = dropTabs(tabbed);
    expect('tabs' in one).toBe(false);
    expect(one.layout.items).toEqual({
      'by-city': { x: 0, y: 0, w: 6, h: 6 },
      title: { x: 0, y: 6, w: 12, h: 1 },
      'by-status': { x: 0, y: 7, w: 6, h: 6 },
    });
  });

  it("leaves tabs out of widgets' slices, and says when they changed", () => {
    const tabbed = tabbedDefinition();
    const slice = sliceOf(tabbed, 'by-city');
    expect('tabs' in slice).toBe(false);
    expect(slice.widgets.some((w) => 'tab' in w)).toBe(false);
    expect(sliceOf(renameTab(tabbed, 'customers', 'People'), 'by-city')).toEqual(slice);
    expect(changesBetween(tabbed, renameTab(tabbed, 'customers', 'People'))).toEqual([
      'Changed its tabs.',
    ]);
    // Split into tabs, its widgets are where they were, on a tab: the tabs changed, not they.
    expect(changesBetween(salesDefinition(), useTabs(salesDefinition(), 'All').definition)).toEqual(
      ['Changed its tabs.'],
    );
    expect(changesBetween(tabbed, moveToTab(tabbed, 'by-city', 'orders'))).toEqual([
      'Moved Customers by city to Orders.',
      'Changed its layout.',
    ]);
  });
});
