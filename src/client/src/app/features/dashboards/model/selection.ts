import { type Key, type Selection, keyText } from './definition';
import type { SelectionAction } from './widget-context';

/** The most slices a selection holds (the server's, signed in: `Dashboards:MaxSelectionKeys`). */
export const maxSelectionKeys = 50;

/**
 * The slices chosen after a click: one chosen alone (choosing the only one chosen again chooses none); added to
 * or taken from those chosen (Ctrl); left out, or let back in (Alt); none. A selection is of slices chosen or of
 * slices left out, never both, so it reads as a sentence ("Status: open, shipped"; "Region: not EMEA").
 */
export function selectionAfter(
  selection: Selection | null,
  action: SelectionAction,
  key: Key,
): Selection | null {
  const text = keyText(key);
  const has = (s: Selection) => s.keys.some((k) => keyText(k) === text);
  const without = (s: Selection) => s.keys.filter((k) => keyText(k) !== text);
  const kept = (s: Selection): Selection | null => (s.keys.length === 0 ? null : s);
  switch (action) {
    case 'clear':
      return null;
    case 'replace':
      return selection?.mode === 'include' && selection.keys.length === 1 && has(selection)
        ? null
        : { mode: 'include', keys: [[...key]] };
    case 'add':
      if (selection?.mode === 'include') {
        return has(selection)
          ? kept({ mode: 'include', keys: without(selection) })
          : { mode: 'include', keys: [...selection.keys, [...key]].slice(-maxSelectionKeys) };
      }
      if (selection?.mode === 'exclude' && has(selection)) {
        return kept({ mode: 'exclude', keys: without(selection) });
      }
      return { mode: 'include', keys: [[...key]] };
    case 'exclude':
      if (selection?.mode === 'exclude') {
        return has(selection)
          ? kept({ mode: 'exclude', keys: without(selection) })
          : { mode: 'exclude', keys: [...selection.keys, [...key]].slice(-maxSelectionKeys) };
      }
      if (selection?.mode === 'include' && has(selection)) {
        return kept({ mode: 'include', keys: without(selection) });
      }
      return { mode: 'exclude', keys: [[...key]] };
  }
}
