import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAnchor, MatButton, MatIconButton } from '@angular/material/button';
import { MatButtonToggle, MatButtonToggleGroup } from '@angular/material/button-toggle';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { MatTab, MatTabGroup } from '@angular/material/tabs';
import { MatTooltip } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, firstValueFrom } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { sameJson } from '../../../core/api/same-json';
import { type Problem, ProblemCode, problemMessage, problemOf } from '../../../core/api/problem';
import { PageTitle } from '../../../core/page-titles';
import { Message } from '../../../core/ui/message';
import { type HasUnsavedChanges, warnBeforeUnload } from '../../../core/ui/unsaved-changes';
import { chartPalette } from '../charts/series-colors';
import { type Definition, palettesOf } from '../model/definition';
import { addWidget } from '../model/definition-ops';
import { PaletteLibrary } from '../palettes/palette-library';
import { WIDGET_KINDS, type WidgetKind } from '../model/widget-registry';
import { InlineHost } from '../state/dashboard-host';
import { DashboardStore } from '../state/dashboard-store';
import { EditorCanvas } from './editor-canvas';
import { type DashboardDto, type EditorIssue, EditorStore, type IssuePlace } from './editor-store';
import { EntityCatalog } from './entity-catalog';
import { FiltersPanel } from './panels/filters-panel';
import { IssuesPanel, LayoutPanel, RefreshPanel } from './panels/layout-panels';
import { SourcesPanel } from './panels/sources-panel';
import { WidgetPanel } from './panels/widget-panel';

/** The side panel's tabs, in order. */
const tabs = ['widget', 'sources', 'filters', 'layout', 'refresh', 'issues'] as const;

/** A save that couldn't be: what to say, and whether it was someone else's change (to read it again, or copy). */
interface SaveProblem {
  readonly text: string;
  readonly conflict: boolean;
}

/** Where a refusal's field is in the editor: `definition.widgets[2].config…` is the widget sent third. */
function placeOf(field: string, sent: Definition): IssuePlace | null {
  const widget = /^definition\.widgets\[(\d+)\]/.exec(field);
  if (widget) {
    const id = sent.widgets[Number(widget[1])]?.id;
    return id ? { widget: id } : null;
  }
  if (/^definition\.(sources|links)/.test(field)) {
    return { panel: 'sources' };
  }
  if (field.startsWith('definition.filters')) {
    return { panel: 'filters' };
  }
  if (field.startsWith('definition.layout')) {
    return { panel: 'layout' };
  }
  if (field.startsWith('definition.refresh') || field === 'definition.palette') {
    return { panel: 'refresh' };
  }
  return null;
}

/**
 * The dashboard editor: a canvas of the dashboard as viewers see it (at each breakpoint), a panel to add widgets,
 * and panels for the widget chosen, the sources and their links, the filters, the layout, the refresh and the
 * issues. Every edit is undone and redone by name (Ctrl+Z, Ctrl+Shift+Z or Ctrl+Y outside fields); Ctrl+S saves
 * the working copy (a new dashboard's first save gives it its address). Each widget's preview is its slice's, asked
 * for as edits pause. Leaving with changes not saved asks first.
 */
@Component({
  selector: 'gd-dashboard-editor',
  imports: [
    EditorCanvas,
    FiltersPanel,
    IssuesPanel,
    LayoutPanel,
    MatAnchor,
    MatButton,
    MatButtonToggle,
    MatButtonToggleGroup,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatProgressBar,
    MatSlideToggle,
    MatTab,
    MatTabGroup,
    MatTooltip,
    Message,
    RefreshPanel,
    RouterLink,
    SourcesPanel,
    WidgetPanel,
  ],
  providers: [DashboardStore, EditorStore, EntityCatalog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'keys($event)' },
  template: `
    <div class="editor">
      <h1 class="hidden">Editing {{ store.name() || 'a new dashboard' }}</h1>
      <header class="top">
        <mat-form-field class="name" subscriptSizing="dynamic">
          <mat-label>Name</mat-label>
          <input
            #name
            matInput
            required
            [attr.aria-invalid]="nameProblem() ? true : null"
            [value]="store.name()"
            (input)="store.name.set(name.value); nameProblem.set(null)"
            autocomplete="off"
          />
          @if (nameProblem(); as problem) {
            <!-- A hint, not an error: Material shows errors of form controls only. -->
            <mat-hint class="problem" role="alert">{{ problem }}</mat-hint>
          }
        </mat-form-field>
        @if (store.dirty()) {
          <span class="edited">Not saved</span>
        }
        <button
          matIconButton
          type="button"
          [disabled]="!store.history.undoLabel()"
          [matTooltip]="
            store.history.undoLabel() ? 'Undo: ' + store.history.undoLabel() : 'Nothing to undo'
          "
          aria-label="Undo"
          (click)="store.undo()"
        >
          <mat-icon>undo</mat-icon>
        </button>
        <button
          matIconButton
          type="button"
          [disabled]="!store.history.redoLabel()"
          [matTooltip]="
            store.history.redoLabel() ? 'Redo: ' + store.history.redoLabel() : 'Nothing to redo'
          "
          aria-label="Redo"
          (click)="store.redo()"
        >
          <mat-icon>redo</mat-icon>
        </button>
        <mat-button-toggle-group
          class="widths"
          aria-label="The width shown"
          hideSingleSelectionIndicator
          [value]="store.breakpoint().id"
          (change)="store.shown.set($event.value)"
        >
          @for (breakpoint of store.draft().layout.breakpoints; track breakpoint.id) {
            <mat-button-toggle [value]="breakpoint.id">{{ breakpoint.label }}</mat-button-toggle>
          }
        </mat-button-toggle-group>
        <mat-slide-toggle
          [checked]="store.interacting()"
          (change)="store.interacting.set($event.checked)"
        >
          Try it
        </mat-slide-toggle>
        <span class="spacer"></span>
        @if (store.saved().id; as id) {
          <a matButton [routerLink]="['/dashboards', id]" [queryParams]="{ copy: 'working' }">
            <mat-icon>visibility</mat-icon>
            View
          </a>
        }
        <button
          matButton="filled"
          type="button"
          [disabled]="saving() || reading()"
          (click)="save()"
        >
          <mat-icon>save</mat-icon>
          Save
        </button>
      </header>
      @if (saving() || reading()) {
        <mat-progress-bar
          mode="indeterminate"
          [attr.aria-label]="saving() ? 'Saving' : 'Reading the dashboard'"
        />
      }
      <div role="alert">
        @if (loadProblem(); as problem) {
          <gd-message kind="problem">{{ problem }}</gd-message>
        }
        @if (saveProblem(); as problem) {
          <gd-message kind="problem">
            {{ problem.text }}
            @if (problem.conflict) {
              <ng-container ngProjectAs="[gdMessageAction]">
                <button matButton type="button" (click)="readAgain()">Read it again</button>
                <button matButton type="button" (click)="saveCopy()">Save as a copy</button>
              </ng-container>
            }
          </gd-message>
        }
      </div>
      @if (ready()) {
        <div class="body">
          <aside class="add-widgets" aria-label="Add a widget">
            @for (kind of kinds; track kind.kind) {
              <button matButton type="button" class="add" (click)="add(kind)">
                <mat-icon>{{ kind.icon }}</mat-icon>
                {{ kind.label }}
              </button>
            }
          </aside>
          <gd-editor-canvas class="canvas" />
          <aside class="side" aria-label="Settings">
            <mat-tab-group
              animationDuration="0ms"
              [selectedIndex]="tab()"
              (selectedIndexChange)="tab.set($event)"
              mat-stretch-tabs="false"
            >
              <mat-tab label="Widget"><gd-widget-panel class="tab" /></mat-tab>
              <mat-tab label="Sources"><gd-sources-panel class="tab" /></mat-tab>
              <mat-tab label="Filters"><gd-filters-panel class="tab" /></mat-tab>
              <mat-tab label="Layout"><gd-layout-panel class="tab" /></mat-tab>
              <mat-tab label="Dashboard"><gd-refresh-panel class="tab" /></mat-tab>
              <mat-tab [label]="issuesLabel()">
                <gd-issues-panel class="tab" (go)="goTo($event)" />
              </mat-tab>
            </mat-tab-group>
          </aside>
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
      container-type: inline-size;
    }
    .editor {
      display: flex;
      flex-direction: column;
      height: 100%;
    }
    .top {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      padding: 8px 16px;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }
    .name {
      width: 280px;
    }
    .problem {
      color: var(--mat-sys-error);
    }
    .edited {
      font: var(--mat-sys-label-medium);
      color: var(--mat-sys-on-surface-variant);
    }
    .spacer {
      flex: 1 1 auto;
    }
    .body {
      flex: 1 1 auto;
      min-height: 0;
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) 380px;
    }
    .add-widgets {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 8px;
      border-right: 1px solid var(--mat-sys-outline-variant);
    }
    .add {
      justify-content: flex-start;
    }
    .canvas {
      overflow: auto;
    }
    .side {
      overflow: auto;
      border-left: 1px solid var(--mat-sys-outline-variant);
    }
    .tab {
      display: block;
      padding: 12px 16px 24px;
    }
    /* Narrow (a tablet's width, the navigation open): the widgets to add a row, the settings below the canvas. */
    @container (max-width: 1000px) {
      .editor {
        height: auto;
      }
      .body {
        grid-template-columns: minmax(0, 1fr);
      }
      .add-widgets {
        flex-direction: row;
        flex-wrap: wrap;
        border-right: 0;
        border-bottom: 1px solid var(--mat-sys-outline-variant);
      }
      .canvas,
      .side {
        overflow: visible;
      }
      .side {
        border-left: 0;
        border-top: 1px solid var(--mat-sys-outline-variant);
      }
    }
    .hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
    }
  `,
})
export class DashboardEditor implements HasUnsavedChanges {
  /** The dashboard's id, from the address; none for a new one. */
  readonly id = input<string | undefined>(undefined);

  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly title = inject(PageTitle);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly document = inject(DOCUMENT);
  protected readonly store = inject(EditorStore);
  private readonly dashboard = inject(DashboardStore);
  private readonly palettes = inject(PaletteLibrary);
  protected readonly kinds = inject(WIDGET_KINDS);
  private readonly nameField = viewChild<ElementRef<HTMLInputElement>>('name');

  protected readonly tab = signal(0);
  protected readonly saving = signal(false);
  protected readonly nameProblem = signal<string | null>(null);
  protected readonly saveProblem = signal<SaveProblem | null>(null);

  private readonly read = rxResource({
    params: () => (this.id() ? Number(this.id()) : undefined),
    stream: ({ params }) =>
      params ? this.api.get('/api/dashboards/{id}', { path: { id: params } }) : EMPTY,
  });
  protected readonly reading = computed(() => this.read.isLoading());
  protected readonly loadProblem = computed(() => {
    const error = this.read.error();
    if (error) {
      return `Couldn't read the dashboard: ${problemMessage(problemOf(error))}`;
    }
    const read = this.read.hasValue() ? this.read.value() : undefined;
    return read && !read.can.edit ? "You can't edit this dashboard: only its owner can." : null;
  });
  /** Whether there is something to edit: a new dashboard, or one read that the user may edit. */
  protected readonly ready = signal(false);

  protected readonly issuesLabel = computed(() => {
    const count = this.store.issues().length;
    return count > 0 ? `Issues (${count})` : 'Issues';
  });

  constructor() {
    effect(() => {
      const id = this.id();
      const read = this.read.hasValue() ? this.read.value() : undefined;
      untracked(() => {
        if (!id) {
          this.start(null);
        } else if (read?.can.edit && this.store.saved().id !== read.id) {
          this.start(read);
        }
      });
    });
    // The canvas shows the draft; its widgets ask for their slices' rows.
    effect(() => this.dashboard.definition.set(this.store.draft()));
    // And are drawn with the palettes it names, as they are now (saved elsewhere, they follow).
    const named = computed(() => palettesOf(this.store.draft()), { equal: sameJson });
    effect(() => {
      const ids = named();
      untracked(() => this.palettes.want(ids));
    });
    effect(() => {
      const read = this.palettes.read();
      const shown = named().flatMap((id) => {
        const palette = read.get(id);
        return palette ? [[id, chartPalette(palette)] as const] : [];
      });
      untracked(() => this.dashboard.palettes.set(new Map(shown)));
    });
    this.dashboard.host.set(new InlineHost(this.api, () => this.dashboard.definition(), 'editor'));
    effect(() => {
      // Unnamed, the page's own title says it is new ("New dashboard").
      const name = this.store.name().trim();
      const unsaved = name ? `${name} (not saved)` : 'Not saved';
      this.title.detail.set(this.store.dirty() ? unsaved : name || null);
    });
    warnBeforeUnload(() => this.hasUnsavedChanges());
    inject(DestroyRef).onDestroy(() => this.title.detail.set(null));
  }

  hasUnsavedChanges(): boolean {
    return this.store.dirty() && !this.saving();
  }

  private start(dashboard: DashboardDto | null): void {
    this.store.load(dashboard);
    this.ready.set(true);
  }

  protected add(kind: WidgetKind): void {
    const source = this.store.draft().sources[0]?.id ?? null;
    let made = '';
    this.store.apply(`Added a ${kind.label.toLowerCase()}`, (d) => {
      const added = addWidget(d, kind.defaults(source), kind.defaultSize);
      made = added.id;
      return added.definition;
    });
    this.store.selected.set(made);
    this.tab.set(0);
    this.announcer.announce(`Added ${this.store.nameOf(made)}`);
  }

  protected goTo(place: IssuePlace): void {
    if ('widget' in place) {
      this.store.selected.set(place.widget);
      this.tab.set(0);
    } else {
      this.tab.set(tabs.indexOf(place.panel));
    }
  }

  /** Ctrl+S saves (anywhere); Ctrl+Z undoes and Ctrl+Shift+Z or Ctrl+Y redo, but where fields keep their own. */
  protected keys(event: KeyboardEvent): void {
    const mac = /Mac|iPhone|iPad/.test(this.document.defaultView?.navigator.platform ?? '');
    if (!(mac ? event.metaKey : event.ctrlKey) || event.altKey) {
      return;
    }
    const key = event.key.toLowerCase();
    if (key === 's' && !event.shiftKey) {
      event.preventDefault();
      void this.save();
      return;
    }
    const own = (event.target as Element | null)?.closest?.(
      'input, textarea, select, [contenteditable="true"], gd-code-editor, ag-grid-angular',
    );
    if (own) {
      return;
    }
    if (key === 'z' && !event.shiftKey) {
      event.preventDefault();
      this.store.undo();
    } else if ((key === 'z' && event.shiftKey) || key === 'y') {
      event.preventDefault();
      this.store.redo();
    }
  }

  /** Saves the working copy: a new dashboard is made; one there is, saved over at its version. */
  protected async save(): Promise<void> {
    if (this.saving() || this.reading() || !this.ready()) {
      return;
    }
    const name = this.store.name().trim();
    if (!name) {
      this.nameProblem.set('Name the dashboard');
      this.nameField()?.nativeElement.focus();
      return;
    }
    const saved = this.store.saved();
    const sent = this.store.draft();
    const description = this.store.description()?.trim() || null;
    this.saving.set(true);
    this.saveProblem.set(null);
    this.nameProblem.set(null);
    this.store.saveIssues.set([]);
    try {
      const dashboard =
        saved.id === null
          ? await firstValueFrom(
              this.api.post('/api/dashboards', { body: { name, description, definition: sent } }),
            )
          : await firstValueFrom(
              this.api.put('/api/dashboards/{id}', {
                path: { id: saved.id },
                body: { name, description, definition: sent, version: saved.version ?? 0 },
              }),
            );
      this.store.savedAs(dashboard, sent);
      this.announcer.announce(`${name} is saved`);
      if (saved.id === null) {
        // Saved, it has an address of its own; the editor there reads it.
        void this.router.navigate(['/dashboards', dashboard.id, 'edit'], { replaceUrl: true });
      }
    } catch (error) {
      this.refused(problemOf(error), sent);
    } finally {
      this.saving.set(false);
    }
  }

  private refused(problem: Problem, sent: Definition): void {
    if (problem.code === 'dashboard-name-taken') {
      this.nameProblem.set('You have a dashboard of this name already');
      this.nameField()?.nativeElement.focus();
      return;
    }
    if (problem.code === ProblemCode.concurrencyConflict) {
      this.saveProblem.set({
        text: 'The dashboard was changed elsewhere since it was read: read it again (your edits stay) to save over it, or save yours as a copy.',
        conflict: true,
      });
      return;
    }
    const errors = Object.entries(problem.errors ?? {});
    if (errors.length > 0) {
      const name = errors.find(([field]) => field === 'name');
      if (name) {
        this.nameProblem.set(name[1][0] ?? 'Name the dashboard');
      }
      const issues: EditorIssue[] = errors
        .filter(([field]) => field !== 'name')
        .flatMap(([field, messages]) =>
          messages.map((message) => {
            const place = placeOf(field, sent);
            const widget = place && 'widget' in place ? `${this.store.nameOf(place.widget)}: ` : '';
            return { severity: 'error' as const, message: `${widget}${message}`, place, field };
          }),
        );
      this.store.saveIssues.set(issues);
      this.saveProblem.set({
        text: "Couldn't save: some of it isn't right (Issues says what).",
        conflict: false,
      });
      if (issues.length > 0) {
        this.tab.set(tabs.indexOf('issues'));
      }
      return;
    }
    this.saveProblem.set({ text: `Couldn't save: ${problemMessage(problem)}`, conflict: false });
  }

  /** After a conflict: the dashboard as saved elsewhere is what saving goes over; the draft stays. */
  protected async readAgain(): Promise<void> {
    const id = this.store.saved().id;
    if (id === null) {
      return;
    }
    try {
      const read = await firstValueFrom(this.api.get('/api/dashboards/{id}', { path: { id } }));
      this.store.savedAs(read, read.working ?? read.published ?? this.store.draft());
      this.saveProblem.set(null);
      this.announcer.announce('Read again: saving now saves over it');
    } catch (error) {
      this.saveProblem.set({
        text: `Couldn't read it again: ${problemMessage(problemOf(error))}`,
        conflict: true,
      });
    }
  }

  /** After a conflict: the draft saved as a new dashboard of the user's, opened. */
  protected async saveCopy(): Promise<void> {
    const name = `${this.store.name().trim() || 'Dashboard'} (copy)`;
    const sent = this.store.draft();
    this.saving.set(true);
    try {
      const copy = await firstValueFrom(
        this.api.post('/api/dashboards', {
          body: { name, description: this.store.description(), definition: sent },
        }),
      );
      this.store.savedAs(copy, sent);
      this.store.name.set(copy.name);
      this.saveProblem.set(null);
      void this.router.navigate(['/dashboards', copy.id, 'edit'], { replaceUrl: true });
    } catch (error) {
      this.refused(problemOf(error), sent);
    } finally {
      this.saving.set(false);
    }
  }
}
