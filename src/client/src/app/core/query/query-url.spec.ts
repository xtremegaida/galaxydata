import { DefaultUrlSerializer } from '@angular/router';
import fc from 'fast-check';
import {
  type QueryAddress,
  newQuery,
  queryUrlTree,
  readQueryAddress,
  sameValues,
} from './query-url';

const serializer = new DefaultUrlSerializer();

/** An address as the browser holds it. */
function addressOf(address: QueryAddress): string {
  return serializer.serialize(queryUrlTree(address));
}

/** An address read as the router gives it to the page: its path's id, and its fragment. */
function read(url: string) {
  const tree = serializer.parse(url);
  const segments = tree.root.children['primary']?.segments ?? [];
  return readQueryAddress(segments[1]?.path ?? null, tree.fragment);
}

describe('query addresses', () => {
  it('are the path alone for a new query, and a saved one as saved', () => {
    expect(addressOf(newQuery)).toBe('/query');
    expect(addressOf({ ...newQuery, id: 7 })).toBe('/query/7');
    expect(read('/query')).toEqual({ address: newQuery, problem: null });
    expect(read('/query/7')).toEqual({ address: { ...newQuery, id: 7 }, problem: null });
  });

  it('hold the text, the values and whether the rows are shown in their fragment', () => {
    const address: QueryAddress = {
      id: 7,
      text: "shop.orders.where(total > $min and status == 'open') # 50% off",
      values: { min: '50', since: null },
      run: true,
    };
    const url = addressOf(address);
    expect(url.startsWith('/query/7#')).toBe(true);
    expect(decodeURIComponent(url.slice(url.indexOf('#') + 1))).toBe(
      JSON.stringify({ text: address.text, values: address.values, run: true }),
    );
    expect(read(url)).toEqual({ address, problem: null });
    expect(addressOf({ ...newQuery, run: true })).toBe('/query#%7B%22run%22:true%7D');
  });

  it("leave out what can't be read, and say so", () => {
    const unreadable = "Part of the address couldn't be read, and was left out.";
    expect(readQueryAddress(null, 'not json')).toEqual({ address: newQuery, problem: unreadable });
    expect(readQueryAddress('3', '[1]')).toEqual({
      address: { ...newQuery, id: 3 },
      problem: unreadable,
    });
    expect(
      readQueryAddress(null, JSON.stringify({ text: 1, values: { a: 'x' }, run: 'yes' })),
    ).toEqual({ address: { ...newQuery, values: { a: 'x' } }, problem: unreadable });
    expect(readQueryAddress(null, JSON.stringify({ text: 'x', values: { a: 1 } }))).toEqual({
      address: { ...newQuery, text: 'x' },
      problem: unreadable,
    });
    expect(readQueryAddress(null, JSON.stringify({ text: 'x', values: ['a'] })).problem).toBe(
      unreadable,
    );
    expect(readQueryAddress(null, JSON.stringify({ run: 'yes' }))).toEqual({
      address: newQuery,
      problem: unreadable,
    });
    // Not a saved query's number.
    expect(readQueryAddress('1e2', null).address.id).toBeNull();
    expect(readQueryAddress('abc', null).address.id).toBeNull();
  });

  it('keep values of any name as their own', () => {
    const { address } = readQueryAddress(null, '{"values":{"__proto__":"x","a":null}}');
    expect(Object.getPrototypeOf(address.values)).toBe(Object.prototype);
    expect(Object.keys(address.values ?? {})).toEqual(['__proto__', 'a']);
    expect(read(addressOf(address)).address).toEqual(address);
  });

  it('tell values apart whatever the order of their names', () => {
    expect(sameValues({ a: '1', b: null }, { b: null, a: '1' })).toBe(true);
    expect(sameValues({ a: '1' }, { a: '1', b: null })).toBe(false);
    expect(sameValues({ a: '1' }, { a: '2' })).toBe(false);
    expect(sameValues({ a: null }, { b: null })).toBe(false);
    expect(sameValues(null, null)).toBe(true);
    expect(sameValues({}, null)).toBe(false);
  });

  it('read what they write, whatever the text and values', () => {
    fc.assert(
      fc.property(addresses(), (address) => {
        expect(read(addressOf(address))).toEqual({ address, problem: null });
      }),
      { numRuns: 400 },
    );
  });
});

/** Text with JSON's and addresses' marks often in it, and any characters (lone surrogates too). */
function text(): fc.Arbitrary<string> {
  const marks = fc.constantFrom(
    '"',
    '\\',
    '#',
    '%',
    '&',
    '=',
    '?',
    '/',
    ' ',
    '\n',
    '\r',
    '{',
    '}',
    '$',
    '+',
  );
  return fc.oneof(
    fc.string({ unit: 'binary', maxLength: 16 }),
    fc.string({ unit: fc.oneof(marks, fc.constantFrom('a', 'é', '1')), maxLength: 12 }),
  );
}

function addresses(): fc.Arbitrary<QueryAddress> {
  return fc.record({
    id: fc.option(fc.nat({ max: 999_999_999 }), { nil: null }),
    text: fc.option(text(), { nil: null }),
    values: fc.option(fc.dictionary(text(), fc.option(text(), { nil: null }), { maxKeys: 4 }), {
      nil: null,
    }),
    run: fc.boolean(),
  });
}
