import { describeChange, describeProperty } from './connection-snapshots';

describe('describeChange', () => {
  it('says what changed between two readings of a schema', () => {
    expect(
      describeChange({
        change: 'added',
        object: 'column',
        schema: 'main',
        table: 'orders',
        name: 'total',
      }),
    ).toBe('Added column main.orders.total');
    expect(
      describeChange({
        change: 'removed',
        object: 'foreignKey',
        table: 'orders',
        name: 'fk_customer',
      }),
    ).toBe('Removed foreign key orders.fk_customer');
    expect(describeChange({ change: 'changed', object: 'source' })).toBe('Changed source');
  });

  it('says how a property changed, or what it is or was', () => {
    expect(describeProperty({ property: 'type', from: 'int32', to: 'int64' })).toBe(
      'type: int32 → int64',
    );
    expect(describeProperty({ property: 'type', from: null, to: 'string?' })).toBe('type: string?');
    expect(describeProperty({ property: 'type', from: 'string', to: null })).toBe(
      'type was string',
    );
  });
});
