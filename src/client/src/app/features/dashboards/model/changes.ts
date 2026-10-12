import { sameJson } from '../../../core/api/same-json';
import { configOf, type Definition, type Widget } from './definition';
import { onTab, tabOf, tabsOf } from './tabs';

/** A widget as a change names it: a heading by its text, else its title, else its kind and id. */
function nameOf(widget: Widget): string {
  const config = configOf(widget);
  if (config.kind === 'heading' && config.text.trim()) {
    return `the heading "${config.text.trim()}"`;
  }
  return widget.title?.trim() || `the ${widget.config.kind ?? 'widget'} ${widget.id}`;
}

/**
 * What changed from one definition to another, briefly, as publishing says it: widgets added, removed, changed or
 * moved to another tab (by name), and the sources, links, filters, layout, refresh, public settings, palette and
 * tabs, when they changed. A widget's tab is where it is, not what it is: split into tabs, the widgets aren't
 * changed, the tabs are.
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
    .filter((w) => was.has(w.id) && !sameJson(onTab(was.get(w.id)!, null), onTab(w, null)))
    .map(nameOf);
  // Moved from one tab to another (both copies having tabs), by the tab it is on now.
  const tabbed = tabsOf(before).length > 0 && tabsOf(after).length > 0;
  const moved = after.widgets.filter(
    (w) => tabbed && was.has(w.id) && tabOf(before, was.get(w.id)!) !== tabOf(after, w),
  );
  if (added.length > 0) {
    changes.push(`Added ${added.join(', ')}.`);
  }
  if (removed.length > 0) {
    changes.push(`Removed ${removed.join(', ')}.`);
  }
  if (changed.length > 0) {
    changes.push(`Changed ${changed.join(', ')}.`);
  }
  for (const widget of moved) {
    const tab = tabsOf(after).find((t) => t.id === tabOf(after, widget));
    changes.push(`Moved ${nameOf(widget)} to ${tab?.label.trim() || 'another tab'}.`);
  }
  const parts: [keyof Definition, string][] = [
    ['sources', 'its sources'],
    ['links', 'the links between them'],
    ['filters', 'its filters'],
    ['layout', 'its layout'],
    ['refresh', 'when it refreshes'],
    ['public', 'what its public link shows'],
    ['palette', "its charts' palette"],
    ['tabs', 'its tabs'],
  ];
  const others = parts
    .filter(([key]) => !sameJson(before[key], after[key]))
    .map(([, said]) => said);
  if (others.length > 0) {
    changes.push(`Changed ${others.join(', ')}.`);
  }
  return changes.length > 0 ? changes : ['Nothing: it is as published.'];
}
