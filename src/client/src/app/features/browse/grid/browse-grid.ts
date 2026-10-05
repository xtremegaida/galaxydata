import {
  Component,
  ElementRef,
  InjectionToken,
  LOCALE_ID,
  computed,
  effect,
  inject,
  input,
  isDevMode,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { DOCUMENT, LocationStrategy } from '@angular/common';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTooltip } from '@angular/material/tooltip';
import { Router, type UrlTree } from '@angular/router';
import { AgGridAngular } from 'ag-grid-angular';
import {
  BigIntFilterModule,
  type CellFocusedEvent,
  CellStyleModule,
  ColumnApiModule,
  DateFilterModule,
  type GridApi,
  type GridOptions,
  type GridReadyEvent,
  GridStateModule,
  InfiniteRowModelModule,
  LocaleModule,
  type Module,
  NumberFilterModule,
  PaginationModule,
  RenderApiModule,
  RowApiModule,
  RowSelectionModule,
  type SelectionChangedEvent,
  type SortModelItem,
  TextFilterModule,
  TooltipModule,
  ValidationModule,
} from 'ag-grid-community';
import { type Observable, catchError, map, throwError } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import {
  type Problem,
  isSessionProblem,
  problemMessage,
  problemOf,
} from '../../../core/api/problem';
import { readStored, storageOf, writeStored } from '../../../core/browser/stored';
import type { BrowseCrumb } from '../../../core/browse/browse-url';
import { followCatalog } from '../../../core/catalog/catalog-changes';
import { Message } from '../../../core/ui/message';
import {
  BrowseDatasource,
  type BrowsePage,
  type BrowseQuery,
  type BrowseSource,
  type DatasourceEvents,
  type PrimedPage,
  type RowCount,
  sameQuery,
} from './browse-datasource';
import {
  type GridRow,
  columnStateOf,
  filterModelOf,
  filtersOf,
  colIdOf,
  indexOfColId,
  keyOf,
  sortOf,
} from './grid-columns';
import { GridInspector, type Inspected } from './grid-inspector';
import {
  type CellLink,
  type GridLinks,
  type LinkSchema,
  cellLinkOf,
  indexOfCollectionColId,
  linkedColIdsOf,
  linkedColumnDefsOf,
  referenceOf,
} from './grid-links';
import { gridTheme } from './grid-theme';

/** How many rows a page of the grid has. */
export const BROWSE_PAGE_SIZE = new InjectionToken<number>('BROWSE_PAGE_SIZE', {
  factory: () => 100,
});

/**
 * What the grid needs of the grid's library: the infinite row model, pages, filters, sorting, choosing rows, and
 * refreshing cells (their links).
 */
const modules: Module[] = [
  InfiniteRowModelModule,
  PaginationModule,
  TextFilterModule,
  NumberFilterModule,
  BigIntFilterModule,
  DateFilterModule,
  RowSelectionModule,
  RowApiModule,
  RenderApiModule,
  CellStyleModule,
  ColumnApiModule,
  GridStateModule,
  LocaleModule,
  TooltipModule,
  ...(isDevMode() ? [ValidationModule] : []),
];

/** Where a navigation followed from a row leads (its key, its values as text). */
export type LinkTo = (row: readonly string[], navigation: string) => UrlTree;

/** The schema of a source's rows, and their first page in the address's state when that could be asked for. */
interface Primed extends LinkSchema {
  readonly keyed: boolean;
  readonly page: PrimedPage | null;
}

/** A grid made for a schema and a state: made again for other columns, or an address the grid can't follow. */
interface Made {
  readonly key: string;
  /** The rows' columns, what they refer to and what refers to them. */
  readonly schema: LinkSchema;
  /** The ids of the columns with links. */
  readonly linked: readonly string[];
  readonly options: GridOptions<GridRow>;
  readonly datasource: BrowseDatasource;
  readonly page: number;
  readonly row: readonly string[] | null;
  /** What of the address's state the grid can't hold, and left out. */
  readonly left: readonly string[];
  /** The address's state the grid was made for. */
  readonly from: BrowseCrumb;
}

/** The grid shown, and where it is: the query its pages are of, the page shown, and the row chosen. */
interface Live {
  readonly key: string;
  readonly api: GridApi<GridRow>;
  datasource: BrowseDatasource;
  page: number;
  row: readonly string[] | null;
}

/** The cell the keyboard is on (or clicked), for the inspector. */
interface Focused {
  readonly colId: string;
  readonly row: GridRow | null;
}

/** What is wrong with a condition, placed in it (counting from 0) when the server could. */
export interface WhereProblem {
  readonly message: string;
  readonly start: number | null;
  readonly end: number | null;
}

/**
 * The rows of a source in a grid, a page at a time from the API (AG Grid's infinite row model, with pages): filtered
 * by its columns' filters and by a condition in the query language, sorted by its columns' headers, a row chosen by
 * clicking it. The grid's state is the address's (`crumb`): what changes in the grid is said (`crumbChange`) for the
 * address to follow, and an address gone elsewhere (back, forward, a link) is shown. Beside it, the inspector says
 * what the cell the keyboard is on holds, and where its column's values come from.
 */
@Component({
  selector: 'gd-browse-grid',
  imports: [
    AgGridAngular,
    GridInspector,
    MatButton,
    MatFormField,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatProgressBar,
    MatTooltip,
    Message,
  ],
  templateUrl: './browse-grid.html',
  styleUrl: './browse-grid.scss',
})
export class BrowseGrid {
  static readonly inspectorKey = 'gd.inspector';

  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly locationStrategy = inject(LocationStrategy);
  private readonly locale = inject(LOCALE_ID);
  private readonly pageSize = inject(BROWSE_PAGE_SIZE);
  private readonly storage = storageOf(inject(DOCUMENT));
  protected readonly modules = modules;
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly whereField = viewChild<ElementRef<HTMLInputElement>>('whereField');
  private live: Live | null = null;
  /** Set while the grid is made to show what the address says: its events then aren't the user's. */
  private following = false;
  /** Set when a grid with the keyboard in it is made anew: the keyboard goes to the new one. */
  private refocus = false;

  /** What the rows are of. */
  readonly source = input.required<BrowseSource>();
  /** The grid's state, as the address says it. */
  readonly crumb = input.required<BrowseCrumb>();
  /** The grid's state changed in the grid: the address follows it. */
  readonly crumbChange = output<BrowseCrumb>();
  /**
   * Where navigations from rows lead: to the rows a column's values refer to, and the collections of rows that
   * refer to them. Without it, cells have no links.
   */
  readonly linkTo = input<LinkTo | null>(null);

  private readonly sourceKey = computed(() => sourceKeyOf(this.source()));
  private readonly followed = followCatalog(() => this.primed.reload());
  protected readonly primed = rxResource({
    params: () => ({ key: this.sourceKey() }),
    stream: () => this.prime(untracked(this.source), untracked(this.crumb)).pipe(this.followed()),
  });
  /** Counts the grids made for the address gone where the grid can't follow it in place. */
  private readonly remade = signal(0);
  /** Whether cells have links (where they lead changes with the address; whether they have them, not). */
  private readonly linked = computed(() => this.linkTo() !== null);
  protected readonly made = computed<Made | undefined>(
    () => {
      const primed = this.primed.hasValue() ? this.primed.value() : undefined;
      const remade = this.remade();
      const linked = this.linked();
      return primed ? untracked(() => this.make(primed, remade, linked)) : undefined;
    },
    { equal: (a, b) => a?.key === b?.key },
  );

  protected readonly count = signal<RowCount | null>(null);
  /** Why the rows couldn't be fetched (but the condition's problems, said under it). */
  protected readonly problem = signal<Problem | null>(null);
  /** What is wrong with the condition, as the server placed it. */
  protected readonly whereProblems = signal<readonly WhereProblem[]>([]);
  protected readonly whereText = signal('');
  /** The condition the rows are filtered by. */
  protected readonly appliedWhere = signal<string | null>(null);
  protected readonly focused = signal<Focused | null>(null);
  /** Whether the inspector is shown: as the user last left it. */
  protected readonly inspecting = signal(
    readStored(this.storage, BrowseGrid.inspectorKey) !== 'hidden',
  );

  protected readonly primeProblem = computed(() => {
    const error = this.primed.error();
    const problem = error ? problemOf(error) : null;
    return problem && !isSessionProblem(problem) ? problem : null;
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
  protected readonly inspected = computed<Inspected | null>(() => {
    const focused = this.focused();
    const made = this.made();
    if (!focused || !made) {
      return null;
    }
    const { colId, row } = focused;
    const linked = made.linked.includes(colId) && cellLinkOf(made.schema, colId, row) !== null;
    const collection = made.schema.collections[indexOfCollectionColId(colId)];
    if (collection) {
      return { kind: 'collection', collection, row, linked };
    }
    const index = indexOfColId(colId);
    const column = made.schema.columns[index];
    if (!column) {
      return null;
    }
    const reference = referenceOf(made.schema, index);
    return { kind: 'column', column, index, row, reference, linked };
  });
  /** Whether the condition typed isn't the one the rows are filtered by. */
  protected readonly whereEdited = computed(
    () => normalWhere(this.whereText()) !== this.appliedWhere(),
  );
  protected readonly message = problemMessage;

  /** Cells' links: where they lead, as the page that has the grid says. */
  private readonly links: GridLinks = {
    href: (link) => {
      const to = untracked(this.linkTo);
      return to
        ? this.locationStrategy.prepareExternalUrl(
            this.router.serializeUrl(to(link.row, link.navigation)),
          )
        : '';
    },
    follow: (link) => this.followLink(link),
  };

  constructor() {
    // A grid made anew starts with nothing loaded, and nothing wrong.
    effect(() => {
      const made = this.made();
      untracked(() => {
        this.count.set(null);
        this.problem.set(null);
        this.whereProblems.set([]);
        this.focused.set(null);
        this.appliedWhere.set(made?.datasource.query.where ?? null);
        this.whereText.set(made?.datasource.query.where ?? '');
      });
    });
    // The address went elsewhere (back, forward, a link): the grid shows where.
    effect(() => {
      const crumb = this.crumb();
      untracked(() => this.follow(crumb));
    });
    // The catalog changed, but not the columns: the rows are asked for anew (and counted), from the page the
    // schema came with.
    effect(() => {
      const primed = this.primed.hasValue() ? this.primed.value() : undefined;
      untracked(() => {
        const live = this.current();
        if (primed && live) {
          this.ask(live, live.datasource.query, live.page, primed.page);
        }
      });
    });
    // Links lead elsewhere (the address changed): the cells with links say where.
    effect(() => {
      this.linkTo();
      untracked(() => {
        const live = this.current();
        const linked = this.made()?.linked ?? [];
        if (live && linked.length > 0) {
          live.api.refreshCells({ columns: [...linked], force: true });
        }
      });
    });
  }

  protected gridReady(event: GridReadyEvent<GridRow>, made: Made): void {
    this.live = {
      key: made.key,
      api: event.api,
      datasource: made.datasource,
      page: made.page,
      row: made.row,
    };
    // The grid sets its filters (asking for its first page again) before its page: the pages it asked for go
    // before they are fetched (see blockLoadDebounceMillis), and only the page shown is.
    event.api.purgeInfiniteCache();
    this.selectRow(this.live);
    if (this.problem() || this.whereProblems().length > 0) {
      this.showFailure(this.live);
    }
    if (this.refocus) {
      this.refocus = false;
      event.api.setFocusedHeader(colIdOf(0));
    }
    const crumb = untracked(this.crumb);
    if (JSON.stringify(crumb) !== JSON.stringify(made.from)) {
      // The address went elsewhere as the grid was made: the grid goes there.
      this.follow(crumb);
    } else if (this.differs(crumb)) {
      // The address said what the grid can't hold, or said it another way: it says what the grid shows.
      this.say();
    }
  }

  /** Sorted or filtered in the grid: the rows so, from their first page. */
  protected changed(): void {
    const live = this.current();
    const made = this.made();
    if (this.following || !live || !made) {
      return;
    }
    const query: BrowseQuery = {
      ...live.datasource.query,
      filters: filtersOf(live.api.getFilterModel(), made.schema.columns),
      sort: sortOf(sortModelOf(live.api), made.schema.columns),
    };
    if (!sameQuery(query, live.datasource.query)) {
      this.ask(live, query);
    }
  }

  /**
   * Another page shown: turned to, or the last when the address's page is past the rows' end. Not before a page
   * has come: a first page that failed shows no rows, and the address keeps its page.
   */
  protected paged(): void {
    const live = this.current();
    if (this.following || !live || live.datasource.count === null) {
      return;
    }
    const page = live.api.paginationGetCurrentPage();
    if (page !== live.page) {
      live.page = page;
      this.say();
    }
  }

  protected chosen(event: SelectionChangedEvent<GridRow>): void {
    const live = this.current();
    if (this.following || !live || event.source === 'api') {
      return;
    }
    const id = live.api.getSelectedRows()[0]?.id ?? null;
    const row = id === null ? null : keyOf(id);
    if (!sameRow(row, live.row)) {
      live.row = row;
      this.say();
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
    writeStored(this.storage, BrowseGrid.inspectorKey, this.inspecting() ? 'shown' : 'hidden');
  }

  protected typed(event: Event): void {
    this.whereText.set((event.target as HTMLInputElement).value);
  }

  protected applyWhere(event?: Event): void {
    event?.preventDefault();
    const live = this.current();
    if (live && this.whereEdited()) {
      this.ask(live, { ...live.datasource.query, where: normalWhere(this.whereText()) });
    }
  }

  /** Clears the condition (the rows filtered by none); the keyboard goes to the field, as the button goes. */
  protected clearWhere(): void {
    this.whereText.set('');
    if (this.appliedWhere() !== null) {
      this.applyWhere();
    } else {
      this.whereProblems.set([]);
    }
    this.whereField()?.nativeElement.focus();
  }

  protected retry(): void {
    const live = this.current();
    if (live) {
      this.ask(live, live.datasource.query, live.page);
    }
  }

  /** Whether the grid's pages are a datasource's: the grid shown's, or the one made last's, before it shows. */
  private shows(datasource: BrowseDatasource): boolean {
    const live = this.current();
    return live ? live.datasource === datasource : untracked(this.made)?.datasource === datasource;
  }

  /** The grid shown, if it is the one made last (one made before may still be going). */
  private current(): Live | null {
    const live = this.live;
    return live && live.key === untracked(this.made)?.key && !live.api.isDestroyed() ? live : null;
  }

  /** The schema of the source's rows, with their first page in the address's state (else the schema alone). */
  private prime(source: BrowseSource, crumb: BrowseCrumb): Observable<Primed> {
    const query: BrowseQuery = {
      source,
      filters: crumb.filters,
      where: crumb.where,
      sort: crumb.sort,
    };
    const limit = this.pageSize;
    const asked = (grid: object, includeCount: boolean) =>
      this.api.post('/api/browse/page', {
        body: { source, grid, includeSchema: true, includeCount },
      });
    const primedOf = (page: BrowsePage, first: PrimedPage | null): Primed => ({
      columns: page.schema?.columns ?? [],
      references: page.schema?.references ?? [],
      collections: page.schema?.collections ?? [],
      keyed: (page.schema?.key?.length ?? 0) > 0,
      page: first,
    });
    const stated =
      crumb.filters.length > 0 || crumb.where !== null || crumb.sort.length > 0 || crumb.page > 0;
    return asked(gridOf(query, crumb.page * limit, limit), true).pipe(
      map((page) => primedOf(page, { query, page, limit })),
      catchError((error: unknown) => {
        // The address's state can't be asked for (a column gone, a value that isn't one, a condition that's
        // wrong): the schema alone, and the grid's own asks say what is wrong.
        const status = problemOf(error).status;
        if (!stated || (status !== 400 && status !== 422)) {
          return throwError(() => error);
        }
        return asked({ limit: 1 }, false).pipe(map((page) => primedOf(page, null)));
      }),
    );
  }

  private make(primed: Primed, remade: number, linked: boolean): Made {
    const crumb = this.crumb();
    const columns = primed.columns;
    const schema: LinkSchema = {
      columns,
      references: primed.references,
      collections: primed.collections,
    };
    const links = linked ? this.links : null;
    const filters = filterModelOf(crumb.filters, columns);
    const sort = columnStateOf(crumb.sort, columns);
    const sortModel: SortModelItem[] = sort.state.map((state) => ({
      colId: state.colId,
      sort: state.sort === 'desc' ? 'desc' : 'asc',
    }));
    const query: BrowseQuery = {
      source: this.source(),
      filters: filtersOf(filters.model, columns),
      where: crumb.where,
      sort: sortOf(sortModel, columns),
    };
    const datasource = new BrowseDatasource(this.api, query, this.events(), primed.page);
    const options: GridOptions<GridRow> = {
      theme: gridTheme,
      loadThemeGoogleFonts: false,
      columnDefs: linkedColumnDefsOf(schema, links, primed.keyed),
      defaultColDef: { resizable: true, minWidth: 72 },
      rowModelType: 'infinite',
      datasource,
      pagination: true,
      paginationPageSize: this.pageSize,
      paginationPageSizeSelector: false,
      cacheBlockSize: this.pageSize,
      maxBlocksInCache: 10,
      // Pages are fetched a moment after the grid asks for them: a grid's first asks come before its filters and
      // sort are set (which ask again), and asking for a query anew goes to its first page first; by then, the
      // pages asked for before are let go.
      blockLoadDebounceMillis: 10,
      infiniteInitialRowCount: (crumb.page + 1) * this.pageSize,
      getRowId: primed.keyed ? ({ data }) => data.id ?? '' : undefined,
      rowSelection: {
        mode: 'singleRow',
        checkboxes: false,
        enableClickSelection: true,
        isRowSelectable: (node) => primed.keyed && !!node.data?.id,
      },
      localeText: {
        noRowsToShow: 'No rows',
        noMatchingRows: 'No rows match the filters',
        loadingOoo: 'Loading…',
      },
      ensureDomOrder: true,
      enableCellTextSelection: true,
      tooltipShowDelay: 600,
      initialState: {
        filter: { filterModel: filters.model },
        sort: { sortModel },
        pagination: { page: crumb.page },
      },
    };
    return {
      key: `${this.sourceKey()}|${JSON.stringify(schema)}|${linked}|${remade}`,
      schema,
      linked: links ? linkedColIdsOf(schema, primed.keyed) : [],
      options,
      datasource,
      page: crumb.page,
      row: crumb.row,
      left: [...filters.left, ...sort.left],
      from: crumb,
    };
  }

  /** Shows what the address says: the page or row chosen in place, else a grid made for it. */
  private follow(crumb: BrowseCrumb): void {
    const live = this.current();
    const made = this.made();
    if (!live || !made || !this.differs(crumb)) {
      return;
    }
    const query: BrowseQuery = {
      ...live.datasource.query,
      filters: filtersOf(
        filterModelOf(crumb.filters, made.schema.columns).model,
        made.schema.columns,
      ),
      where: crumb.where,
      sort: sortOf(
        columnStateOf(crumb.sort, made.schema.columns).state.map((state) => ({
          colId: state.colId,
          sort: state.sort === 'desc' ? 'desc' : 'asc',
        })),
        made.schema.columns,
      ),
    };
    if (
      !sameQuery(query, live.datasource.query) ||
      crumb.page >= live.api.paginationGetTotalPages()
    ) {
      this.refocus =
        this.host.nativeElement
          .querySelector('ag-grid-angular')
          ?.contains(this.host.nativeElement.ownerDocument.activeElement) ?? false;
      this.remade.update((count) => count + 1);
      return;
    }
    this.following = true;
    try {
      if (crumb.page !== live.page) {
        live.page = crumb.page;
        live.api.paginationGoToPage(crumb.page);
      }
      if (!sameRow(crumb.row, live.row)) {
        live.row = crumb.row;
        this.selectRow(live);
      }
    } finally {
      this.following = false;
    }
  }

  /** Whether the address says other than what the grid shows. */
  private differs(crumb: BrowseCrumb): boolean {
    const live = this.current();
    if (!live) {
      return false;
    }
    const query = live.datasource.query;
    return (
      !sameQuery(
        { ...query, filters: crumb.filters, where: crumb.where, sort: crumb.sort },
        query,
      ) ||
      crumb.page !== live.page ||
      !sameRow(crumb.row, live.row)
    );
  }

  /**
   * Asks for the rows of a query anew: another's from their first page, or the same's again from a page (with the
   * page fetched ahead, when there is one).
   */
  private ask(live: Live, query: BrowseQuery, page = 0, primed: PrimedPage | null = null): void {
    const said = live.page !== page || !sameQuery(query, live.datasource.query);
    live.datasource.retire();
    live.datasource = new BrowseDatasource(this.api, query, this.events(), primed);
    live.page = page;
    this.count.set(null);
    this.problem.set(null);
    this.whereProblems.set([]);
    this.appliedWhere.set(query.where);
    this.following = true;
    try {
      clearFailure(live.api);
      live.api.setGridOption('datasource', live.datasource);
      // A new cache starts with the rows the grid was made with (to reach the address's page then): as many as
      // reach this page, and more.
      live.api.setRowCount((page + 1) * this.pageSize, false);
      live.api.paginationGoToPage(page);
      // The pages asked for on the way go before they are fetched (see blockLoadDebounceMillis).
      live.api.purgeInfiniteCache();
    } finally {
      this.following = false;
    }
    if (said) {
      this.say();
    }
  }

  private events(): DatasourceEvents {
    return {
      loaded: (datasource, count) => {
        if (!this.shows(datasource)) {
          return;
        }
        this.count.set(count);
        this.problem.set(null);
        const live = this.current();
        if (live) {
          clearFailure(live.api);
          this.selectRow(live);
          this.refreshFocused(live);
        }
      },
      pastEnd: (datasource) => {
        const live = this.current();
        if (live && live.datasource === datasource) {
          // Where the rows end isn't known: their first page, counted anew.
          this.ask(live, datasource.query);
        }
      },
      failed: (datasource, problem) => {
        if (!this.shows(datasource) || isSessionProblem(problem)) {
          return;
        }
        const where = whereProblemsOf(problem);
        if (where.length > 0) {
          this.whereProblems.set(where);
        } else {
          this.problem.set(problem);
        }
        const live = this.current();
        if (live) {
          this.showFailure(live);
        }
      },
    };
  }

  /** The rows couldn't be read: the grid says so over them (why is said above it). */
  private showFailure(live: Live): void {
    live.api.setGridOption('overlayComponentParams', {
      noRows: {
        overlayText:
          this.whereProblems().length > 0
            ? "The condition can't be used"
            : "Couldn't read the rows",
      },
    });
    live.api.setGridOption('activeOverlay', 'agNoRowsOverlay');
  }

  /** Chooses in the grid the row the address chose, when it is among those loaded. */
  private selectRow(live: Live): void {
    const wanted = live.row === null ? null : JSON.stringify(live.row);
    const selected = live.api.getSelectedRows()[0]?.id;
    if ((selected ? JSON.stringify(keyOf(selected)) : null) === wanted) {
      return;
    }
    this.following = true;
    try {
      let found = false;
      live.api.forEachNode((node) => {
        if (wanted !== null && node.data?.id && JSON.stringify(keyOf(node.data.id)) === wanted) {
          node.setSelected(true, true, 'api');
          found = true;
        }
      });
      // The row the address chose isn't among those loaded (or none is): none is chosen in the grid.
      if (!found && selected) {
        live.api.deselectAll('all', 'api');
      }
    } finally {
      this.following = false;
    }
  }

  private refreshFocused(live: Live): void {
    const cell = live.api.getFocusedCell();
    if (!cell || cell.rowPinned) {
      return;
    }
    this.focused.set({
      colId: cell.column.getColId(),
      row: live.api.getDisplayedRowAtIndex(cell.rowIndex)?.data ?? null,
    });
  }

  /** Follows a navigation from a row, where the page that has the grid says it leads. */
  private followLink(link: CellLink): void {
    const to = untracked(this.linkTo);
    if (to) {
      void this.router.navigateByUrl(to(link.row, link.navigation));
    }
  }

  /** Says the grid's state, for the address to follow. */
  private say(): void {
    const live = this.current();
    if (!live) {
      return;
    }
    const query = live.datasource.query;
    this.crumbChange.emit({
      ...this.crumb(),
      filters: query.filters,
      where: query.where,
      sort: query.sort,
      page: live.page,
      row: live.row,
    });
  }
}

/** The problems a problem says the condition has: diagnostics placed in it, or what is wrong with its field. */
export function whereProblemsOf(problem: Problem): WhereProblem[] {
  const field = problem.errors?.['grid.where'];
  if (field) {
    return field.map((message) => ({ message, start: null, end: null }));
  }
  const body = problem.body;
  if (body?.['field'] !== 'grid.where' || !Array.isArray(body['diagnostics'])) {
    return [];
  }
  return (body['diagnostics'] as Record<string, unknown>[]).map((diagnostic) => ({
    message: String(diagnostic['message'] ?? ''),
    start: typeof diagnostic['start'] === 'number' ? diagnostic['start'] : null,
    end: typeof diagnostic['end'] === 'number' ? diagnostic['end'] : null,
  }));
}

/** The grid's overlay for rows that couldn't be read goes. */
function clearFailure(api: GridApi<GridRow>): void {
  if (api.getGridOption('activeOverlay')) {
    api.setGridOption('activeOverlay', undefined);
    api.setGridOption('overlayComponentParams', undefined);
  }
}

/** A condition as typed, or null for none. */
function normalWhere(text: string): string | null {
  return text.trim() === '' ? null : text;
}

function gridOf(query: BrowseQuery, offset: number, limit: number) {
  return {
    filters: query.filters.map((filter) => ({ ...filter, conditions: [...filter.conditions] })),
    where: query.where,
    sort: [...query.sort],
    offset,
    limit,
  };
}

function sameRow(a: readonly string[] | null, b: readonly string[] | null): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function sourceKeyOf(source: BrowseSource): string {
  return JSON.stringify([source.entity ?? null, source.from ?? null, source.navigation ?? null]);
}

function sortModelOf(api: GridApi<GridRow>): SortModelItem[] {
  return api
    .getColumnState()
    .filter((state) => state.sort)
    .sort((a, b) => (a.sortIndex ?? 0) - (b.sortIndex ?? 0))
    .map((state) => ({ colId: state.colId, sort: state.sort === 'desc' ? 'desc' : 'asc' }));
}
