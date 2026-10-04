import { schemaNode, sourceNode, tableNode } from '../../../testing/catalog';
import { compactCount, iconOf, isEntity, kindOf, matchParts, sourceKindName } from './tree-nodes';

describe('tree nodes', () => {
  it('tells entities from what holds them', () => {
    expect(isEntity(tableNode('shop.orders'))).toBe(true);
    expect(isEntity(tableNode('shop.open_orders', { kind: 'view' }))).toBe(true);
    expect(isEntity(tableNode('reports.big', { kind: 'virtual' }))).toBe(true);
    expect(isEntity(sourceNode('shop'))).toBe(false);
    expect(isEntity(schemaNode('shop.sales'))).toBe(false);
    expect(isEntity(schemaNode('reports', { kind: 'folder' }))).toBe(false);
  });

  it('names and draws each kind of node', () => {
    expect(kindOf(sourceNode('shop'))).toBe('connection');
    expect(kindOf(schemaNode('reports', { kind: 'folder' }))).toBe('folder of virtual entities');
    expect(iconOf(sourceNode('shop'))).toBe('database');
    expect(iconOf(sourceNode('xl', { sourceKind: 'excel' }))).toBe('table_view');
    expect(iconOf(tableNode('shop.open_orders', { kind: 'view' }))).toBe('table_eye');
    expect(sourceKindName('sqlserver')).toBe('SQL Server');
    expect(sourceKindName('oracle')).toBe('oracle');
  });

  it('counts rows as they are read at a glance', () => {
    expect(compactCount(950, 'en-US')).toBe('950');
    expect(compactCount(12_345, 'en-US')).toBe('12.3K');
    expect(compactCount(1_200_000, 'en-US')).toBe('1.2M');
  });

  it('parts a text into what was looked for, ignoring case, and what is between', () => {
    expect(matchParts('order_lines', 'ORDER')).toEqual([
      { text: 'order', match: true },
      { text: '_lines', match: false },
    ]);
    expect(matchParts('shop.orders.order_id', 'order')).toEqual([
      { text: 'shop.', match: false },
      { text: 'order', match: true },
      { text: 's.', match: false },
      { text: 'order', match: true },
      { text: '_id', match: false },
    ]);
    expect(matchParts('customers', ' ')).toEqual([{ text: 'customers', match: false }]);
    expect(matchParts('İstanbul', 'stan')).toEqual([{ text: 'İstanbul', match: false }]);
  });
});
