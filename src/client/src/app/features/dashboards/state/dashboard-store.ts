import { Injectable, computed, signal } from '@angular/core';
import {
  type ConditionValue,
  type DashboardIssue,
  type DashboardState,
  type Definition,
  type Key,
  type Selection,
  configOf,
  isData,
  reachedFrom,
} from '../model/definition';
import { selectionAfter } from '../model/selection';
import type { SelectionAction } from '../model/widget-context';
import type { DashboardHost } from './dashboard-host';
import { sameJson } from '../../../core/api/same-json';

export { sameJson } from '../../../core/api/same-json';
export { reachedFrom } from '../model/definition';

/** A blank dashboard: nothing on it. */
export function blankDefinition(): Definition {
  return {
    schema: 1,
    layout: {
      rowHeight: 48,
      gap: 12,
      breakpoints: [
        { id: 'narrow', label: 'Narrow', minWidth: 0, columns: 1 },
        { id: 'medium', label: 'Medium', minWidth: 720, columns: 6 },
        { id: 'wide', label: 'Wide', minWidth: 1200, columns: 12 },
      ],
      items: {},
      overrides: {},
    },
    sources: [],
    links: [],
    filters: [],
    widgets: [],
    refresh: { mode: 'manual', seconds: null },
    public: { showData: true },
  };
}

/**
 * A dashboard as a page shows it: its definition, the filters' values the viewer set and the slices chosen in its
 * widgets, which ask for their rows again when what reaches them changes (and only then), or when the dashboard is
 * refreshed. Each page provides its own.
 */
@Injectable()
export class DashboardStore {
  readonly definition = signal<Definition>(blankDefinition());

  /** Where widgets get their rows; the page sets it. */
  readonly host = signal<DashboardHost | null>(null);

  /** The values of the filters the viewer may change, by id (null: cleared); the others are the definition's. */
  readonly filters = signal<Readonly<Record<string, ConditionValue | null>>>({});

  /** The slices chosen, by widget. */
  readonly selections = signal<Readonly<Record<string, Selection>>>({});

  /** Goes up each time the dashboard is refreshed: widgets read their rows again, fresh. */
  readonly refreshes = signal(0);

  /** The widgets reading their rows now. */
  readonly loading = signal<ReadonlySet<string>>(new Set());

  /** When the rows shown were read, the earliest of the widgets'. */
  readonly refreshedAt = signal<ReadonlyMap<string, string>>(new Map());

  readonly anyLoading = computed(() => this.loading().size > 0);

  /** The widgets whose rows couldn't be read the last time they asked. */
  readonly failing = signal<ReadonlySet<string>>(new Set());

  /** What the server said is wrong with each widget (or may be), the last time it asked for its rows. */
  readonly issues = signal<ReadonlyMap<string, readonly DashboardIssue[]>>(new Map());

  readonly oldestRefresh = computed(() => {
    const times = [...this.refreshedAt().values()].sort();
    return times[0] ?? null;
  });

  refresh(): void {
    this.refreshes.update((n) => n + 1);
  }

  setLoading(widget: string, loading: boolean): void {
    const now = this.loading();
    if (now.has(widget) === loading) {
      return;
    }
    const next = new Set(now);
    if (loading) {
      next.add(widget);
    } else {
      next.delete(widget);
    }
    this.loading.set(next);
  }

  setFailing(widget: string, failing: boolean): void {
    const now = this.failing();
    if (now.has(widget) !== failing) {
      const next = new Set(now);
      if (failing) {
        next.add(widget);
      } else {
        next.delete(widget);
      }
      this.failing.set(next);
    }
  }

  setIssues(widget: string, issues: readonly DashboardIssue[]): void {
    const now = this.issues().get(widget) ?? [];
    if (sameJson(now, issues)) {
      return;
    }
    const next = new Map(this.issues());
    if (issues.length === 0) {
      next.delete(widget);
    } else {
      next.set(widget, issues);
    }
    this.issues.set(next);
  }

  setRefreshedAt(widget: string, at: string | null): void {
    if ((this.refreshedAt().get(widget) ?? null) === at) {
      return;
    }
    const next = new Map(this.refreshedAt());
    if (at === null) {
      next.delete(widget);
    } else {
      next.set(widget, at);
    }
    this.refreshedAt.set(next);
  }

  /** Chooses (or leaves out, or lets in again) a widget's slice. */
  choose(widget: string, key: Key, action: SelectionAction): void {
    const next = selectionAfter(this.selections()[widget] ?? null, action, key);
    this.setSelection(widget, next);
  }

  setSelection(widget: string, selection: Selection | null): void {
    const all = { ...this.selections() };
    if (selection) {
      all[widget] = selection;
    } else {
      delete all[widget];
    }
    this.selections.set(all);
  }

  clearSelections(): void {
    this.selections.set({});
  }

  setFilter(filter: string, value: ConditionValue | null | undefined): void {
    const all = { ...this.filters() };
    if (value === undefined) {
      delete all[filter];
    } else {
      all[filter] = value;
    }
    this.filters.set(all);
  }

  /**
   * What reaches a widget, as its request gives it: the values set of the filters that apply to it (those of
   * sources linked to its own, it isn't excepted from), and the slices chosen in the widgets it listens to (never
   * its own) on sources linked to its own. Anything else changing doesn't change it.
   */
  inputsOf(widget: string): DashboardState {
    const definition = this.definition();
    const found = definition.widgets.find((w) => w.id === widget);
    if (!found) {
      return {};
    }
    const config = configOf(found);
    if (!isData(config)) {
      return {};
    }
    // A public dashboard's definition has no sources (they are the server's): what reaches which widget is the
    // server's to say, and it is given everything set, as it takes only what reaches.
    const known = definition.sources.length > 0;
    const reached = reachedFrom(definition, config.source);
    const set = this.filters();
    const filters: Record<string, ConditionValue | null> = {};
    for (const filter of definition.filters) {
      if (
        filter.editable &&
        filter.id in set &&
        !filter.except.includes(widget) &&
        (!known || reached.has(filter.field.source))
      ) {
        filters[filter.id] = set[filter.id];
      }
    }
    const selections: Record<string, Selection> = {};
    for (const [emitter, selection] of Object.entries(this.selections())) {
      const other = definition.widgets.find((w) => w.id === emitter);
      if (emitter === widget || !other || selection.keys.length === 0) {
        continue;
      }
      const emitting = configOf(other);
      if (!isData(emitting) || !emitting.emits || (known && !reached.has(emitting.source))) {
        continue;
      }
      const listens = config.listens;
      if (
        listens.mode === 'none' ||
        (listens.mode === 'chosen' && !listens.widgets.includes(emitter))
      ) {
        continue;
      }
      selections[emitter] = selection;
    }
    // A null value is a filter cleared, which the server takes as no condition (its schema doesn't say values may be null).
    return { filters: filters as DashboardState['filters'], selections };
  }
}
