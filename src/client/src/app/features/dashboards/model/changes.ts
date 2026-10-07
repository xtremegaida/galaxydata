import { sameJson } from '../../../core/api/same-json';
import type { Definition, Widget } from './definition';

/** A widget as a change names it: its title, else its kind and id. */
function nameOf(widget: Widget): string {
  return widget.title?.trim() || `the ${widget.config.kind ?? 'widget'} ${widget.id}`;
}

/**
 * What changed from one definition to another, briefly, as publishing says it: widgets added, removed or changed
 * (by name), and the sources, links, filters, layout, refresh and public settings, when they changed.
 */
export function changesBetween(before: Definition | null, after: Definition): string[] {
  if (!before) {
    return ['Its first revision: everything is new to its viewers.'];
  }
  const changes: string[] = [];
  const was = new Map(before.widgets.map((w) => [w.id, w]));
  const is = new Map(after.widgets.map((w) => [w.id, w]));
  const added = after.widgets.filter((w) => !was.has(w.id)).map(nameOf);
  const removed = before.widgets.filter((w) => !is.has(w.id)).map(nameOf);
  const changed = after.widgets
    .filter((w) => was.has(w.id) && !sameJson(was.get(w.id), w))
    .map(nameOf);
  if (added.length > 0) {
    changes.push(`Added ${added.join(', ')}.`);
  }
  if (removed.length > 0) {
    changes.push(`Removed ${removed.join(', ')}.`);
  }
  if (changed.length > 0) {
    changes.push(`Changed ${changed.join(', ')}.`);
  }
  const parts: [keyof Definition, string][] = [
    ['sources', 'its sources'],
    ['links', 'the links between them'],
    ['filters', 'its filters'],
    ['layout', 'its layout'],
    ['refresh', 'when it refreshes'],
    ['public', 'what its public link shows'],
  ];
  const others = parts
    .filter(([key]) => !sameJson(before[key], after[key]))
    .map(([, said]) => said);
  if (others.length > 0) {
    changes.push(`Changed ${others.join(', ')}.`);
  }
  return changes.length > 0 ? changes : ['Nothing: it is as published.'];
}
