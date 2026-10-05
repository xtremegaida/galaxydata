import {
  issueOf,
  overlayOf,
  overrideOf,
  relationOf,
  settingsOf,
  virtualEntityOf,
} from '../../../../testing/overlay';
import {
  itemLink,
  itemName,
  itemTitle,
  itemsOf,
  namesOf,
  overrideText,
  relationText,
  settingsParts,
  trimmed,
  worstOf,
} from './overlay-items';

describe('overlay items', () => {
  it('says what each is', () => {
    expect(relationText(relationOf({ fromColumns: ['a', 'b'], toColumns: ['x', 'y'] }))).toBe(
      'wh.orders (a, b) → shop.customers (x, y)',
    );
    expect(overrideText({ renameTo: 'buyer', hidden: false })).toBe('Renamed buyer');
    expect(overrideText({ renameTo: null, hidden: true })).toBe('Hidden');
    expect(overrideText({ renameTo: 'buyer', hidden: true })).toBe('Renamed buyer, and hidden');
    expect(settingsParts(settingsOf())).toEqual(['Shown by name', "1 column's settings"]);
    expect(
      settingsParts(
        settingsOf({
          key: ['tenant', 'id'],
          displayColumn: null,
          hidden: true,
          columns: [
            { name: 'a', hidden: true },
            { name: 'b', hidden: true },
          ],
        }),
      ),
    ).toEqual(['Key: tenant, id', 'Hidden', "2 columns' settings"]);
    expect(settingsParts(settingsOf({ displayColumn: null, columns: [] }))).toEqual([
      'Nothing set',
    ]);
  });

  it('names items, and titles them for sentences', () => {
    expect(itemName('relation', relationOf())).toBe(
      'wh.orders (customer_id) → shop.customers (id)',
    );
    expect(itemName('navigation', overrideOf())).toBe('shop.orders.bill_address');
    expect(itemName('virtualEntity', virtualEntityOf())).toBe('reports.big_orders');
    expect(itemName('entitySettings', settingsOf())).toBe('shop.customers');
    expect(itemTitle('relation', relationOf(), 3)).toBe(
      'The relation wh.orders (customer_id) → shop.customers (id)',
    );
    expect(itemTitle('navigation', overrideOf(), 4)).toBe(
      'The override of shop.orders.bill_address',
    );
    expect(itemTitle('virtualEntity', virtualEntityOf(), 5)).toBe(
      'The virtual entity reports.big_orders',
    );
    expect(itemTitle('entitySettings', settingsOf(), 6)).toBe('The settings of shop.customers');
    // Not known: by its kind and id.
    expect(itemTitle('navigation', undefined, 9)).toBe('The navigation override 9');
    expect(itemLink('virtualEntity', 5)).toEqual(['/admin/overlay', 'virtual-entities', '5']);
  });

  it('finds the items of each kind', () => {
    const overlay = overlayOf({
      relations: [relationOf()],
      navigations: [overrideOf()],
      virtualEntities: [virtualEntityOf()],
      entitySettings: [settingsOf()],
    });
    expect(itemsOf(overlay, 'relation').map((item) => item.id)).toEqual([3]);
    expect(itemsOf(overlay, 'navigation').map((item) => item.id)).toEqual([4]);
    expect(itemsOf(overlay, 'virtualEntity').map((item) => item.id)).toEqual([5]);
    expect(itemsOf(overlay, 'entitySettings').map((item) => item.id)).toEqual([6]);
  });

  it('finds the worst of issues', () => {
    expect(worstOf([])).toBeNull();
    expect(worstOf([issueOf('a', 'info')])).toBe('info');
    expect(worstOf([issueOf('a', 'info'), issueOf('b', 'warning')])).toBe('warning');
    expect(worstOf([issueOf('a', 'warning'), issueOf('b', 'error')])).toBe('error');
  });

  it('trims what is given, leaving blanks out', () => {
    expect(namesOf([' id ', '', '  ', 'tenant'])).toEqual(['id', 'tenant']);
    expect(namesOf(['', ' '])).toBeNull();
    expect(namesOf([])).toBeNull();
    expect(trimmed('  a b ')).toBe('a b');
    expect(trimmed('   ')).toBeNull();
  });
});
