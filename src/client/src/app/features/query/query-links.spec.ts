import type { ICellRendererParams } from 'ag-grid-community';
import { orderResultColumns } from '../../../testing/query';
import type { GridRow } from '../browse/grid/grid-columns';
import type { ResultSchema } from './query-datasource';
import {
  type LinkTarget,
  ResultLinkCell,
  gridColumnsOf,
  indexOfRelatedColId,
  leadsSomewhere,
  linkText,
  relatedColIdOf,
  relatedOf,
  resultColumnDefsOf,
} from './query-links';

const schema: ResultSchema = {
  columns: orderResultColumns(),
  rowIdentity: {
    entity: 'shop.orders',
    keyOrdinals: [0],
    related: [
      {
        kind: 'collection',
        ordinals: [0],
        target: 'shop.order_lines',
        navigation: 'order_lines',
        multiplicity: 'many',
      },
    ],
    capabilities: { canInsert: false, canUpdate: false, canDelete: false },
  },
};

const row = (values: unknown[]): GridRow => ({ id: null, k: null, v: values, r: null });

describe("a query's links", () => {
  it('say where they lead', () => {
    expect(linkText(schema.columns[1].link!)).toBe(
      'The row of shop.customers it refers to, through customer.',
    );
    expect(
      linkText({
        kind: 'collection',
        ordinals: [0],
        target: 'shop.orders',
        navigation: 'orders',
        multiplicity: 'many',
      }),
    ).toBe('The rows of shop.orders it was worked out from, through orders.');
    expect(
      linkText({
        kind: 'collection',
        ordinals: [0],
        target: 'shop.profiles',
        navigation: null,
        multiplicity: 'one',
      }),
    ).toBe('The row of shop.profiles it was worked out from.');
    expect(
      linkText({
        kind: 'drillDown',
        ordinals: [0],
        target: null,
        navigation: null,
        multiplicity: null,
      }),
    ).toBe('The rows of its group, which it was worked out from.');
  });

  it('lead nowhere from a NULL a row refers by', () => {
    const link = schema.columns[1].link!;
    expect(leadsSomewhere(link, row(['1', 'Acme', '1.00', '42']))).toBe(true);
    expect(leadsSomewhere(link, row(['1', null, '1.00', null]))).toBe(false);
    expect(leadsSomewhere(link, null)).toBe(false);
    // A group's rows are there for a NULL key too.
    expect(leadsSomewhere({ ...link, kind: 'drillDown' }, row(['1', null, '1.00', null]))).toBe(
      true,
    );
  });

  it("make the grid's columns: hidden ones hidden, the key marked, the rows that refer to each row after", () => {
    expect(gridColumnsOf(schema).map((column) => [column.name, column.isKey])).toEqual([
      ['id', true],
      ['customer', false],
      ['total', false],
      ['id', false],
    ]);
    expect(relatedOf(schema)).toEqual([
      { navigation: 'order_lines', target: 'shop.order_lines', multiplicity: 'many' },
    ]);
    const followed: [LinkTarget, GridRow][] = [];
    const defs = resultColumnDefsOf(schema, {
      follow: (target, at) => followed.push([target, at]),
    });
    expect(defs.map((def) => [def.colId, !!def.hide, def.cellRenderer === ResultLinkCell])).toEqual(
      [
        ['c0', false, false],
        ['c1', false, true],
        ['c2', false, false],
        ['c3', true, false],
        ['n0', false, true],
      ],
    );
    expect(relatedColIdOf(0)).toBe('n0');
    expect(indexOfRelatedColId('n2')).toBe(2);
    expect(indexOfRelatedColId('c2')).toBe(-1);

    // Enter follows a cell's link, but not with modifiers, nor where it leads nowhere.
    const enter = (init: KeyboardEventInit, data: GridRow) =>
      defs[1].suppressKeyboardEvent!({
        event: new KeyboardEvent('keydown', { key: 'Enter', ...init }),
        editing: false,
        data,
      } as never);
    const acme = row(['1', 'Acme', '1.00', '42']);
    expect(enter({}, acme)).toBe(true);
    expect(enter({ ctrlKey: true }, acme)).toBe(false);
    expect(enter({}, row(['2', null, '1.00', null]))).toBe(false);
    expect(followed).toEqual([[{ column: 1 }, acme]]);
    // Without the rows being an entity's, none refer to them.
    expect(
      resultColumnDefsOf({ ...schema, rowIdentity: null }, { follow: () => undefined }).length,
    ).toBe(4);
  });

  it('follow a click on the link, not with Alt, nor another button, and change in place', () => {
    const followed: GridRow[] = [];
    const cell = new ResultLinkCell();
    const acme = row(['1', 'Acme', '1.00', '42']);
    const params = (data: GridRow, leads: boolean) =>
      ({
        eGridCell: document.createElement('div'),
        data,
        content: () => ({ text: String(data.v[1] ?? 'NULL'), leads, arrow: false }),
        follow: (at: GridRow) => followed.push(at),
      }) as unknown as ICellRendererParams<GridRow>;
    cell.init(params(acme, true) as never);
    const element = cell.getGui();
    expect([element.className, element.getAttribute('role'), element.textContent]).toEqual([
      'gd-link',
      'link',
      'Acme',
    ]);
    element.dispatchEvent(new MouseEvent('click', { button: 0, bubbles: true }));
    element.dispatchEvent(new MouseEvent('click', { button: 0, altKey: true, bubbles: true }));
    element.dispatchEvent(new MouseEvent('click', { button: 1, bubbles: true }));
    expect(followed).toEqual([acme]);

    const none = row(['2', null, '1.00', null]);
    expect(cell.refresh(params(none, false) as never)).toBe(true);
    expect(cell.getGui()).toBe(element);
    expect([element.className, element.getAttribute('role'), element.textContent]).toEqual([
      '',
      null,
      'NULL',
    ]);
    element.dispatchEvent(new MouseEvent('click', { button: 0, bubbles: true }));
    expect(followed).toEqual([acme]);
  });
});
