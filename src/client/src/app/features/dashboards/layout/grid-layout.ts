/**
 * The layout of a dashboard's grid, as pure functions of its cells: a widget's cell is from column `x` and row `y`
 * (from 0), `w` columns wide and `h` rows high. Layouts are kept compact (nothing could move up a row), every cell
 * within its columns, none over another. The widest breakpoint is the one designed; narrower ones are derived from
 * it (widths scaled to their columns, then placed in reading order), unless a breakpoint has been laid out by hand
 * (an override), which widgets added since are placed into. Nothing given is changed: every function gives anew.
 */

export interface Placement {
  readonly x: number;
  readonly y: number;
  readonly w: number;
  readonly h: number;
}

/** Cells by widget id. */
export type Cells = Readonly<Record<string, Placement>>;

/** The smallest a widget may be, in columns and rows (one of each when not said). */
export type MinSizes = Readonly<Record<string, { readonly w: number; readonly h: number }>>;

export interface Breakpoint {
  readonly id: string;
  readonly label: string;
  readonly minWidth: number;
  readonly columns: number;
}

export interface LayoutOverride {
  readonly columns: number;
  readonly items: Cells;
  readonly hidden: readonly string[];
}

export interface DashboardLayout {
  readonly rowHeight: number;
  readonly gap: number;
  /** Ascending, the first from 0; the last is the one designed. */
  readonly breakpoints: readonly Breakpoint[];
  readonly items: Cells;
  readonly overrides: Readonly<Record<string, LayoutOverride>>;
}

/** The highest a widget may be, in rows. */
export const maxRows = 200;

export function collides(a: Placement, b: Placement): boolean {
  return a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
}

/** The ids in reading order: by row, then column (then id, so it is the same every time). */
export function readingOrder(cells: Cells): string[] {
  return Object.keys(cells).sort((a, b) => order(cells[a], cells[b]) || compare(a, b));
}

/** Cells, every one within the columns and its smallest size, overlapping ones pushed down in reading order, compact. */
export function normalize(
  cells: Cells,
  columns: number,
  mins: MinSizes = {},
): Record<string, Placement> {
  const placed: Record<string, Placement> = {};
  for (const id of readingOrder(cells)) {
    let cell = clamp(cells[id], columns, mins[id]);
    while (hits(cell, placed)) {
      cell = { ...cell, y: cell.y + 1 };
    }
    placed[id] = cell;
  }
  return compact(placed);
}

/**
 * Every cell moved up as far as it goes, in reading order; `pinned` cells stay where they are and are placed
 * first. Cells that collide with a pinned one go down until they don't.
 */
export function compact(cells: Cells, pinned: readonly string[] = []): Record<string, Placement> {
  const placed: Record<string, Placement> = {};
  for (const id of pinned) {
    if (cells[id]) {
      placed[id] = cells[id];
    }
  }
  for (const id of readingOrder(cells)) {
    if (pinned.includes(id)) {
      continue;
    }
    let y = cells[id].y;
    while (y > 0 && !hits({ ...cells[id], y: y - 1 }, placed)) {
      y--;
    }
    while (hits({ ...cells[id], y }, placed)) {
      y++;
    }
    placed[id] = { ...cells[id], y };
  }
  return placed;
}

/**
 * A widget moved to `to` (kept within the columns). Those it lands on move: up above it when they fit there (what
 * moving down onto a widget means; compaction would undo it otherwise), else down below it, and those they land on
 * in turn. Then everything is compacted.
 */
export function moveTo(
  cells: Cells,
  id: string,
  to: { x: number; y: number },
  columns: number,
): Record<string, Placement> {
  const cell = cells[id];
  if (!cell) {
    return { ...cells };
  }
  const moved: Placement = {
    ...cell,
    x: clampTo(to.x, 0, columns - cell.w),
    y: Math.max(0, Math.round(to.y)),
  };
  const next: Record<string, Placement> = { ...cells, [id]: moved };
  for (const other of readingOrder(next)) {
    if (other === id || !collides(moved, next[other])) {
      continue;
    }
    const above: Placement = { ...next[other], y: moved.y - next[other].h };
    const rest = without(next, other);
    if (above.y >= 0 && !hits(above, rest)) {
      next[other] = above;
    }
  }
  pushDown(next, id);
  return compact(next);
}

/** A widget given a size (within the columns, its smallest and `maxRows`); those it then covers go down, and everything is compacted. */
export function resizeTo(
  cells: Cells,
  id: string,
  size: { w: number; h: number },
  columns: number,
  min?: { w: number; h: number },
): Record<string, Placement> {
  const cell = cells[id];
  if (!cell) {
    return { ...cells };
  }
  const minW = Math.min(min?.w ?? 1, columns);
  const resized: Placement = {
    ...cell,
    w: clampTo(Math.round(size.w), minW, columns - cell.x),
    h: clampTo(Math.round(size.h), min?.h ?? 1, maxRows),
  };
  const next: Record<string, Placement> = { ...cells, [id]: resized };
  pushDown(next, id);
  return compact(next);
}

/** Where a new widget of `size` goes: the first place it fits, row by row from the top. */
export function place(cells: Cells, size: { w: number; h: number }, columns: number): Placement {
  return firstFit(cells, Math.min(Math.max(size.w, 1), columns), Math.max(size.h, 1), columns, {
    y: 0,
    x: 0,
  });
}

/**
 * A layout for fewer (or more) columns: widths scaled (`round(w · to / from)`, no less than a widget's smallest),
 * heights kept, each cell placed in reading order at the first place it fits after the one before. Not compacted,
 * as compaction could change the reading order. The same number of columns gives the layout as it is.
 */
export function derive(
  cells: Cells,
  from: number,
  to: number,
  mins: MinSizes = {},
): Record<string, Placement> {
  if (from === to) {
    return normalize(cells, to, mins);
  }
  const placed: Record<string, Placement> = {};
  let cursor = { y: 0, x: 0 };
  for (const id of readingOrder(cells)) {
    const cell = cells[id];
    const w = clampTo(Math.max(Math.round((cell.w * to) / from), mins[id]?.w ?? 1), 1, to);
    const at = firstFit(placed, w, Math.max(cell.h, mins[id]?.h ?? 1), to, cursor);
    placed[id] = at;
    cursor = { y: at.y, x: at.x + at.w };
  }
  return placed;
}

/** A breakpoint's layout, and the widgets placed into it automatically (added since it was laid out by hand). */
export interface EffectiveLayout {
  readonly cells: Record<string, Placement>;
  readonly columns: number;
  readonly placed: readonly string[];
}

/**
 * The layout at a breakpoint for the widgets there are: the designed layout at the widest; derived from it at
 * others; or the breakpoint's override, without widgets gone since and those it hides, derived anew if the
 * breakpoint's columns changed since, with widgets added since placed after the one before them in the designed
 * reading order.
 */
export function effectiveLayout(
  layout: DashboardLayout,
  breakpoint: string,
  widgets: readonly string[],
  mins: MinSizes = {},
): EffectiveLayout {
  const designed = layout.breakpoints.at(-1)!;
  const target = layout.breakpoints.find((b) => b.id === breakpoint) ?? designed;
  const present = new Set(widgets);
  const designedCells = only(layout.items, present);
  if (target.id === designed.id) {
    return {
      cells: normalize(designedCells, designed.columns, mins),
      columns: designed.columns,
      placed: [],
    };
  }
  const custom = layout.overrides[target.id];
  if (!custom) {
    return {
      cells: derive(designedCells, designed.columns, target.columns, mins),
      columns: target.columns,
      placed: [],
    };
  }
  const hidden = new Set(custom.hidden);
  const kept = only(custom.items, new Set([...present].filter((id) => !hidden.has(id))));
  const cells: Record<string, Placement> =
    custom.columns === target.columns
      ? normalize(kept, target.columns, mins)
      : derive(kept, custom.columns, target.columns, mins);
  const order = readingOrder(designedCells);
  const added = order.filter((id) => !(id in custom.items) && !hidden.has(id));
  for (const id of added) {
    const before = order
      .slice(0, order.indexOf(id))
      .reverse()
      .find((other) => other in cells);
    const cursor = before
      ? { y: cells[before].y, x: cells[before].x + cells[before].w }
      : { y: 0, x: 0 };
    const designedCell = designedCells[id];
    const w = clampTo(
      Math.max(Math.round((designedCell.w * target.columns) / designed.columns), mins[id]?.w ?? 1),
      1,
      target.columns,
    );
    cells[id] = firstFit(cells, w, designedCell.h, target.columns, cursor);
  }
  return { cells, columns: target.columns, placed: added };
}

/** The breakpoint for a width: the last whose least width it reaches; the widest when the width isn't known. */
export function breakpointFor(
  width: number | null,
  breakpoints: readonly Breakpoint[],
): Breakpoint {
  if (width === null) {
    return breakpoints.at(-1)!;
  }
  let found = breakpoints[0];
  for (const breakpoint of breakpoints) {
    if (breakpoint.minWidth <= width) {
      found = breakpoint;
    }
  }
  return found;
}

/** The rows a layout takes. */
export function rowsOf(cells: Cells): number {
  return Object.values(cells).reduce((rows, cell) => Math.max(rows, cell.y + cell.h), 0);
}

/** A widget's least size in columns at a breakpoint, from its least width in pixels: the columns that are at least that wide there. */
export function minColumns(widthPx: number, breakpoint: Breakpoint, gap: number): number {
  const column = (breakpoint.minWidth - gap * (breakpoint.columns - 1)) / breakpoint.columns;
  return column <= 0
    ? 1
    : clampTo(Math.ceil((widthPx + gap) / (column + gap)), 1, breakpoint.columns);
}

function firstFit(
  cells: Cells,
  w: number,
  h: number,
  columns: number,
  from: { y: number; x: number },
): Placement {
  for (let y = from.y; ; y++) {
    for (let x = y === from.y ? from.x : 0; x + w <= columns; x++) {
      const cell = { x, y, w, h };
      if (!hits(cell, cells)) {
        return cell;
      }
    }
  }
}

/** Those `id` covers move down below it, and those they then cover, in turn; downwards only, so it ends. */
function pushDown(cells: Record<string, Placement>, id: string): void {
  const queue = [id];
  while (queue.length > 0) {
    const mover = cells[queue.shift()!];
    for (const other of readingOrder(cells)) {
      if (cells[other] === mover || !collides(mover, cells[other])) {
        continue;
      }
      cells[other] = { ...cells[other], y: mover.y + mover.h };
      queue.push(other);
    }
  }
}

function hits(cell: Placement, cells: Cells): boolean {
  for (const id in cells) {
    if (collides(cell, cells[id])) {
      return true;
    }
  }
  return false;
}

function clamp(cell: Placement, columns: number, min?: { w: number; h: number }): Placement {
  const w = clampTo(Math.round(cell.w), Math.min(min?.w ?? 1, columns), columns);
  return {
    x: clampTo(Math.round(cell.x), 0, columns - w),
    y: Math.max(0, Math.round(cell.y)),
    w,
    h: clampTo(Math.round(cell.h), min?.h ?? 1, maxRows),
  };
}

function clampTo(value: number, low: number, high: number): number {
  return Math.min(Math.max(value, low), Math.max(low, high));
}

function only(cells: Cells, ids: ReadonlySet<string>): Record<string, Placement> {
  const kept: Record<string, Placement> = {};
  for (const id in cells) {
    if (ids.has(id)) {
      kept[id] = cells[id];
    }
  }
  return kept;
}

function without(cells: Cells, id: string): Record<string, Placement> {
  const rest: Record<string, Placement> = { ...cells };
  delete rest[id];
  return rest;
}

function order(a: Placement, b: Placement): number {
  return a.y - b.y || a.x - b.x;
}

function compare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
