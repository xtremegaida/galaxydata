import type { ColDef, ValueGetterParams } from 'ag-grid-community';
import {
  customerReference,
  linesCollection,
  linkedColumns,
  linkedRows,
  rowOf,
} from '../../../../testing/browse';
import type { GridRow } from './grid-columns';
import {
  type CellLink,
  type GridLinks,
  type LinkSchema,
  LinkCell,
  type LinkCellParams,
  cellLinkOf,
  collectionColIdOf,
  collectionText,
  displayOf,
  displayText,
  indexOfCollectionColId,
  linkedColIdsOf,
  linkedColumnDefsOf,
  referenceOf,
  referenceText,
} from './grid-links';

const schema: LinkSchema = {
  columns: linkedColumns(),
  references: [customerReference],
  collections: [linesCollection],
};

/** Links that lead to `/to/<navigation>/<row>`, and say what was followed. */
function linksTo(): GridLinks & { followed: CellLink[] } {
  const followed: CellLink[] = [];
  return {
    followed,
    href: (link) => `/to/${link.navigation}/${link.row.join('~')}`,
    follow: (link) => followed.push(link),
  };
}

/** The params a column's renderer is given for a row's cell. */
function paramsOf(def: ColDef<GridRow>, row: GridRow | undefined): LinkCellParams {
  const value = row
    ? (def.valueGetter as (params: Partial<ValueGetterParams<GridRow>>) => unknown)({ data: row })
    : undefined;
  return {
    value,
    data: row,
    colDef: def,
    eGridCell: document.createElement('div'),
    ...(def.cellRendererParams as object),
  } as unknown as LinkCellParams;
}

/** What a column's renderer makes of a row's cell. */
function rendered(def: ColDef<GridRow>, row: GridRow | undefined): HTMLElement {
  return renderer(def, row).getGui();
}

/** A column's renderer of a row's cell. */
function renderer(def: ColDef<GridRow>, row: GridRow | undefined): LinkCell {
  expect(def.cellRenderer).toBe(LinkCell);
  const cell = new LinkCell();
  cell.init(paramsOf(def, row));
  return cell;
}

describe("the grid's links", () => {
  it('follows the row a cell refers to, from the row with the cell', () => {
    const [acme, nameless, none] = linkedRows(3);
    expect(cellLinkOf(schema, 'c1', acme)).toEqual({ navigation: 'customer', row: ['1001'] });
    expect(cellLinkOf(schema, 'c1', nameless)).toEqual({ navigation: 'customer', row: ['1002'] });
    expect(cellLinkOf(schema, 'c1', none)).toBeNull();
    expect(cellLinkOf(schema, 'c0', acme)).toBeNull();
    expect(cellLinkOf(schema, 'c9', acme)).toBeNull();
  });

  it("follows a collection from any row with a key, a composite key's values as text", () => {
    const [, , none] = linkedRows(3);
    expect(cellLinkOf(schema, 'n0', none)).toEqual({ navigation: 'order_lines', row: ['1003'] });
    expect(cellLinkOf(schema, 'n0', rowOf(['1', '2', 'x'], [1, 'a~b']))).toEqual({
      navigation: 'order_lines',
      row: ['1', 'a~b'],
    });
    expect(cellLinkOf(schema, 'n1', none)).toBeNull();
  });

  it('follows nothing from rows without a key, or not loaded', () => {
    const keyless = { ...linkedRows(1)[0], id: null, k: null };
    expect(cellLinkOf(schema, 'c1', keyless)).toBeNull();
    expect(cellLinkOf(schema, 'n0', keyless)).toBeNull();
    expect(cellLinkOf(schema, 'n0', undefined)).toBeNull();
  });

  it('follows nothing when any of the values that refer to a row is null', () => {
    const composite: LinkSchema = {
      ...schema,
      references: [{ ...customerReference, columns: [1, 2] }],
    };
    expect(cellLinkOf(composite, 'c1', rowOf(['1', '42', 'open']))).not.toBeNull();
    expect(cellLinkOf(composite, 'c1', rowOf(['1', '42', null]))).toBeNull();
  });

  it('names collections by their places, and finds their references', () => {
    expect(collectionColIdOf(2)).toBe('n2');
    expect(indexOfCollectionColId('n2')).toBe(2);
    expect(indexOfCollectionColId('c2')).toBe(-1);
    expect(indexOfCollectionColId('n')).toBe(-1);
    expect(referenceOf(schema, 1)).toBe(customerReference);
    expect(referenceOf(schema, 0)).toBeNull();
    expect(referenceOf(schema, 5)).toBeNull();
  });

  it('shows the display values of the rows referred to as text', () => {
    const column = schema.columns[1];
    expect(displayOf(column, linkedRows(1)[0])).toBe('Acme');
    expect(displayOf(column, linkedRows(2)[1])).toBeNull();
    expect(displayOf(column, { ...linkedRows(1)[0], r: [7] })).toBe('7');
    expect(displayOf(column, { ...linkedRows(1)[0], r: null })).toBeNull();
    expect(displayOf(schema.columns[0], linkedRows(1)[0])).toBeNull();
    expect(displayOf(column, null)).toBeNull();
    // Date-times read with a space, as the grid shows them.
    expect(displayOf(column, { ...linkedRows(1)[0], r: ['2026-01-05T08:30:00'] })).toBe(
      '2026-01-05 08:30:00',
    );
    expect(displayText('Tea T-shirt')).toBe('Tea T-shirt');
    expect(displayText(true)).toBe('true');
  });

  it('says what references and collections are', () => {
    expect(referenceText(customerReference)).toBe(
      'Refers to a row of shop.customers, through customer.',
    );
    expect(collectionText(linesCollection)).toBe(
      'The rows of shop.order_lines that refer to this row.',
    );
    expect(collectionText({ ...linesCollection, multiplicity: 'zeroOrOne' })).toBe(
      'The row of shop.order_lines that refers to this row.',
    );
  });

  it("shows a reference's display value as a link, with the value itself beside it", () => {
    const links = linksTo();
    const defs = linkedColumnDefsOf(schema, links, true);
    const [acme, nameless, none] = linkedRows(3);
    const cell = rendered(defs[1], acme);
    const link = cell.querySelector('a')!;
    expect(link.textContent).toBe('Acme');
    expect(link.getAttribute('href')).toBe('/to/customer/1001');
    expect(link.tabIndex).toBe(-1);
    expect(cell.querySelector('.gd-raw')?.textContent).toBe('42');
    // No display value: the value itself is the link.
    const bare = rendered(defs[1], nameless);
    expect(bare.querySelector('a')?.textContent).toBe('43');
    expect(bare.querySelector('.gd-raw')).toBeNull();
    // A display value that is the value itself isn't said twice.
    const same = rendered(defs[1], { ...acme, r: ['42'] });
    expect(same.querySelector('a')?.textContent).toBe('42');
    expect(same.querySelector('.gd-raw')).toBeNull();
    // Refers to no row: no link, and text as text.
    for (const [row, text] of [
      [none, 'NULL'],
      [undefined, ''],
    ] as const) {
      const cell = rendered(defs[1], row);
      expect(cell.textContent).toBe(text);
      expect(cell.querySelector('a')).toBeNull();
    }
    const marked = rendered(defs[1], { ...acme, r: ['<b>Acme</b>'] });
    expect(marked.querySelector('b')).toBeNull();
    expect(marked.querySelector('a')?.textContent).toBe('<b>Acme</b>');
    // Not a number's cell, though its values are numbers: the display value is text.
    expect(defs[1].cellClass).toBeUndefined();
    expect(defs[1].headerTooltip).toBe(
      'customer_id: int64?. From shop.orders.customer_id. Refers to a row of shop.customers, through customer.',
    );
    expect(defs[0].cellRenderer).toBeUndefined();
  });

  it('follows a link clicked without choosing its row; the browser opens it otherwise, but not with Alt', () => {
    const links = linksTo();
    const link = rendered(
      linkedColumnDefsOf(schema, links, true)[1],
      linkedRows(1)[0],
    ).querySelector('a')!;
    const row = document.createElement('div');
    row.append(link);
    const rowClicks: Event[] = [];
    row.addEventListener('click', (event) => rowClicks.push(event));
    const click = (init: MouseEventInit) => {
      const event = new MouseEvent('click', { bubbles: true, cancelable: true, ...init });
      link.dispatchEvent(event);
      return event;
    };
    expect(click({}).defaultPrevented).toBe(true);
    expect(links.followed).toEqual([{ navigation: 'customer', row: ['1001'] }]);
    for (const modified of [
      { ctrlKey: true },
      { metaKey: true },
      { shiftKey: true },
      { button: 1 },
    ]) {
      expect(click(modified).defaultPrevented).toBe(false);
    }
    // Alt would download it (Chrome), and is how a link's text is selected: nothing happens.
    expect(click({ altKey: true }).defaultPrevented).toBe(true);
    expect(links.followed.length).toBe(1);
    expect(rowClicks).toEqual([]);
  });

  it('follows the link of a cell on Enter, before the grid or the browser does anything with it', () => {
    const links = linksTo();
    const defs = linkedColumnDefsOf(schema, links, true);
    const [acme, , none] = linkedRows(3);
    const pressed = (
      def: ColDef<GridRow>,
      data: GridRow,
      init: KeyboardEventInit,
      type = 'keydown',
    ) => {
      const event = new KeyboardEvent(type, { key: 'Enter', cancelable: true, ...init });
      const suppressed = def.suppressKeyboardEvent!({ event, data, editing: false } as never);
      return [suppressed, event.defaultPrevented];
    };
    expect(pressed(defs[1], acme, {})).toEqual([true, true]);
    expect(pressed(defs[3], none, {})).toEqual([true, true]);
    expect(links.followed).toEqual([
      { navigation: 'customer', row: ['1001'] },
      { navigation: 'order_lines', row: ['1003'] },
    ]);
    for (const init of [{ ctrlKey: true }, { shiftKey: true }, { altKey: true }, { key: ' ' }]) {
      expect(pressed(defs[1], acme, init)).toEqual([false, false]);
    }
    expect(pressed(defs[1], acme, {}, 'keypress')).toEqual([false, false]);
    expect(pressed(defs[1], none, {})).toEqual([false, false]);
    expect(
      defs[1].suppressKeyboardEvent!({
        event: new KeyboardEvent('keydown', { key: 'Enter' }),
        data: acme,
        editing: true,
      } as never),
    ).toBe(false);
    expect(links.followed.length).toBe(2);
    expect(defs[0].suppressKeyboardEvent).toBeUndefined();
    expect(linkedColumnDefsOf(schema, null, true)[1].suppressKeyboardEvent).toBeUndefined();
  });

  it('shows another row or another address in place, so the keyboard on a link stays on it', () => {
    let to = 'to';
    const links: GridLinks = {
      href: (link) => `/${to}/${link.navigation}/${link.row.join('~')}`,
      follow: () => undefined,
    };
    const def = linkedColumnDefsOf(schema, links, true)[1];
    const [acme, nameless, none] = linkedRows(3);
    const cell = renderer(def, acme);
    const link = cell.getGui().querySelector('a')!;
    to = 'elsewhere';
    expect(cell.refresh(paramsOf(def, nameless))).toBe(true);
    expect(cell.getGui().querySelector('a')).toBe(link);
    expect(link.getAttribute('href')).toBe('/elsewhere/customer/1002');
    expect(link.textContent).toBe('43');
    expect(cell.getGui().querySelector('.gd-raw')).toBeNull();
    cell.refresh(paramsOf(def, acme));
    expect(cell.getGui().querySelector('.gd-raw')?.textContent).toBe('42');
    // No row referred to: no link; and back.
    cell.refresh(paramsOf(def, none));
    expect(cell.getGui().querySelector('a')).toBeNull();
    expect(cell.getGui().textContent).toBe('NULL');
    cell.refresh(paramsOf(def, acme));
    expect(cell.getGui().querySelector('a')?.textContent).toBe('Acme');
  });

  it("shows a column for each collection, a link to its rows in each row's", () => {
    const links = linksTo();
    const defs = linkedColumnDefsOf(schema, links, true);
    expect(defs.map((def) => def.colId)).toEqual(['c0', 'c1', 'c2', 'n0']);
    expect(linkedColIdsOf(schema, true)).toEqual(['c1', 'n0']);
    const lines = defs[3];
    expect(lines).toMatchObject({
      headerName: 'order_lines',
      headerTooltip: 'order_lines: The rows of shop.order_lines that refer to this row.',
      sortable: false,
      filter: false,
    });
    const link = rendered(lines, linkedRows(1)[0]).querySelector('a')!;
    expect(link.textContent).toBe('order_lines ›');
    expect(link.getAttribute('href')).toBe('/to/order_lines/1001');
    // The arrow is drawn, not read.
    expect(link.querySelector('[aria-hidden=true]')?.textContent).toBe(' ›');
    for (const row of [{ ...linkedRows(1)[0], id: null }, undefined]) {
      expect(rendered(lines, row).textContent).toBe('');
    }
    const value = (data: GridRow | undefined) =>
      (lines.valueGetter as (params: Partial<ValueGetterParams<GridRow>>) => unknown)({ data });
    expect(value(linkedRows(1)[0])).toBe('order_lines ›');
    expect(value({ ...linkedRows(1)[0], id: null })).toBe('');
    expect(value(undefined)).toBeUndefined();
  });

  it('shows no collections of rows without keys, which lead nowhere', () => {
    expect(linkedColumnDefsOf(schema, linksTo(), false).map((def) => def.colId)).toEqual([
      'c0',
      'c1',
      'c2',
    ]);
    expect(linkedColIdsOf(schema, false)).toEqual([]);
  });

  it('shows display values without links, and no collections, without links to follow', () => {
    const defs = linkedColumnDefsOf(schema, null, true);
    expect(defs.map((def) => def.colId)).toEqual(['c0', 'c1', 'c2']);
    const cell = rendered(defs[1], linkedRows(1)[0]);
    expect(cell.querySelector('a')).toBeNull();
    expect(cell.querySelector('.gd-display')?.textContent).toBe('Acme');
    expect(cell.querySelector('.gd-raw')?.textContent).toBe('42');
  });
});
