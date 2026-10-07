import { NgComponentOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  computed,
  effect,
  inject,
  input,
  resource,
  untracked,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIcon } from '@angular/material/icon';
import { MatMenu, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { MatProgressBar } from '@angular/material/progress-bar';
import { EMPTY, switchMap, tap, timer } from 'rxjs';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { Message } from '../../../core/ui/message';
import {
  type DashboardIssue,
  type DataConfig,
  type Widget,
  configOf,
  isChart,
  isData,
} from '../model/definition';
import { WidgetContext } from '../model/widget-context';
import { WIDGET_KINDS, kindOf } from '../model/widget-registry';
import { DashboardStore, sameJson } from '../state/dashboard-store';
import { DASHBOARD_WAITS } from '../state/waits';
import { missingOf } from '../model/complete';
import { WIDGET_EDITING } from './widget-editing';

let frames = 0;

/**
 * A widget on the grid: its title, its rows (read again when what reaches it changes, or the dashboard is
 * refreshed; those shown are kept while they are read, and when reading fails), what went wrong, and a menu (show
 * as a table, clear what is chosen). What the widget shows is its kind's component, given the widget's context.
 */
@Component({
  selector: 'gd-widget-frame',
  imports: [
    MatButton,
    MatIcon,
    MatIconButton,
    MatMenu,
    MatMenuItem,
    MatMenuTrigger,
    MatProgressBar,
    Message,
    NgComponentOutlet,
  ],
  providers: [WidgetContext],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.plain]': 'plain() && !edit',
    '[class.plain-edit]': 'plain() && !!edit',
    '[class.selected]': 'edit?.selected() === widget().id',
    '[class.moving]': 'edit?.moving() === widget().id',
  },
  template: `
    <section
      class="frame"
      [attr.aria-labelledby]="title() ? titleId : null"
      [attr.aria-label]="title() ? null : kindLabel()"
    >
      @if (!plain() || edit) {
        <header class="bar">
          @if (edit?.movable()) {
            <button
              matIconButton
              type="button"
              class="move"
              aria-roledescription="movable widget"
              [attr.aria-label]="'Move ' + edit!.nameOf(widget().id)"
              [attr.aria-pressed]="edit!.moving() === widget().id"
              [attr.data-gd-move]="widget().id"
              (pointerdown)="edit!.startMove(widget().id, $event)"
              (keydown)="edit!.key(widget().id, $event)"
            >
              <mat-icon>drag_indicator</mat-icon>
            </button>
          }
          @if (title()) {
            <h2 class="title" [id]="titleId">{{ title() }}</h2>
          } @else {
            <span class="title"></span>
          }
          @if (selected(); as selected) {
            <span class="chosen">{{ selected }}</span>
          }
          <button
            matIconButton
            type="button"
            class="menu"
            [matMenuTriggerFor]="menu"
            [attr.aria-label]="'Actions of ' + (title() ?? kindLabel())"
          >
            <mat-icon>more_vert</mat-icon>
          </button>
        </header>
      }
      @if (data.isLoading()) {
        <mat-progress-bar
          class="progress"
          mode="indeterminate"
          [attr.aria-label]="'Reading the rows of ' + (title() ?? kindLabel())"
        />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">
            {{ problem }}
            <button gdMessageAction matButton type="button" (click)="data.reload()">
              Try again
            </button>
          </gd-message>
        }
      </div>
      <div class="body">
        @if (missing(); as missing) {
          <p class="aside missing">{{ missing }}</p>
        } @else if (view.hasValue() ? view.value() : null; as component) {
          <ng-container *ngComponentOutlet="component; injector: injector" />
        } @else if (unknown()) {
          <p class="aside">This kind of widget isn't one this page knows.</p>
        }
      </div>
      @if (more(); as more) {
        <p class="aside more">{{ more }}</p>
      }
      @if (edit && !edit.interacting()) {
        <!-- On the canvas, a click chooses the widget, not its slices. -->
        <div class="cover" aria-hidden="true" (click)="edit.select(widget().id)"></div>
      }
      @if (edit?.movable()) {
        <div
          class="resize"
          aria-hidden="true"
          (pointerdown)="edit!.startResize(widget().id, $event)"
        ></div>
      }
    </section>
    <mat-menu #menu="matMenu">
      @if (chart()) {
        <button mat-menu-item type="button" (click)="context.asTable.set(!context.asTable())">
          <mat-icon>{{ context.asTable() ? 'bar_chart' : 'table' }}</mat-icon>
          {{ context.asTable() ? 'Show as chart' : 'Show as table' }}
        </button>
      }
      @if (context.selection()) {
        <button mat-menu-item type="button" (click)="store.setSelection(widget().id, null)">
          <mat-icon>filter_alt_off</mat-icon>
          Clear what is chosen
        </button>
      }
      <button mat-menu-item type="button" (click)="data.reload()">
        <mat-icon>refresh</mat-icon>
        Read its rows again
      </button>
      @if (showsData()) {
        <button mat-menu-item type="button" [disabled]="!context.data()" (click)="openData()">
          <mat-icon>table_view</mat-icon>
          Data
        </button>
      }
      @if (showsQuery()) {
        <button mat-menu-item type="button" (click)="openQuery()">
          <mat-icon>code</mat-icon>
          View query
        </button>
      }
    </mat-menu>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
      min-height: 0;
    }
    .frame {
      box-sizing: border-box;
      height: 100%;
      display: flex;
      flex-direction: column;
      position: relative;
      padding: 8px 12px 12px;
      border-radius: var(--mat-sys-corner-medium);
      background: var(--mat-sys-surface-container-low);
      border: 1px solid var(--mat-sys-outline-variant);
    }
    :host(.plain) .frame {
      background: transparent;
      border-color: transparent;
      padding: 4px 0;
    }
    :host(.selected) .frame {
      outline: 2px solid var(--mat-sys-primary);
      outline-offset: 2px;
    }
    :host(.moving) .frame {
      box-shadow: var(--mat-sys-level3);
    }
    .move {
      cursor: grab;
      touch-action: none;
      margin-inline-start: -8px;
    }
    .cover {
      position: absolute;
      inset: 48px 0 0;
      cursor: pointer;
    }
    /* Unframed text on the canvas: its handle and menu float over it, so it reads as on the page. */
    :host(.plain-edit) .frame {
      background: transparent;
      border-style: dashed;
      padding: 4px 8px;
    }
    :host(.plain-edit) .bar {
      position: absolute;
      top: 2px;
      right: 2px;
      z-index: 1;
      min-height: 0;
      border-radius: 20px;
      background: var(--mat-sys-surface-container-high);
    }
    :host(.plain-edit) .bar .title {
      display: none;
    }
    :host(.plain-edit) .move {
      margin-inline-start: 0;
    }
    :host(.plain-edit) .cover {
      inset: 0;
    }
    .resize {
      position: absolute;
      right: 0;
      bottom: 0;
      width: 16px;
      height: 16px;
      cursor: nwse-resize;
      touch-action: none;
      background: linear-gradient(
        135deg,
        transparent 50%,
        var(--mat-sys-outline) 50%,
        var(--mat-sys-outline) 60%,
        transparent 60%,
        transparent 75%,
        var(--mat-sys-outline) 75%,
        var(--mat-sys-outline) 85%,
        transparent 85%
      );
      border-bottom-right-radius: var(--mat-sys-corner-medium);
    }
    .bar {
      display: flex;
      align-items: center;
      gap: 8px;
      min-height: 40px;
    }
    .title {
      flex: 1 1 auto;
      margin: 0;
      font: var(--mat-sys-title-medium);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .chosen {
      font: var(--mat-sys-label-medium);
      color: var(--mat-sys-on-surface-variant);
    }
    .progress {
      position: absolute;
      left: 0;
      right: 0;
      top: 0;
    }
    .body {
      flex: 1 1 auto;
      min-height: 0;
      position: relative;
    }
    .aside {
      margin: 4px 0 0;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class WidgetFrame {
  readonly widget = input.required<Widget>();
  /** Whether the dashboard is being edited. */
  readonly editing = input(false);

  protected readonly context = inject(WidgetContext);
  protected readonly store = inject(DashboardStore);
  protected readonly injector = inject(Injector);
  private readonly kinds = inject(WIDGET_KINDS);
  private readonly dialog = inject(MatDialog);
  private readonly element = inject(ElementRef<HTMLElement>);
  private readonly waits = inject(DASHBOARD_WAITS);
  /** The editor's, on its canvas. */
  protected readonly edit = inject(WIDGET_EDITING, { optional: true });
  /** The rows last asked for: asked again with the same, or after a refresh, they are read fresh. */
  private asked: { readonly params: object; readonly refreshes: number } | null = null;
  protected readonly titleId = `gd-widget-${++frames}`;

  protected readonly config = computed(() => configOf(this.widget()));
  protected readonly title = computed(() => this.widget().title?.trim() || null);
  protected readonly kind = computed(() => kindOf(this.kinds, this.config()));
  protected readonly kindLabel = computed(() => this.kind()?.label ?? 'Widget');
  protected readonly unknown = computed(() => !this.kind());
  protected readonly chart = computed(() => isChart(this.config()));
  /** A text widget without a title has no frame: headings and notes stand on the page. */
  protected readonly plain = computed(() => !isData(this.config()) && !this.title());
  /** Whether its rows may be shown in a popup: always in the application, as the dashboard says embedded. */
  protected readonly showsData = computed(
    () =>
      isData(this.config()) &&
      (this.store.host()?.kind !== 'embed' || this.store.definition().public.showData),
  );
  /** Whether its query may be seen: signed in. */
  protected readonly showsQuery = computed(
    () => isData(this.config()) && !!this.store.host()?.canViewQueries,
  );

  /** What reaches the widget: the same as JSON, nothing is asked again. */
  private readonly inputs = computed(() => this.store.inputsOf(this.widget().id), {
    equal: sameJson,
  });

  protected readonly view = resource({
    params: () => this.kind(),
    loader: ({ params }) => params.view(),
  });

  /** What the rows are asked for with: equal as JSON, they aren't asked for again. */
  private readonly request = computed(
    () => {
      const host = this.store.host();
      const id = this.widget().id;
      return isData(this.config()) && host && !this.missing()
        ? {
            id,
            inputs: this.inputs(),
            refreshes: this.store.refreshes(),
            offset: this.context.offset(),
            basis: host.basis(id, this.store.definition(), this.inputs()),
          }
        : undefined;
    },
    { equal: sameJson },
  );

  protected readonly data = rxResource({
    params: () => this.request(),
    stream: ({ params }) => {
      const host = untracked(() => this.store.host());
      if (!host) {
        return EMPTY;
      }
      const config = untracked(() => this.config());
      const page =
        config.kind === 'table'
          ? { offset: params.offset, limit: config.pageSize, count: false }
          : null;
      // Fresh when the dashboard was refreshed since, or the same rows are asked for again; not for others.
      const asked = this.asked;
      const fresh = !!asked && (params === asked.params || params.refreshes !== asked.refreshes);
      this.asked = { params, refreshes: params.refreshes };
      const read = () =>
        host.data(params.id, params.inputs, page, fresh).pipe(
          tap({
            next: (data) => this.store.setIssues(params.id, data.issues ?? []),
            error: (error: unknown) => {
              const issues = problemOf(error).body?.['issues'];
              this.store.setIssues(
                params.id,
                Array.isArray(issues) ? (issues as DashboardIssue[]) : [],
              );
            },
          }),
        );
      // The editor's previews wait for edits to pause.
      return host.kind === 'editor' ? timer(this.waits.preview).pipe(switchMap(read)) : read();
    },
    equal: sameJson,
  });

  /** What the widget still needs before it has rows (as the editor makes it); nothing is asked for till then. */
  protected readonly missing = computed(() => {
    const config = this.config();
    // Only the editor's widgets can be: published ones passed the server's checks (and public ones have no sources).
    return isData(config) && this.store.host()?.kind === 'editor'
      ? missingOf(config, this.store.definition().sources)
      : null;
  });

  protected readonly problem = computed(() => {
    const error = this.data.error();
    if (!error) {
      return null;
    }
    // A refusal by field says what is wrong better than its title.
    const problem = problemOf(error);
    const first = Object.values(problem.errors ?? {})[0]?.[0];
    return first ?? problemMessage(problem);
  });

  /** What is chosen in the widget, briefly: how many slices, or left out. */
  protected readonly selected = computed(() => {
    const selection = this.context.selection();
    if (!selection) {
      return null;
    }
    const n = selection.keys.length;
    return selection.mode === 'include' ? `${n} chosen` : `${n} left out`;
  });

  protected readonly more = computed(() => {
    const data = this.context.data();
    const config = this.config();
    if (!data?.truncated || config.kind === 'table') {
      return null;
    }
    return config.kind === 'bar' && config.series
      ? `The first ${data.categories?.length ?? data.rows.length} categories of more`
      : `The first ${data.rows.length} of more`;
  });

  /** Shows the rows read, in a popup; signed in, also the rows they were worked out from. */
  protected async openData(): Promise<void> {
    const data = this.context.data();
    const config = this.config();
    const host = this.store.host();
    if (!data || !host || !isData(config)) {
      return;
    }
    const { DataDialog } = await import('../popups/data-dialog');
    const widget = this.widget().id;
    const inputs = this.inputs();
    this.dialog.open(DataDialog, {
      data: {
        title: this.title() ?? this.kindLabel(),
        data,
        config: config as DataConfig,
        query: host.canViewUnderlying ? () => host.query(widget, inputs) : null,
      },
      width: '960px',
      maxWidth: '95vw',
      maxHeight: '90vh',
      // Embedded, the frame may be as tall as the dashboard: the popup opens where the widget is, not in the
      // middle of a page its parent shows a part of.
      position:
        host.kind === 'embed'
          ? {
              top: `${Math.max(8, (this.element.nativeElement as HTMLElement).getBoundingClientRect().top)}px`,
            }
          : undefined,
    });
  }

  /** Shows the widget's query, as it runs with what reaches it now, and its SQL. */
  protected async openQuery(): Promise<void> {
    const host = this.store.host();
    const query = host?.query(this.widget().id, this.inputs());
    if (!query) {
      return;
    }
    const { QueryDialog } = await import('../popups/query-dialog');
    this.dialog.open(QueryDialog, {
      data: { title: this.title() ?? this.kindLabel(), query },
      width: '960px',
      maxWidth: '95vw',
      maxHeight: '90vh',
    });
  }

  constructor() {
    effect(() => this.context.show(this.widget()));
    effect(() => this.context.editing.set(this.editing()));
    // On the canvas, slices are chosen only to try the dashboard out.
    effect(() => this.context.choosing.set(!this.edit || this.edit.interacting()));
    effect(() => this.context.selection.set(this.store.selections()[this.widget().id] ?? null));
    this.context.choose = (key, action) => this.store.choose(this.widget().id, key, action);
    // The rows shown stay while they are read again, and when that fails.
    effect(() => {
      const value = this.data.hasValue() ? this.data.value() : null;
      const loading = this.data.isLoading();
      const error = this.data.error();
      // Written without being read: the store's signals aren't what this follows.
      untracked(() => {
        const id = this.widget().id;
        if (value) {
          this.context.data.set(value);
          this.store.setRefreshedAt(id, value.refreshedAt);
        }
        this.context.loading.set(loading);
        this.store.setLoading(id, loading);
        this.store.setFailing(id, !!error);
        this.context.problem.set(error ? problemOf(error) : null);
      });
    });
  }
}
