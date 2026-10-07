import { DOCUMENT } from '@angular/common';
import {
  Component,
  DestroyRef,
  LOCALE_ID,
  computed,
  effect,
  inject,
  input,
  isDevMode,
  output,
  signal,
  untracked,
} from '@angular/core';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTooltip } from '@angular/material/tooltip';
import type { UrlTree } from '@angular/router';
import { AgGridAngular } from 'ag-grid-angular';
import {
  BigIntFilterModule,
  type CellFocusedEvent,
  ColumnApiModule,
  DateFilterModule,
  type GridApi,
  type GridOptions,
  type GridReadyEvent,
  InfiniteRowModelModule,
  LocaleModule,
  type Module,
  NumberFilterModule,
  PaginationModule,
  RenderApiModule,
  RowApiModule,
  type SortModelItem,
  TextFilterModule,
  TooltipModule,
  ValidationModule,
  CellStyleModule,
} from 'ag-grid-community';
import { type Subscription, firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../../core/api/api-client';
import {
  type Problem,
  ProblemCode,
  isSessionProblem,
  problemMessage,
  problemOf,
} from '../../core/api/problem';
import { readStored, storageOf, writeStored } from '../../core/browser/stored';
import { browseUrlTree, crumbOf } from '../../core/browse/browse-url';
import { CatalogVersion } from '../../core/catalog/catalog-version';
import type { ParameterValues } from '../../core/query/query-url';
import { Message } from '../../core/ui/message';

import {
  type GridColumn,
  type GridRow,
  filtersOf,
  indexOfColId,
  sortOf,
} from '../browse/grid/grid-columns';
import { GridInspector, type Inspected } from '../browse/grid/grid-inspector';
import type { GridCollection } from '../browse/grid/grid-links';
import { BROWSE_PAGE_SIZE, inspectorKey } from '../browse/grid/grid-settings';
import { gridTheme } from '../browse/grid/grid-theme';
import type { RowCount } from '../browse/grid/paged-datasource';
import {
  type Diagnostic,
  type QueryParameterInput,
  type QueryStats,
  QueryDatasource,
  type ResultSchema,
  type ResultsEvents,
  type ResultsPage,
  type ResultsQuery,
  resultsPageOf,
  sameResults,
} from './query-datasource';
import {
  type LinkTarget,
  type ResultLinks,
  gridColumnsOf,
  indexOfRelatedColId,
  leadsSomewhere,
  linkText,
  relatedOf,
  resultColumnDefsOf,
} from './query-links';
import { timeText } from './time-text';

type QueryLink = Schema<'QueryLinkDto'>;

/** What the grid needs of the grid's library: the infinite row model, pages, filters, sorting and links' cells. */
const modules: Module[] = [
  InfiniteRowModelModule,
  PaginationModule,
  TextFilterModule,
  NumberFilterModule,
  BigIntFilterModule,
  DateFilterModule,
  RowApiModule,
  RenderApiModule,
  CellStyleModule,
  ColumnApiModule,
  LocaleModule,
  TooltipModule,
  ...(isDevMode() ? [ValidationModule] : []),
];

/** A run of a query: its text and its parameters' values, as sent. Each run is asked for anew. */
export interface QueryRun {
  readonly text: string;
  readonly parameters: readonly QueryParameterInput[];
  /** Tells runs apart, of the same text too. */
  readonly serial: number;
}

/** What came of a run: a page read (what it took, what was wrong), the rows not read (why), or stopped. */
export type RunOutcome =
  | {
      readonly kind: 'read';
      readonly run: QueryRun;
      readonly stats: QueryStats;
      readonly warnings: readonly Diagnostic[];
    }
  | { readonly kind: 'failed'; readonly run: QueryRun; readonly problem: Problem }
  | { readonly kind: 'stopped'; readonly run: QueryRun };

/** Where a link of the rows leads: rows to browse, or a query of the rows (a group's). */
export type FollowedLink =
  | { readonly kind: 'browse'; readonly url: UrlTree }
  | { readonly kind: 'query'; readonly text: string; readonly values: ParameterValues };

/** The schema of a run's rows, and their first page. */
interface Primed {
  readonly run: QueryRun;
  readonly page: ResultsPage;
  /** The catalog's version the rows were read at: their links are followed at it. */
  readonly version: string | null;
}

/** A run's first page as it comes. */
type Priming =
  | { readonly status: 'idle' }
  | { readonly status: 'loading' | 'stopped'; readonly run: QueryRun }
  | { readonly status: 'read'; readonly primed: Primed }
  | { readonly status: 'failed'; readonly run: QueryRun; readonly problem: Problem };

/** A grid made for a run. */
interface Made {
  readonly key: string;
  readonly run: QueryRun;
  readonly schema: ResultSchema;
  readonly columns: readonly GridColumn[];
  readonly related: readonly GridCollection[];
  readonly options: GridOptions<GridRow>;
  readonly datasource: QueryDatasource;
  readonly version: string | null;
}

/** The grid shown, and the datasource its pages are of. */
interface Live {
  readonly key: string;
  readonly api: GridApi<GridRow>;
  datasource: QueryDatasource;
}

/** The cell the keyboard is on (or clicked), for the inspector. */
interface Focused {
  readonly colId: string;
  readonly row: GridRow | null;
}

/**
 * The rows of a run of a query, in a grid a page at a time (AG Grid's infinite row model, with pages): filtered by
 * its columns' filters and sorted by their headers, which the server composes onto the query; counted. Values that
 * lead somewhere are links (the rows they refer to or were worked out from, a group's rows), and so are the rows
 * that refer to each row, when they are an entity's: where they lead, the server says, and the page goes there
 * (`followed`). Beside it, the inspector says what the cell the keyboard is on holds, where its column's values
 * come from, and where they lead. A run may be stopped before its rows come.
 */
let results = 0;

/** A schema whose values lead nowhere, and whose rows nothing refers to. */
function withoutLinks(schema: ResultSchema): ResultSchema {
  return {
    ...schema,
    columns: schema.columns.map((column) => ({ ...column, link: null })),
    rowIdentity: schema.rowIdentity ? { ...schema.rowIdentity, related: [] } : null,
  };
}

@Component({
  selector: 'gd-query-results',
  imports: [
    AgGridAngular,
    GridInspector,
    MatButton,
    MatIcon,
    MatIconButton,
    MatProgressBar,
    MatTooltip,
    Message,
  ],
  templateUrl: './query-results.html',
  styleUrl: './query-results.scss',
})
export class QueryResults {
  private readonly api = inject(ApiClient);
  private readonly locale = inject(LOCALE_ID);
  private readonly pageSize = inject(BROWSE_PAGE_SIZE);
  private readonly storage = storageOf(inject(DOCUMENT));
  private readonly versions = inject(CatalogVersion);
  protected readonly modules = modules;
  private live: Live | null = null;
  /** Set while the grid is asked anew: its events then aren't the user's. */
  private following = false;
  /** Set while a link is followed: one at a time. */
  private linking = false;

  /** The run whose rows are shown. */
  readonly run = input.required<QueryRun>();
  /** Whether values lead where they refer (and rows to those referring to them); off, the rows are only read. */
  readonly links = input(true);
  /** Whether the inspector may be shown beside the rows. */
  readonly inspector = input(true);
  /** The inspector's id, this grid's own. */
  protected readonly inspectorId = `gd-results-inspector-${++results}`;
  /** A link of the rows followed: where it leads, for the page to go there. */
  readonly followed = output<FollowedLink>();
  /** What came of the run: each page read, or why the rows couldn't be. */
  readonly outcome = output<RunOutcome>();

  /** The run's first page, with its schema: being read, read, failed, or stopped before it came. */
  protected readonly primed = signal<Priming>({ status: 'idle' });
  private priming: Subscription | null = null;
  protected readonly loading = computed(() => this.primed().status === 'loading');
  protected readonly stopped = computed(() => this.primed().status === 'stopped');
  protected readonly made = computed<Made | undefined>(
    () => {
      const primed = this.primed();
      return primed.status === 'read' ? untracked(() => this.make(primed.primed)) : undefined;
    },
    { equal: (a, b) => a?.key === b?.key },
  );

  protected readonly count = signal<RowCount | null>(null);
  /** Why a page of the rows couldn't be read (but the first, said as the run's problem). */
  protected readonly problem = signal<Problem | null>(null);
  /** Why a link couldn't be followed, or that it leads nowhere. */
  protected readonly linkProblem = signal<string | null>(null);
  /** What running the page shown took. */
  protected readonly stats = signal<QueryStats | null>(null);
  protected readonly focused = signal<Focused | null>(null);
  /** Whether the inspector is shown: as the user last left it (here or browsing). */
  protected readonly inspecting = signal(readStored(this.storage, inspectorKey) !== 'hidden');

  protected readonly primeProblem = computed(() => {
    const primed = this.primed();
    return primed.status === 'failed' && !isSessionProblem(primed.problem) ? primed.problem : null;
  });
  protected readonly countText = computed(() => {
    const count = this.count();
    if (!count) {
      return '';
    }
    const rows = count.rows.toLocaleString(this.locale);
    if (count.kind === 'atLeast') {
      return `At least ${rows} rows`;
    }
    return count.rows === 1 ? '1 row' : `${rows} rows`;
  });
  protected readonly statsText = computed(() => {
    const stats = this.stats();
    return stats ? `in ${timeText(stats.elapsedMs, this.locale)}` : '';
  });
  protected readonly inspected = computed<Inspected | null>(() => {
    const focused = this.focused();
    const made = this.made();
    if (!focused || !made) {
      return null;
    }
    const { colId, row } = focused;
    const related = made.related[indexOfRelatedColId(colId)];
    if (related) {
      return { kind: 'collection', collection: related, row, linked: row !== null };
    }
    const index = indexOfColId(colId);
    const column = made.columns[index];
    if (!column) {
      return null;
    }
    const link = made.schema.columns[index].link;
    const leads = link ? leadsSomewhere(link, row) : false;
    return {
      kind: 'column',
      column,
      index,
      row,
      reference: null,
      linked: leads,
      value: row ? row.v[index] : undefined,
      display: null,
      edit: null,
      leadsTo: link
        ? {
            text: linkText(link),
            linked: leads,
            why: row && !leads ? 'A NULL leads nowhere.' : null,
          }
        : null,
    };
  });
  protected readonly message = problemMessage;

  private readonly followers: ResultLinks = {
    follow: (target, row) => void this.follow(target, row),
  };

  constructor() {
    // Each run is asked for anew, the one before let go.
    effect(() => {
      const run = this.run();
      untracked(() => this.prime(run));
    });
    inject(DestroyRef).onDestroy(() => this.priming?.unsubscribe());
  }

  protected gridReady(event: GridReadyEvent<GridRow>, made: Made): void {
    this.live = { key: made.key, api: event.api, datasource: made.datasource };
    this.count.set(null);
    this.problem.set(null);
    this.linkProblem.set(null);
    this.focused.set(null);
  }

  /** Sorted or filtered in the grid: the rows so, from their first page. */
  protected changed(): void {
    const live = this.current();
    const made = this.made();
    if (this.following || !live || !made) {
      return;
    }
    const query: ResultsQuery = {
      ...live.datasource.query,
      filters: filtersOf(live.api.getFilterModel(), made.columns),
      sort: sortOf(sortModelOf(live.api), made.columns),
    };
    if (!sameResults(query, live.datasource.query)) {
      this.ask(live, query);
    }
  }

  protected cellFocused(event: CellFocusedEvent<GridRow>): void {
    const live = this.current();
    const colId = typeof event.column === 'string' ? event.column : event.column?.getColId();
    if (!live || !colId || event.rowIndex === null || event.rowPinned) {
      return;
    }
    this.focused.set({
      colId,
      row: live.api.getDisplayedRowAtIndex(event.rowIndex)?.data ?? null,
    });
  }

  protected toggleInspector(): void {
    this.inspecting.update((shown) => !shown);
    writeStored(this.storage, inspectorKey, this.inspecting() ? 'shown' : 'hidden');
  }

  /** The page that couldn't be read, asked for again. */
  protected retry(): void {
    const live = this.current();
    if (live) {
      this.ask(live, live.datasource.query, live.api.paginationGetCurrentPage());
    }
  }

  /** Stops the run before its rows come: its request is let go, and the server stops the query. */
  protected stop(): void {
    const primed = this.primed();
    if (primed.status === 'loading') {
      this.priming?.unsubscribe();
      // Said by the notice that it was stopped (a status).
      this.primed.set({ status: 'stopped', run: primed.run });
      this.outcome.emit({ kind: 'stopped', run: primed.run });
    }
  }

  /** Asks for the schema of the run's rows, with their first page and count. */
  private prime(run: QueryRun): void {
    this.priming?.unsubscribe();
    // Nothing of the run before is said of this one.
    this.count.set(null);
    this.stats.set(null);
    this.problem.set(null);
    this.linkProblem.set(null);
    this.focused.set(null);
    this.primed.set({ status: 'loading', run });
    this.priming = this.api
      .post('/api/query/execute', {
        body: {
          text: run.text,
          parameters: [...run.parameters],
          grid: { offset: 0, limit: this.pageSize },
          includeSchema: true,
          includeCount: true,
        },
      })
      .subscribe({
        // The catalog's version the answer gave (kept as it came).
        next: (page) =>
          this.primed.set({
            status: 'read',
            primed: { run, page: resultsPageOf(page), version: this.versions.version() },
          }),
        error: (error: unknown) => {
          const problem = problemOf(error);
          this.primed.set({ status: 'failed', run, problem });
          if (!isSessionProblem(problem)) {
            this.outcome.emit({ kind: 'failed', run, problem });
          }
        },
      });
  }

  private make(primed: Primed): Made {
    // A page asked for with its schema has it; without links, it leads nowhere.
    const read = primed.page.schema ?? { columns: [], rowIdentity: null };
    const schema: ResultSchema = this.links() ? read : withoutLinks(read);
    const query: ResultsQuery = {
      text: primed.run.text,
      parameters: primed.run.parameters,
      filters: [],
      sort: [],
    };
    const datasource = new QueryDatasource(this.api, query, this.events(), {
      query,
      page: primed.page,
      limit: this.pageSize,
    });
    const options: GridOptions<GridRow> = {
      theme: gridTheme,
      loadThemeGoogleFonts: false,
      columnDefs: resultColumnDefsOf(schema, this.followers),
      defaultColDef: { resizable: true, minWidth: 72 },
      rowModelType: 'infinite',
      datasource,
      pagination: true,
      paginationPageSize: this.pageSize,
      paginationPageSizeSelector: false,
      cacheBlockSize: this.pageSize,
      maxBlocksInCache: 10,
      // Pages asked for on the way to another (sorted, filtered anew) go before they are fetched.
      blockLoadDebounceMillis: 10,
      infiniteInitialRowCount: this.pageSize,
      localeText: {
        noRowsToShow: 'No rows',
        noMatchingRows: 'No rows match the filters',
        loadingOoo: 'Loading…',
      },
      ensureDomOrder: true,
      enableCellTextSelection: true,
      tooltipShowDelay: 600,
    };
    return {
      key: String(primed.run.serial),
      run: primed.run,
      schema,
      columns: gridColumnsOf(schema),
      related: relatedOf(schema),
      options,
      datasource,
      version: primed.version,
    };
  }

  /** The grid shown, if it is the one made last (one made before may still be going). */
  private current(): Live | null {
    const live = this.live;
    return live && live.key === untracked(this.made)?.key && !live.api.isDestroyed() ? live : null;
  }

  /** Whether the grid's pages are a datasource's: the grid shown's, or the one made last's, before it shows. */
  private shows(datasource: object): boolean {
    const live = this.current();
    return live ? live.datasource === datasource : untracked(this.made)?.datasource === datasource;
  }

  /** Asks for the rows of a query anew, from a page (the first, unless said). */
  private ask(live: Live, query: ResultsQuery, page = 0): void {
    live.datasource.retire();
    live.datasource = new QueryDatasource(this.api, query, this.events());
    this.count.set(null);
    this.problem.set(null);
    this.following = true;
    try {
      clearFailure(live.api);
      live.api.setGridOption('datasource', live.datasource);
      live.api.setRowCount((page + 1) * this.pageSize, false);
      live.api.paginationGoToPage(page);
      // The pages asked for on the way go before they are fetched (see blockLoadDebounceMillis).
      live.api.purgeInfiniteCache();
    } finally {
      this.following = false;
    }
  }

  private events(): ResultsEvents {
    return {
      loaded: (datasource, count) => {
        if (!this.shows(datasource)) {
          return;
        }
        this.count.set(count);
        this.problem.set(null);
        const page = (datasource as QueryDatasource).last;
        if (page) {
          this.stats.set(page.stats);
          this.outcome.emit({
            kind: 'read',
            run: untracked(this.run),
            stats: page.stats,
            warnings: page.warnings,
          });
        }
        const live = this.current();
        if (live) {
          clearFailure(live.api);
        }
      },
      pastEnd: (datasource) => {
        const live = this.current();
        if (live && live.datasource === datasource) {
          this.ask(live, datasource.query);
        }
      },
      failed: (datasource, problem) => {
        if (!this.shows(datasource) || isSessionProblem(problem)) {
          return;
        }
        this.problem.set(problem);
        this.outcome.emit({ kind: 'failed', run: untracked(this.run), problem });
        const live = this.current();
        if (live) {
          live.api.setGridOption('overlayComponentParams', {
            noRows: { overlayText: "Couldn't read the rows" },
          });
          live.api.setGridOption('activeOverlay', 'agNoRowsOverlay');
        }
      },
    };
  }

  /** Follows a link of a row: where it leads is asked of the server, for the rows of the page shown. */
  private async follow(target: LinkTarget, row: GridRow): Promise<void> {
    const live = this.current();
    const made = untracked(this.made);
    const page = live?.datasource.last;
    if (!live || !made || !page || this.linking) {
      return;
    }
    this.linking = true;
    this.linkProblem.set(null);
    // An answer for rows no longer shown (run anew, sorted, filtered) leads nowhere now.
    const datasource = live.datasource;
    const stale = () =>
      untracked(this.made)?.key !== made.key || this.current()?.datasource !== datasource;
    try {
      const link = await firstValueFrom(
        this.api.post('/api/query/link', {
          body: {
            text: page.queryText,
            parameters: page.parameters.map((parameter) => ({
              name: parameter.name,
              type: parameter.type,
              value: parameter.value,
            })),
            row: [...row.v],
            column: 'column' in target ? target.column : null,
            related: 'related' in target ? target.related : null,
            catalogVersion: made.version,
          },
        }),
      );
      if (stale()) {
        return;
      }
      const followed = followedOf(link);
      if (followed) {
        this.followed.emit(followed);
      } else {
        this.linkProblem.set('It leads to no rows.');
      }
    } catch (error) {
      const problem = problemOf(error);
      if (!stale() && !isSessionProblem(problem)) {
        this.linkProblem.set(
          problem.code === ProblemCode.concurrencyConflict
            ? "The catalog changed since the rows were read, so where their links lead isn't known: run the query again."
            : `Couldn't follow the link: ${problemMessage(problem)}`,
        );
      }
    } finally {
      this.linking = false;
    }
  }
}

/** Where a link leads, as the page goes there: rows to browse, else a query of them; null for nowhere. */
export function followedOf(link: QueryLink): FollowedLink | null {
  const browse = link.browse;
  if (browse?.entity) {
    return {
      kind: 'browse',
      url: browseUrlTree({
        crumbs: [crumbOf(browse.entity, { row: link.key ? link.key.map(keyText) : null })],
        at: 0,
      }),
    };
  }
  if (browse?.from && browse.navigation) {
    return {
      kind: 'browse',
      url: browseUrlTree({
        crumbs: [
          crumbOf(browse.from.entity, { row: browse.from.key.map(keyText) }),
          crumbOf(browse.navigation),
        ],
        at: 1,
      }),
    };
  }
  if (link.queryText === null) {
    return null;
  }
  return {
    kind: 'query',
    text: link.queryText,
    values: Object.fromEntries(
      link.parameters.map((parameter) => [
        parameter.name,
        parameter.value === null || parameter.value === undefined ? null : keyText(parameter.value),
      ]),
    ),
  };
}

/** A value as the address holds it: text as it is, anything else as JSON writes it. */
function keyText(value: unknown): string {
  return typeof value === 'string' ? value : JSON.stringify(value);
}

/** The grid's overlay for rows that couldn't be read goes. */
function clearFailure(api: GridApi<GridRow>): void {
  if (api.getGridOption('activeOverlay')) {
    api.setGridOption('activeOverlay', undefined);
    api.setGridOption('overlayComponentParams', undefined);
  }
}

function sortModelOf(api: GridApi<GridRow>): SortModelItem[] {
  return api
    .getColumnState()
    .filter((state) => state.sort)
    .sort((a, b) => (a.sortIndex ?? 0) - (b.sortIndex ?? 0))
    .map((state) => ({ colId: state.colId, sort: state.sort === 'desc' ? 'desc' : 'asc' }));
}
