import { salesDefinition } from '../../../../testing/dashboards';
import { History } from '../editor/history';
import { rowCount, tableDefaults, textDefaults } from './widget-defaults';
import type { Definition } from './definition';
import {
  addFilter,
  addLink,
  addSource,
  addWidget,
  customize,
  duplicateWidget,
  hideAt,
  moveWidget,
  removeSource,
  removeWidget,
  renameNewFilter,
  resetBreakpoint,
  setBreakpoints,
  setFilterApplies,
  sourceUsedBy,
  updateFilter,
  wouldCycle,
} from './definition-ops';
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
