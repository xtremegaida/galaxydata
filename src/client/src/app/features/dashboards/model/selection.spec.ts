import fc from 'fast-check';
import type { Key, Selection } from './definition';
import { keyText } from './definition';
import { maxSelectionKeys, selectionAfter } from './selection';
import type { SelectionAction } from './widget-context';

const keys = fc.array(
  fc.oneof(
    fc.constantFrom('open', 'shipped', 'cancelled'),
    fc.constant(null),
    fc.integer({ min: 1, max: 3 }),
  ),
  { minLength: 1, maxLength: 2 },
);
const actions = fc.constantFrom<SelectionAction>('replace', 'add', 'exclude', 'clear');
const runs = { numRuns: 400 };

/** The selection after clicks, from none. */
function after(clicks: [SelectionAction, Key][]): Selection | null {
  return clicks.reduce<Selection | null>(
    (selection, [action, key]) => selectionAfter(selection, action, key),
    null,
  );
}

describe('choosing slices', () => {
  it('is a selection of slices chosen or of slices left out, never empty, each slice once', () => {
    fc.assert(
      fc.property(fc.array(fc.tuple(actions, keys), { maxLength: 30 }), (clicks) => {
        const selection = after(clicks);
        if (selection) {
          expect(selection.keys.length).toBeGreaterThan(0);
          expect(selection.keys.length).toBeLessThanOrEqual(maxSelectionKeys);
          expect(new Set(selection.keys.map((k) => keyText(k))).size).toBe(selection.keys.length);
        }
      }),
      runs,
    );
  });

  it('gives back what was chosen when a slice is added and taken again', () => {
    fc.assert(
      fc.property(fc.array(fc.tuple(actions, keys), { maxLength: 10 }), keys, (clicks, key) => {
        const before = after(clicks);
        fc.pre(before?.mode === 'include' && !before.keys.some((k) => keyText(k) === keyText(key)));
        expect(selectionAfter(selectionAfter(before, 'add', key), 'add', key)).toEqual(before);
      }),
      runs,
    );
  });

  it('chooses none when cleared, and when the only slice chosen is chosen again ', () => {
    fc.assert(
      fc.property(fc.array(fc.tuple(actions, keys), { maxLength: 10 }), keys, (clicks, key) => {
        expect(selectionAfter(after(clicks), 'clear', key)).toBeNull();
        const before = after(clicks);
        const onlyThis =
          before?.mode === 'include' &&
          before.keys.length === 1 &&
          keyText(before.keys[0]) === keyText(key);
        expect(selectionAfter(before, 'replace', key)).toEqual(
          onlyThis ? null : { mode: 'include', keys: [key] },
        );
      }),
      runs,
    );
  });

  it('reads as people click', () => {
    expect(after([['replace', ['open']]])).toEqual({ mode: 'include', keys: [['open']] });
    expect(
      after([
        ['replace', ['open']],
        ['add', ['shipped']],
      ]),
    ).toEqual({ mode: 'include', keys: [['open'], ['shipped']] });
    expect(
      after([
        ['replace', ['open']],
        ['replace', ['shipped']],
      ]),
    ).toEqual({ mode: 'include', keys: [['shipped']] });
    expect(
      after([
        ['exclude', ['open']],
        ['exclude', [null]],
      ]),
    ).toEqual({ mode: 'exclude', keys: [['open'], [null]] });
    expect(
      after([
        ['exclude', ['open']],
        ['add', ['open']],
      ]),
    ).toBeNull();
    expect(
      after([
        ['replace', ['open']],
        ['exclude', ['shipped']],
      ]),
    ).toEqual({ mode: 'exclude', keys: [['shipped']] });
  });
});
