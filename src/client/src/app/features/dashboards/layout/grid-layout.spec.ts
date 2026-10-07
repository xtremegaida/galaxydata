import fc from 'fast-check';
import {
  type Breakpoint,
  type Cells,
  type DashboardLayout,
  type Placement,
  breakpointFor,
  collides,
  compact,
  derive,
  effectiveLayout,
  maxRows,
  minColumns,
  moveTo,
  normalize,
  place,
  readingOrder,
  resizeTo,
  rowsOf,
} from './grid-layout';

const runs = { numRuns: 400 };

/** Columns, and any cells (not yet a layout: they may overlap or stand outside). */
const anyCells = fc.integer({ min: 1, max: 24 }).chain((columns) =>
  fc
    .uniqueArray(fc.stringMatching(/^[a-z][a-z0-9-]{0,5}$/), { maxLength: 20 })
    .chain((ids) =>
      fc.tuple(
        fc.constant(columns),
        fc.tuple(
          ...ids.map((id) =>
            fc.record({
              id: fc.constant(id),
              x: fc.integer({ min: -2, max: 26 }),
              y: fc.integer({ min: -2, max: 30 }),
              w: fc.integer({ min: 0, max: 26 }),
              h: fc.integer({ min: 0, max: 8 }),
            }),
          ),
        ),
      ),
    )
    .map(([columns, cells]) => ({
      columns,
      cells: Object.fromEntries(cells.map(({ id, ...cell }) => [id, cell])) as Cells,
    })),
);

/** A layout as one is kept: normalized. */
const layouts = anyCells.map(({ columns, cells }) => ({
  columns,
  cells: deepFreeze(normalize(cells, columns)),
}));

function deepFreeze<T>(value: T): T {
  if (value && typeof value === 'object') {
    Object.values(value).forEach(deepFreeze);
    Object.freeze(value);
  }
  return value;
}

/** What every layout must be: within its columns, nothing over anything else, the ids it was given, compact. */
function expectLayout(cells: Cells, columns: number, ids: readonly string[]): void {
  expect(Object.keys(cells).sort()).toEqual([...ids].sort());
  for (const [id, cell] of Object.entries(cells)) {
    expect(cell.x, id).toBeGreaterThanOrEqual(0);
    expect(cell.y, id).toBeGreaterThanOrEqual(0);
    expect(cell.w, id).toBeGreaterThanOrEqual(1);
    expect(cell.h, id).toBeGreaterThanOrEqual(1);
    expect(cell.h, id).toBeLessThanOrEqual(maxRows);
    expect(cell.x + cell.w, id).toBeLessThanOrEqual(columns);
  }
  expectNoOverlaps(cells);
}

function expectNoOverlaps(cells: Cells): void {
  const ids = Object.keys(cells);
  for (let i = 0; i < ids.length; i++) {
    for (let j = i + 1; j < ids.length; j++) {
      expect(collides(cells[ids[i]], cells[ids[j]]), `${ids[i]} and ${ids[j]}`).toBe(false);
    }
  }
}

function expectCompact(cells: Cells): void {
  for (const [id, cell] of Object.entries(cells)) {
    if (cell.y === 0) {
      continue;
    }
    const up: Placement = { ...cell, y: cell.y - 1 };
    const blocked = Object.entries(cells).some(([other, at]) => other !== id && collides(up, at));
    expect(blocked, `${id} could move up`).toBe(true);
  }
}

describe('grid layout', () => {
  it('normalizes any cells into a compact layout within the columns', () => {
    fc.assert(
      fc.property(anyCells, ({ columns, cells }) => {
        const layout = normalize(deepFreeze(cells), columns);
        expectLayout(layout, columns, Object.keys(cells));
        expectCompact(layout);
        expect(normalize(layout, columns)).toEqual(layout);
        expect(compact(layout)).toEqual(layout);
      }),
      runs,
    );
  });

  it('moves a widget, keeping a layout', () => {
    fc.assert(
      fc.property(
        layouts,
        fc.nat(),
        fc.integer({ min: -3, max: 30 }),
        fc.integer({ min: -3, max: 30 }),
        ({ columns, cells }, pick, x, y) => {
          const ids = Object.keys(cells);
          fc.pre(ids.length > 0);
          const id = ids[pick % ids.length];
          const moved = moveTo(cells, id, { x, y }, columns);
          expectLayout(moved, columns, ids);
          expectCompact(moved);
          expect(moveTo(cells, id, cells[id], columns)).toEqual(cells);
        },
      ),
      runs,
    );
  });

  it('resizes a widget, keeping its column and a layout', () => {
    fc.assert(
      fc.property(
        layouts,
        fc.nat(),
        fc.integer({ min: -2, max: 30 }),
        fc.integer({ min: -2, max: 300 }),
        ({ columns, cells }, pick, w, h) => {
          const ids = Object.keys(cells);
          fc.pre(ids.length > 0);
          const id = ids[pick % ids.length];
          const resized = resizeTo(cells, id, { w, h }, columns);
          expectLayout(resized, columns, ids);
          expectCompact(resized);
          expect(resized[id].x).toBe(cells[id].x);
          expect(resized[id].w).toBe(Math.min(Math.max(w, 1), columns - cells[id].x));
          expect(resized[id].h).toBe(Math.min(Math.max(h, 1), maxRows));
        },
      ),
      runs,
    );
  });

  it('places a new widget where it fits', () => {
    fc.assert(
      fc.property(
        layouts,
        fc.integer({ min: 1, max: 30 }),
        fc.integer({ min: 1, max: 6 }),
        ({ columns, cells }, w, h) => {
          const cell = place(cells, { w, h }, columns);
          expect(cell.x + cell.w).toBeLessThanOrEqual(columns);
          expect(Object.values(cells).some((other) => collides(cell, other))).toBe(false);
        },
      ),
      runs,
    );
  });

  it('derives a layout for other columns in the same reading order', () => {
    fc.assert(
      fc.property(layouts, fc.integer({ min: 1, max: 24 }), ({ columns, cells }, to) => {
        const derived = derive(cells, columns, to);
        expectLayout(derived, to, Object.keys(cells));
        if (to !== columns) {
          expect(readingOrder(derived)).toEqual(readingOrder(cells));
        } else {
          expect(derived).toEqual(cells);
        }
        if (to === 1) {
          const order = readingOrder(derived);
          order.forEach((id, i) => {
            expect(derived[id].x).toBe(0);
            if (i > 0) {
              expect(derived[id].y).toBe(derived[order[i - 1]].y + derived[order[i - 1]].h);
            }
          });
        }
      }),
      runs,
    );
  });

  it('finds a breakpoint for every width, wider ones for wider widths', () => {
    const breakpoints: Breakpoint[] = [
      { id: 'narrow', label: 'Narrow', minWidth: 0, columns: 1 },
      { id: 'medium', label: 'Medium', minWidth: 720, columns: 6 },
      { id: 'wide', label: 'Wide', minWidth: 1200, columns: 12 },
    ];
    fc.assert(
      fc.property(fc.nat({ max: 5000 }), fc.nat({ max: 5000 }), (a, b) => {
        const [narrower, wider] = a <= b ? [a, b] : [b, a];
        expect(breakpoints.indexOf(breakpointFor(narrower, breakpoints))).toBeLessThanOrEqual(
          breakpoints.indexOf(breakpointFor(wider, breakpoints)),
        );
      }),
      runs,
    );
    expect([
      breakpointFor(null, breakpoints).id,
      breakpointFor(719, breakpoints).id,
      breakpointFor(720, breakpoints).id,
    ]).toEqual(['wide', 'narrow', 'medium']);
  });

  describe('as people move widgets', () => {
    const two: Cells = deepFreeze({ a: { x: 0, y: 0, w: 6, h: 2 }, b: { x: 0, y: 2, w: 6, h: 2 } });

    it('swaps a widget moved down onto another, which compaction would otherwise undo', () => {
      expect(moveTo(two, 'a', { x: 0, y: 2 }, 12)).toEqual({
        a: { x: 0, y: 2, w: 6, h: 2 },
        b: { x: 0, y: 0, w: 6, h: 2 },
      });
      expect(moveTo(two, 'b', { x: 0, y: 0 }, 12)).toEqual({
        a: { x: 0, y: 2, w: 6, h: 2 },
        b: { x: 0, y: 0, w: 6, h: 2 },
      });
    });

    it('pushes down what a widget lands on, in a chain', () => {
      const three: Cells = {
        ...two,
        c: { x: 0, y: 4, w: 12, h: 1 },
        d: { x: 6, y: 0, w: 6, h: 4 },
      };
      const moved = moveTo(three, 'd', { x: 0, y: 0 }, 12);
      expect(moved).toEqual({
        d: { x: 0, y: 0, w: 6, h: 4 },
        a: { x: 0, y: 4, w: 6, h: 2 },
        b: { x: 0, y: 6, w: 6, h: 2 },
        c: { x: 0, y: 8, w: 12, h: 1 },
      });
      expect(rowsOf(moved)).toBe(9);
    });

    it('keeps a widget moved past the edge within the columns', () => {
      expect(moveTo(two, 'a', { x: 20, y: 0 }, 12)['a']).toEqual({ x: 6, y: 0, w: 6, h: 2 });
    });
  });

  describe('narrower breakpoints', () => {
    const designed: Cells = deepFreeze({
      title: { x: 0, y: 0, w: 12, h: 1 },
      left: { x: 0, y: 1, w: 6, h: 4 },
      right: { x: 6, y: 1, w: 6, h: 2 },
      small: { x: 6, y: 3, w: 3, h: 2 },
    });
    const layout: DashboardLayout = {
      rowHeight: 48,
      gap: 12,
      breakpoints: [
        { id: 'narrow', label: 'Narrow', minWidth: 0, columns: 1 },
        { id: 'medium', label: 'Medium', minWidth: 720, columns: 4 },
        { id: 'wide', label: 'Wide', minWidth: 1200, columns: 12 },
      ],
      items: designed,
      overrides: {},
    };

    it('scales widths to the columns and places them in reading order', () => {
      expect(derive(designed, 12, 4)).toEqual({
        title: { x: 0, y: 0, w: 4, h: 1 },
        left: { x: 0, y: 1, w: 2, h: 4 },
        right: { x: 2, y: 1, w: 2, h: 2 },
        small: { x: 2, y: 3, w: 1, h: 2 },
      });
    });

    it('stacks them in one column', () => {
      expect(effectiveLayout(layout, 'narrow', Object.keys(designed)).cells).toEqual({
        title: { x: 0, y: 0, w: 1, h: 1 },
        left: { x: 0, y: 1, w: 1, h: 4 },
        right: { x: 0, y: 5, w: 1, h: 2 },
        small: { x: 0, y: 7, w: 1, h: 2 },
      });
    });

    it('keeps a breakpoint laid out by hand, without widgets gone, and places those added after the one before them', () => {
      const custom: DashboardLayout = {
        ...layout,
        items: { ...designed, added: { x: 0, y: 5, w: 12, h: 1 } },
        overrides: {
          medium: {
            columns: 4,
            items: {
              title: { x: 0, y: 0, w: 4, h: 1 },
              right: { x: 0, y: 1, w: 4, h: 2 },
              left: { x: 0, y: 3, w: 4, h: 2 },
              gone: { x: 0, y: 5, w: 4, h: 1 },
            },
            hidden: ['small'],
          },
        },
      };
      const effective = effectiveLayout(custom, 'medium', [
        'title',
        'left',
        'right',
        'small',
        'added',
      ]);
      expect(effective.placed).toEqual(['added']);
      expect(effective.cells).toEqual({
        title: { x: 0, y: 0, w: 4, h: 1 },
        right: { x: 0, y: 1, w: 4, h: 2 },
        left: { x: 0, y: 3, w: 4, h: 2 },
        added: { x: 0, y: 5, w: 4, h: 1 },
      });
      // The breakpoint now has more columns than it was laid out with: the override is derived anew.
      const wider: DashboardLayout = {
        ...custom,
        breakpoints: [
          layout.breakpoints[0],
          { ...layout.breakpoints[1], columns: 8 },
          layout.breakpoints[2],
        ],
      };
      expect(effectiveLayout(wider, 'medium', ['title', 'right', 'left']).cells['title']).toEqual({
        x: 0,
        y: 0,
        w: 8,
        h: 1,
      });
    });

    it('gives the designed layout at the widest breakpoint', () => {
      expect(effectiveLayout(layout, 'wide', ['title', 'left']).cells).toEqual({
        title: designed['title'],
        left: designed['left'],
      });
    });

    it('turns a least width in pixels into columns', () => {
      expect(minColumns(300, layout.breakpoints[2], 12)).toBe(4);
      expect(minColumns(300, layout.breakpoints[0], 12)).toBe(1);
      expect(minColumns(5000, layout.breakpoints[1], 12)).toBe(4);
    });
  });
});
