import { LiveAnnouncer } from '@angular/cdk/a11y';
import { Injectable, computed, inject, signal } from '@angular/core';
import type { Schema } from '../../../core/api/api-client';
import { sameJson } from '../../../core/api/same-json';
import { designedOf } from '../model/definition-ops';
import {
  type DashboardIssue,
  type Definition,
  configOf,
  isChart,
  isData,
  reachedFrom,
} from '../model/definition';
import { WIDGET_KINDS, kindOf } from '../model/widget-registry';
import { DashboardStore, blankDefinition } from '../state/dashboard-store';
import { History } from './history';
import { missingOf } from '../model/complete';
import { PaletteLibrary } from '../palettes/palette-library';

export type DashboardDto = Schema<'DashboardDto'>;

/** What the editor has saved: the dashboard's id (none while new), version, name, description and working copy. */
export interface Saved {
  readonly id: number | null;
  readonly version: number | null;
  readonly name: string;
  readonly description: string | null;
  readonly definition: Definition;
}

/** Where an issue is in the editor, to go to it: a widget's settings, or a panel. */
export type IssuePlace =
  { readonly widget: string } | { readonly panel: 'sources' | 'filters' | 'layout' | 'refresh' };

/** An issue as the editor lists it: the server's (of the dashboard, or a widget's preview) or its own. */
export interface EditorIssue {
  readonly severity: DashboardIssue['severity'];
  readonly message: string;
  readonly place: IssuePlace | null;
  readonly field: string | null;
}

/**
 * A dashboard being edited: what was saved, the draft (every edit a step of its history, undone and redone by
 * name), the widget chosen, the breakpoint shown, and what is wrong (the server's, as it last said, and the
 * editor's own). The editor page provides it.
 */
@Injectable()
export class EditorStore {
  private readonly announcer = inject(LiveAnnouncer);
  private readonly kinds = inject(WIDGET_KINDS);
  /** The canvas's: what its widgets' previews last said. */
  private readonly dashboard = inject(DashboardStore);
  /** The palettes the draft names, as read: one that isn't there is an issue. */
  private readonly palettes = inject(PaletteLibrary);
  readonly history = new History<Definition>();

  readonly saved = signal<Saved>({
    id: null,
    version: null,
    name: '',
    description: null,
    definition: blankDefinition(),
  });
  readonly draft = signal<Definition>(blankDefinition());
  readonly name = signal('');
  readonly description = signal<string | null>(null);

  /** Whether clicking slices on the canvas chooses them, to try the dashboard out. */
  readonly interacting = signal(false);

  /** The widget whose settings are shown, if any. */
  readonly selected = signal<string | null>(null);
  /** The breakpoint the canvas shows: the designed one unless another is chosen. */
  readonly shown = signal<string | null>(null);
  readonly breakpoint = computed(() => {
    const breakpoints = this.draft().layout.breakpoints;
    return breakpoints.find((b) => b.id === this.shown()) ?? designedOf(this.draft());
  });
  readonly designed = computed(() => this.breakpoint().id === designedOf(this.draft()).id);
  /** Whether the breakpoint shown is laid out (designed, or customized): widgets can be moved there. */
  readonly laidOut = computed(
    () => this.designed() || this.breakpoint().id in this.draft().layout.overrides,
  );

  /** The issues of the dashboard the server last gave (on reading or saving it). */
  readonly serverIssues = signal<readonly DashboardIssue[]>([]);

  /** What the server refused of the last save, by field (till the next). */
  readonly saveIssues = signal<readonly EditorIssue[]>([]);

  readonly dirty = computed(() => {
    const saved = this.saved();
    return (
      !sameJson(this.draft(), saved.definition) ||
      this.name().trim() !== saved.name ||
      (this.description()?.trim() || null) !== saved.description
    );
  });

  /** The editor's own issues: what the server can't know before it is sent. */
  readonly ownIssues = computed<EditorIssue[]>(() => {
    const definition = this.draft();
    const issues: EditorIssue[] = [];
    for (const widget of definition.widgets) {
      const config = configOf(widget);
      const place = { widget: widget.id };
      if (!kindOf(this.kinds, config)) {
        issues.push({
          severity: 'error',
          message: `This page doesn't know ${config.kind} widgets.`,
          place,
          field: null,
        });
        continue;
      }
      if (!isData(config)) {
        if (/!\[[^\]]*\]\((?!\/(?!\/)|data:image\/)/i.test(config.markdown)) {
          issues.push({
            severity: 'warning',
            message: `${widget.title ?? 'A text widget'} shows images of other sites, which the page's security policy doesn't load: they show as links.`,
            place,
            field: 'config.markdown',
          });
        }
        continue;
      }
      if (!definition.sources.some((s) => s.id === config.source)) {
        issues.push({
          severity: 'error',
          message: `${this.nameOf(widget.id)} has no source.`,
          place,
          field: 'config.source',
        });
        continue;
      }
      const missing = missingOf(config, definition.sources);
      if (missing) {
        issues.push({
          severity: 'warning',
          message: `${this.nameOf(widget.id)}: ${missing}`,
          place,
          field: null,
        });
      }
      if (config.listens.mode === 'chosen') {
        const reached = reachedFrom(definition, config.source);
        for (const other of config.listens.widgets) {
          const emitter = definition.widgets.find((w) => w.id === other);
          const emitting = emitter ? configOf(emitter) : null;
          if (emitting && isData(emitting) && !reached.has(emitting.source)) {
            issues.push({
              severity: 'warning',
              message: `${this.nameOf(widget.id)} listens to ${this.nameOf(other)}, whose source isn't linked to its own: what is chosen there doesn't reach it. Link the sources.`,
              place: { panel: 'sources' },
              field: null,
            });
          }
        }
      }
    }
    const read = this.palettes.read();
    if (definition.palette != null && read.get(definition.palette) === null) {
      issues.push({
        severity: 'warning',
        message:
          "Its charts' palette is gone: they are drawn with the built-in colours. Choose another.",
        place: { panel: 'refresh' },
        field: 'palette',
      });
    }
    for (const widget of definition.widgets) {
      const config = configOf(widget);
      if (isChart(config) && config.palette != null && read.get(config.palette) === null) {
        issues.push({
          severity: 'warning',
          message: `${this.nameOf(widget.id)}: its palette is gone: it is drawn with the dashboard's.`,
          place: { widget: widget.id },
          field: 'config.palette',
        });
      }
    }
    for (const filter of definition.filters.filter((f) => !f.field.column)) {
      issues.push({
        severity: 'warning',
        message: `The filter ${filter.label || filter.id} has no field: it reaches nothing yet.`,
        place: { panel: 'filters' },
        field: null,
      });
    }
    return issues;
  });

  /** Every issue the editor knows of: a save's refusals, its own, the server's, each preview's; errors first. */
  readonly issues = computed<EditorIssue[]>(() => {
    // Whether palettes are there the editor knows as they are read, better than the server did when it last said.
    const server = this.serverIssues()
      .filter((issue) => !issue.field?.endsWith('palette'))
      .map((issue) => ({
        severity: issue.severity,
        message: issue.widget ? `${this.nameOf(issue.widget)}: ${issue.message}` : issue.message,
        place: issue.widget ? { widget: issue.widget } : null,
        field: issue.field,
      }));
    const previews = [...this.dashboard.issues()].flatMap(([widget, issues]) =>
      issues.map((issue) => ({
        severity: issue.severity,
        message: `${this.nameOf(widget)}: ${issue.message}`,
        place: { widget },
        field: issue.field,
      })),
    );
    // A widget's preview says what the server would of it now: its say on save is the older.
    const previewed = new Set(this.dashboard.issues().keys());
    const kept = server.filter(
      (i) => !(i.place && 'widget' in i.place && previewed.has(i.place.widget)),
    );
    return [...this.saveIssues(), ...this.ownIssues(), ...kept, ...previews].sort(
      (a, b) => (a.severity === 'error' ? 0 : 1) - (b.severity === 'error' ? 0 : 1),
    );
  });

  /** A widget as the editor names it: its title, or its kind and id. */
  nameOf(id: string): string {
    const widget = this.draft().widgets.find((w) => w.id === id);
    if (!widget) {
      return id;
    }
    const config = configOf(widget);
    if (widget.title?.trim()) {
      return widget.title.trim();
    }
    // A text widget is named by its first line (a heading, usually).
    const first =
      config.kind === 'text'
        ? config.markdown
            .split('\n')
            .map((line) =>
              line
                .replace(/^[#>*\s-]+/, '')
                .replace(/[*_`[\]]/g, '')
                .trim(),
            )
            .find(Boolean)
        : undefined;
    const kind = kindOf(this.kinds, config)?.label ?? 'Widget';
    return first
      ? `${kind}: ${first.length > 40 ? first.slice(0, 40) + '…' : first}`
      : `${kind} ${id}`;
  }

  /** Starts editing: a dashboard read (its working copy), or a new one. */
  load(dashboard: DashboardDto | null): void {
    const definition = dashboard?.working ?? dashboard?.published ?? blankDefinition();
    this.saved.set({
      id: dashboard?.id ?? null,
      version: dashboard?.version ?? null,
      name: dashboard?.name ?? '',
      description: dashboard?.description ?? null,
      definition,
    });
    this.draft.set(definition);
    this.name.set(dashboard?.name ?? '');
    this.description.set(dashboard?.description ?? null);
    this.serverIssues.set(dashboard?.issues ?? []);
    this.history.clear();
    if (this.selected() && !definition.widgets.some((w) => w.id === this.selected())) {
      this.selected.set(null);
    }
  }

  /** What was saved: the draft as it was sent is the dashboard's working copy now. */
  savedAs(dashboard: DashboardDto, sent: Definition): void {
    this.saved.set({
      id: dashboard.id,
      version: dashboard.version,
      name: dashboard.name,
      description: dashboard.description,
      definition: sent,
    });
    this.serverIssues.set(dashboard.issues);
  }

  /**
   * An edit of the draft, a step of its history (`label` is what undo says). Edits with the same `key` soon after
   * one another are one step: typing in a field, a widget dragged. An edit that changes nothing is none.
   */
  apply(
    label: string,
    edit: (definition: Definition) => Definition,
    key: string | null = null,
  ): void {
    const before = this.draft();
    const after = edit(before);
    if (after === before || sameJson(after, before)) {
      return;
    }
    this.history.record(label, before, key);
    this.draft.set(after);
    this.keepSelection();
  }

  undo(): void {
    const undone = this.history.undo(this.draft());
    if (undone) {
      this.draft.set(undone.value);
      this.keepSelection();
      this.announcer.announce(`Undid: ${undone.label}`);
    }
  }

  redo(): void {
    const redone = this.history.redo(this.draft());
    if (redone) {
      this.draft.set(redone.value);
      this.keepSelection();
      this.announcer.announce(`Redid: ${redone.label}`);
    }
  }

  /** The widget chosen, if it is still there. */
  private keepSelection(): void {
    const selected = this.selected();
    if (selected && !this.draft().widgets.some((w) => w.id === selected)) {
      this.selected.set(null);
    }
    const shown = this.shown();
    if (shown && !this.draft().layout.breakpoints.some((b) => b.id === shown)) {
      this.shown.set(null);
    }
  }
}
