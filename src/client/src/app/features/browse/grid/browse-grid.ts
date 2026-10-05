import {
  Component,
  ElementRef,
  Injector,
  LOCALE_ID,
  afterNextRender,
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
import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DOCUMENT, LocationStrategy } from '@angular/common';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatTooltip } from '@angular/material/tooltip';
import { Router, type UrlTree } from '@angular/router';
import { AgGridAngular } from 'ag-grid-angular';
import {
  BigIntFilterModule,
  type CellDoubleClickedEvent,
  type CellFocusedEvent,
  type CellKeyDownEvent,
  type FullWidthCellKeyDownEvent,
  CellStyleModule,
  ColumnApiModule,
  DateEditorModule,
  DateFilterModule,
  type GridApi,
  type GridOptions,
  type GridReadyEvent,
  GridStateModule,
  type IRowNode,
  InfiniteRowModelModule,
  LargeTextEditorModule,
  LocaleModule,
  type Module,
  NumberFilterModule,
  PaginationModule,
  PinnedRowModule,
  RenderApiModule,
  RowApiModule,
  type RowClassRules,
  RowSelectionModule,
  RowStyleModule,
  ScrollApiModule,
  SelectEditorModule,
  type SelectionChangedEvent,
  type SortModelItem,
  TextEditorModule,
  TextFilterModule,
  TooltipModule,
  ValidationModule,
} from 'ag-grid-community';
import { type Observable, catchError, firstValueFrom, map, throwError } from 'rxjs';
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
import {
  type ChangeRow,
  type EntityChanges,
  type Outcome,
  PendingChanges,
  type PendingChange,
  type Values,
} from '../../../core/changes/pending-changes';
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
  type GridColumn,
  type GridRow,
  colIdOf,
  columnStateOf,
  filterModelOf,
  filtersOf,
  indexOfColId,
  indexOfColumn,
  keyOf,
  sortOf,
} from './grid-columns';
import {
  type Capabilities,
  type EditRow,
  type GridEdits,
  cellEditable,
  conflicts,
  editedColumnDefsOf,
  hasValue,
  isNew,
  originalOf,
  referenceChangesOf,
  referenceEditable,
  rowClassRulesOf,
  rowStateOf,
  shownValue,
  stateColId,
  takesChanges,
} from './grid-edits';
import { GridInspector, type Inspected } from './grid-inspector';
import {
  type CellLink,
  type GridLinks,
  type LinkSchema,
  cellLinkOf,
  displayOf,
  displayText,
  indexOfCollectionColId,
  linkedColIdsOf,
  linkedColumnDefsOf,
  referenceOf,
} from './grid-links';
import { BROWSE_PAGE_SIZE, inspectorKey } from './grid-settings';
import { gridTheme } from './grid-theme';
import type { NavPicked, NavPickerData } from './nav-picker';

export { BROWSE_PAGE_SIZE };

/**
 * What the grid needs of the grid's library: the infinite row model, pages, filters, sorting, choosing rows,
 * refreshing cells (their links and changes), editing cells, rows' classes and new rows pinned at the top.
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
  RowStyleModule,
  ColumnApiModule,
  GridStateModule,
  LocaleModule,
  TooltipModule,
  TextEditorModule,
  LargeTextEditorModule,
  SelectEditorModule,
  DateEditorModule,
  PinnedRowModule,
  ScrollApiModule,
  ...(isDevMode() ? [ValidationModule] : []),
];

/** Where a navigation followed from a row leads (its key, its values as text). */
export type LinkTo = (row: readonly string[], navigation: string) => UrlTree;

/** What new rows start with: values by column, and display values by navigation (a navigation's crumb's row). */
export interface InsertDefaults {
  readonly values: Values;
  readonly display: Values;
}

/** A row chosen in the grid, with the columns its values are of. */
export interface ChosenRow {
  readonly row: GridRow;
  readonly columns: readonly GridColumn[];
}

/** A change that couldn't be made, and why. */
interface EditProblem {
  readonly title: string;
  readonly reasons: readonly string[];
}

/** The schema of a source's rows, and their first page in the address's state when that could be asked for. */
interface Primed extends LinkSchema {
  /** The entity the rows are of, as changes name it. */
  readonly entity: string;
  readonly capabilities: Capabilities;
  readonly keyed: boolean;
  readonly page: PrimedPage | null;
}

/** A grid made for a schema and a state: made again for other columns, or an address the grid can't follow. */
interface Made {
  readonly key: string;
  readonly entity: string;
  /** The rows' columns, what they refer to and what refers to them. */
  readonly schema: LinkSchema;
  /** The ids of the columns with links. */
  readonly linked: readonly string[];
  /** Changing the rows, when they may be. */
  readonly edits: GridEdits | null;
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
  /** Whether rows were loaded (a page answered). */
  loaded: boolean;
  /** Whether the keyboard was placed (`focusChosen`), or left where the user had put it: done once. */
  keyboardPlaced: boolean;
}

/** The cell the keyboard is on (or clicked), for the inspector and the row's actions. */
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

const noChanges: EntityChanges = { rows: new Map(), inserts: [] };

/** The longest display value sent with a reference chosen, as JSON (the server keeps 1,000 characters of it). */
const displayLength = 1000;

/**
 * The rows of a source in a grid, a page at a time from the API (AG Grid's infinite row model, with pages): filtered
 * by its columns' filters and by a condition in the query language, sorted by its columns' headers, a row chosen by
 * clicking it. The grid's state is the address's (`crumb`): what changes in the grid is said (`crumbChange`) for the
 * address to follow, and an address gone elsewhere (back, forward, a link) is shown. Beside it, the inspector says
 * what the cell the keyboard is on holds, and where its column's values come from.
 *
 * With `editing`, for those who change data, the rows may be changed as their entity allows: cells edited, rows
 * deleted, new rows added (pinned at the top), each a pending change (`PendingChanges`) shown in the cells until it
 * is committed or reverted.
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
  static readonly inspectorKey = inspectorKey;

  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly locationStrategy = inject(LocationStrategy);
  private readonly locale = inject(LOCALE_ID);
  private readonly pageSize = inject(BROWSE_PAGE_SIZE);
  private readonly storage = storageOf(inject(DOCUMENT));
  private readonly changes = inject(PendingChanges);
  private readonly dialog = inject(MatDialog);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly injector = inject(Injector);
  protected readonly modules = modules;
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly whereField = viewChild<ElementRef<HTMLInputElement>>('whereField');
  private live: Live | null = null;
  /** Set while the grid is made to show what the address says: its events then aren't the user's. */
  private following = false;
  /** Set when a grid with the keyboard in it is made anew: the keyboard goes to the new one. */
  private refocus = false;
  /** The changes of the grid's entity, as its cells show them. */
  private entityChanges: EntityChanges = noChanges;
  /** New rows as the grid holds them, by their temporary ids (kept, so the grid changes them in place). */
  private newRows = new Map<string, EditRow>();
  /** Set while the picker is open (or loading): it opens once at a time. */
  private picking = false;
  /** The grid whose rows wait for a cell's edit to end, to be read again. */
  private waitingToRead: Live | null = null;

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
  /** Whether the rows may be changed here (for those who change data, as the entity allows). */
  readonly editing = input(false);
  /** What new rows start with. */
  readonly insertDefaults = input<InsertDefaults | null>(null);
  /** Whether the inspector may be shown beside the rows. */
  readonly inspectable = input(true);
  /**
   * Whether the keyboard goes, once, to the row the crumb chooses, or to the condition's field when the rows first
   * loaded don't hold it (a picker opened at the row): as they load, or as this becomes true with them loaded (once
   * the picker's dialog has taken the keyboard); only while the keyboard isn't elsewhere (the user may have moved it).
   */
  readonly focusChosen = input(false);
  /** The row chosen (clicked), or none. */
  readonly rowChosen = output<ChosenRow | null>();
  /** A row double-clicked, or Enter on it, in a grid without links or changes (a picker's). */
  readonly rowActivated = output<ChosenRow>();

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
  /** Whether the rows may be changed: here, and by the user. */
  private readonly editable = computed(() => this.editing() && this.changes.enabled());
  protected readonly made = computed<Made | undefined>(
    () => {
      const primed = this.primed.hasValue() ? this.primed.value() : undefined;
      const remade = this.remade();
      const linked = this.linked();
      const editable = this.editable();
      return primed ? untracked(() => this.make(primed, remade, linked, editable)) : undefined;
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
  /** A change that couldn't be made, and why. */
  protected readonly editProblem = signal<EditProblem | null>(null);

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
  /**
   * The changes the grid shows: its entity's, with the new rows of the crumb's row in a collection's crumb (those
   * that start as new rows do there), not those of other rows; the same while the entity's are.
   */
  private readonly shownChanges = computed(
    () => {
      const made = this.made();
      if (!made?.edits) {
        return noChanges;
      }
      const changes = this.changes.of(made.entity);
      const defaults = this.insertDefaults()?.values;
      return defaults
        ? {
            rows: changes.rows,
            inserts: changes.inserts.filter((change) => startsAs(change, defaults)),
          }
        : changes;
    },
    { equal: sameChanges },
  );
  /** The change of the row the keyboard is on (or clicked). */
  private readonly focusedChange = computed(() => {
    const row = this.focused()?.row ?? null;
    return row ? changeIn(this.shownChanges(), row) : null;
  });
  /** What can be done with the row the keyboard is on: delete it (or restore it, or drop it), revert its change. */
  protected readonly rowActions = computed(() => {
    const made = this.made();
    const row = this.focused()?.row ?? null;
    if (!made?.edits || !row) {
      return null;
    }
    const change = this.focusedChange();
    const state = rowStateOf(row, change);
    const can = made.edits.capabilities;
    const canDelete = state === 'new' || state === 'deleted' || (can.canDelete && !!row.id);
    return {
      state,
      /** The row they act on, as they say it. */
      row: rowLabelOf(row, this.shownChanges()),
      deleteLabel:
        state === 'new'
          ? 'Drop the new row'
          : state === 'deleted'
            ? 'Restore the row'
            : 'Delete the row',
      canDelete,
      deleteWhyNot: canDelete
        ? null
        : !row.id
          ? "Rows without a key can't be deleted"
          : (can.changeReason ?? "The rows can't be deleted"),
      canRevert: state === 'changed' || state === 'deleted',
    };
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
    const change = this.focusedChange();
    const edits = made.edits;
    const changed = row !== null && hasValue(change, column.name);
    const referenceChanged =
      reference !== null &&
      reference.columns.some((at) => hasValue(change, made.schema.columns[at].name));
    const pendingDisplay = change?.display[reference?.navigation ?? ''];
    return {
      kind: 'column',
      column,
      index,
      row,
      reference,
      linked: linked && !referenceChanged && !isNew(row),
      value: row ? shownValue(column, index, row, change) : undefined,
      display: referenceChanged
        ? pendingDisplay === null || pendingDisplay === undefined
          ? null
          : displayText(pendingDisplay)
        : displayOf(column, row),
      edit:
        edits && row
          ? {
              state: rowStateOf(row, change),
              changed,
              original: changed ? originalOf(change, column.name) : undefined,
              conflict: conflicts(column, index, row, change),
              issues: this.changes
                .issuesOf(change)
                .filter(
                  (issue) =>
                    issue.column === null ||
                    issue.column.toLowerCase() === column.name.toLowerCase(),
                )
                .map((issue) => issue.message),
              editable:
                reference !== null
                  ? referenceEditable(made.schema, reference, row, edits)
                  : cellEditable(column, row, edits),
            }
          : null,
    };
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
        this.editProblem.set(null);
        this.appliedWhere.set(made?.datasource.query.where ?? null);
        this.whereText.set(made?.datasource.query.where ?? '');
      });
    });
    // The address went elsewhere (back, forward, a link): the grid shows where.
    effect(() => {
      const crumb = this.crumb();
      untracked(() => this.follow(crumb));
    });
    // The rows loaded before the keyboard may go to them.
    effect(() => {
      if (this.focusChosen()) {
        untracked(() => {
          const live = this.current();
          if (live) {
            this.placeKeyboard(live);
          }
        });
      }
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
    // The entity's changes changed (or what a preview found wrong with them): the cells show them.
    effect(() => {
      const changes = this.shownChanges();
      this.changes.issues();
      untracked(() => this.showChanges(changes));
    });
    // Changes were committed (here or in another tab): the rows are read again, from the page shown (a grid
    // being made has none yet).
    effect(() => {
      this.changes.commits();
      untracked(() => {
        const live = this.current();
        if (live) {
          this.readAgain(live);
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
      loaded: false,
      keyboardPlaced: false,
    };
    // The grid sets its filters (asking for its first page again) before its page: the pages it asked for go
    // before they are fetched (see blockLoadDebounceMillis), and only the page shown is.
    event.api.purgeInfiniteCache();
    this.showChanges(this.entityChanges);
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
    const made = this.made();
    if (!live || !made) {
      return;
    }
    const selected = live.api.getSelectedRows()[0] ?? null;
    this.rowChosen.emit(selected ? { row: selected, columns: made.schema.columns } : null);
    if (this.following || event.source === 'api') {
      return;
    }
    const id = selected?.id ?? null;
    const row = id === null ? null : keyOf(id);
    if (!sameRow(row, live.row)) {
      live.row = row;
      this.say();
    }
  }

  protected cellFocused(event: CellFocusedEvent<GridRow>): void {
    const live = this.current();
    const colId = typeof event.column === 'string' ? event.column : event.column?.getColId();
    if (!live || !colId || event.rowIndex === null) {
      return;
    }
    this.focused.set({ colId, row: rowAt(live.api, event.rowIndex, event.rowPinned) });
  }

  /** A double click on a reference's cell chooses the row it refers to; in a picker's grid, it chooses the row. */
  protected doubleClicked(event: CellDoubleClickedEvent<GridRow>): void {
    const made = this.made();
    const row = event.data;
    if (!made || !row) {
      return;
    }
    if (made.edits) {
      const reference = referenceOf(made.schema, indexOfColId(event.column.getColId()));
      if (reference && referenceEditable(made.schema, reference, row, made.edits)) {
        made.edits.pick(row, made.schema.references.indexOf(reference));
      }
      return;
    }
    if (!this.linked() && row.id) {
      this.rowActivated.emit({ row, columns: made.schema.columns });
    }
  }

  /** A cell's edit ended: rows waiting for it are read again. */
  protected editingStopped(): void {
    const live = this.current();
    if (live && this.waitingToRead === live) {
      this.readAgain(live);
    }
  }

  /** Enter on a row of a picker's grid chooses it. */
  protected keyDown(event: CellKeyDownEvent<GridRow> | FullWidthCellKeyDownEvent<GridRow>): void {
    const made = this.made();
    const key = (event.event as KeyboardEvent | null)?.key;
    if (made && !made.edits && !this.linked() && key === 'Enter' && event.data?.id) {
      this.rowActivated.emit({ row: event.data, columns: made.schema.columns });
    }
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

  /**
   * A new row, with what new rows start with, at the top of the grid: the keyboard goes to its first cell that
   * takes a value, which is edited.
   */
  protected addRow(): void {
    const made = this.made();
    if (!made?.edits) {
      return;
    }
    const defaults = this.insertDefaults();
    const { tempId, done } = this.changes.insert(
      made.entity,
      defaults?.values ?? {},
      defaults?.display ?? {},
    );
    this.editProblem.set(null);
    void done.then((outcome) => this.after(outcome, "Couldn't add the row"));
    this.announce('A new row, at the top');
    this.showChanges(this.changes.of(made.entity));
    const live = this.current();
    const index = this.entityChanges.inserts.findIndex((change) => change.tempId === tempId);
    // The first column a value is needed for, else the first that takes one (but the key's, often generated).
    const editable = (column: GridColumn) => column.insert !== 'never' && column.reference === null;
    const needed = made.schema.columns.findIndex(
      (column) => editable(column) && column.insert === 'required',
    );
    const first =
      needed >= 0
        ? needed
        : made.schema.columns.findIndex((column) => editable(column) && !column.isKey);
    if (live && index >= 0) {
      const colKey = colIdOf(Math.max(first, 0));
      live.api.ensureColumnVisible(colKey);
      live.api.setFocusedCell(index, colKey, 'top');
      if (first >= 0) {
        live.api.startEditingCell({ rowIndex: index, colKey, rowPinned: 'top' });
      }
    }
  }

  /** Deletes the row the keyboard is on (or clicked), restores it, or drops it when it is new. */
  protected deleteFocused(): void {
    const row = this.focused()?.row;
    const edits = this.made()?.edits;
    if (row && edits) {
      edits.toggleDelete(row);
    }
  }

  /** Reverts the change of the row the keyboard is on (or clicked). */
  protected revertFocused(): void {
    const row = this.focused()?.row;
    const made = this.made();
    const target = row ? changeRowOf(row) : null;
    if (made && target && row) {
      this.editProblem.set(null);
      void this.changes
        .revert(made.entity, target)
        .then((outcome) => this.after(outcome, "Couldn't revert the row"));
      this.announce(`The changes of ${rowLabelOf(row, this.entityChanges)} reverted`);
    }
  }

  /** Reverts the change of the cell the inspector shows. */
  protected revertInspected(): void {
    const focused = this.focused();
    const edits = this.made()?.edits;
    if (focused?.row && edits) {
      edits.revertCell(focused.row, indexOfColId(focused.colId));
      this.keepKeyboard();
    }
  }

  /**
   * The keyboard goes back to the grid's cell when the button it was on goes (reverted, there is nothing to revert
   * any more), rather than to the page.
   */
  private keepKeyboard(): void {
    const document = this.host.nativeElement.ownerDocument;
    afterNextRender(
      () => {
        const active = document.activeElement;
        const lost = !active || active === document.body || !active.isConnected;
        const cell = this.current()?.api.getFocusedCell();
        if (lost && cell) {
          this.current()?.api.setFocusedCell(cell.rowIndex, cell.column, cell.rowPinned);
        }
      },
      { injector: this.injector },
    );
  }

  /** Whether the grid's pages are a datasource's: the grid shown's, or the one made last's, before it shows. */
  private shows(datasource: object): boolean {
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
      entity: page.schema?.entity ?? page.entity,
      capabilities: page.schema?.capabilities ?? {
        canInsert: false,
        canUpdate: false,
        canDelete: false,
      },
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

  private make(primed: Primed, remade: number, linked: boolean, editable: boolean): Made {
    const crumb = this.crumb();
    const columns = primed.columns;
    const schema: LinkSchema = {
      columns,
      references: primed.references,
      collections: primed.collections,
    };
    const links = linked ? this.links : null;
    const edits =
      editable && takesChanges(primed.capabilities) ? this.gridEdits(primed, schema) : null;
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
    const linkedDefs = linkedColumnDefsOf(
      schema,
      links,
      primed.keyed,
      edits ? referenceChangesOf(schema, edits) : null,
    );
    const options: GridOptions<GridRow> = {
      theme: gridTheme,
      loadThemeGoogleFonts: false,
      columnDefs: edits ? editedColumnDefsOf(schema, linkedDefs, edits) : linkedDefs,
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
      // New rows (pinned at the top) by their temporary ids, so they are changed in place.
      getRowId: primed.keyed
        ? ({ data }) => (isNew(data) ? `new:${data.tempId}` : (data.id ?? ''))
        : undefined,
      rowSelection: {
        mode: 'singleRow',
        checkboxes: false,
        enableClickSelection: true,
        isRowSelectable: (node) => primed.keyed && !!node.data?.id && !node.rowPinned,
      },
      rowClassRules: edits ? (rowClassRulesOf(edits) as RowClassRules<GridRow>) : undefined,
      stopEditingWhenCellsLoseFocus: true,
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
        // An initial state unpins the columns it doesn't pin.
        ...(edits ? { columnPinning: { leftColIds: [stateColId], rightColIds: [] } } : {}),
      },
    };
    return {
      key: `${this.sourceKey()}|${JSON.stringify(schema)}|${linked}|${edits !== null}|${remade}`,
      entity: primed.entity,
      schema,
      linked: links ? linkedColIdsOf(schema, primed.keyed) : [],
      edits,
      options,
      datasource,
      page: crumb.page,
      row: crumb.row,
      left: [...filters.left, ...sort.left],
      from: crumb,
    };
  }

  /** Changing the rows of an entity, through the user's pending changes. */
  private gridEdits(primed: Primed, schema: LinkSchema): GridEdits {
    const entity = primed.entity;
    const capabilities = primed.capabilities;
    const edits: GridEdits = {
      capabilities,
      // The store's, as they are (the cells may be drawn before the grid follows them).
      changeOf: (row) => untracked(() => changeIn(this.changes.of(entity), row)),
      issuesOf: (row) =>
        untracked(() => this.changes.issuesOf(changeIn(this.changes.of(entity), row))),
      set: (row, values, display = {}) => this.setValues(entity, schema, row, values, display),
      refused: (_, column, reason) =>
        this.editProblem.set({ title: `Couldn't change ${column.name}`, reasons: [reason] }),
      pick: (row, at) => void this.pick(entity, schema, edits, row, at),
      toggleDelete: (row) => this.toggleDelete(entity, schema, capabilities, row),
      revertCell: (row, index) => this.revertCell(entity, schema, row, index),
    };
    return edits;
  }

  /** Sets columns' values of a row (with the values it had, for those first changed). */
  private setValues(
    entity: string,
    schema: LinkSchema,
    row: GridRow,
    values: ReadonlyMap<number, unknown>,
    display: Values,
  ): void {
    const target = changeRowOf(row);
    if (!target) {
      return;
    }
    const named: Record<string, unknown> = {};
    const original: Record<string, unknown> = {};
    for (const [index, value] of values) {
      const column = schema.columns[index];
      named[column.name] = value;
      original[column.name] = row.v[index];
    }
    this.editProblem.set(null);
    void this.changes
      // A new row has no originals (the store sends them for rows that are there).
      .set(entity, target, named, original, display)
      .then((outcome) => this.after(outcome, "Couldn't change the row"));
  }

  private toggleDelete(
    entity: string,
    schema: LinkSchema,
    capabilities: Capabilities,
    row: GridRow,
  ): void {
    const target = changeRowOf(row);
    if (!target) {
      return;
    }
    this.editProblem.set(null);
    let done: Promise<Outcome>;
    if (isNew(row)) {
      this.announce(`${capital(rowLabelOf(row, this.entityChanges))} dropped`);
      done = this.changes.delete(entity, target);
    } else if (changeIn(this.changes.of(entity), row)?.kind === 'delete') {
      done = this.changes.revert(entity, target);
      this.announce(`${capital(rowLabelOf(row, this.entityChanges))} restored`);
    } else if (!capabilities.canDelete) {
      this.editProblem.set({
        title: "Couldn't delete the row",
        reasons: [capabilities.changeReason ?? "The entity's rows can't be deleted"],
      });
      return;
    } else {
      done = this.changes.delete(entity, target, originalsOf(schema, row));
      this.announce(`${capital(rowLabelOf(row, this.entityChanges))} to be deleted`);
    }
    void done.then((outcome) => this.after(outcome, "Couldn't change the row"));
  }

  /** Reverts a cell's change (all a reference's columns'), or restores its row when it is to be deleted. */
  private revertCell(entity: string, schema: LinkSchema, row: GridRow, index: number): void {
    const change = changeIn(this.changes.of(entity), row);
    const target = changeRowOf(row);
    if (!change || !target) {
      return;
    }
    let done: Promise<Outcome>;
    if (change.kind === 'delete') {
      done = this.changes.revert(entity, target);
      this.announce(`${capital(rowLabelOf(row, this.entityChanges))} restored`);
    } else {
      const reference = referenceOf(schema, index);
      const names = (reference ? reference.columns : [index])
        .map((at) => schema.columns[at]?.name)
        .filter((name): name is string => name !== undefined && hasValue(change, name));
      if (names.length === 0) {
        return;
      }
      done = this.changes.revert(entity, target, names);
      this.announce(`${names.join(', ')} of ${rowLabelOf(row, this.entityChanges)} reverted`);
    }
    this.editProblem.set(null);
    void done.then((outcome) => this.after(outcome, "Couldn't revert the change"));
  }

  /** Chooses the row a reference refers to, in the picker: its columns are set to the target's values. */
  private async pick(
    entity: string,
    schema: LinkSchema,
    edits: GridEdits,
    row: GridRow,
    at: number,
  ): Promise<void> {
    const reference = schema.references[at];
    if (!reference || this.picking) {
      return;
    }
    const nullable = reference.columns.every((index) => schema.columns[index].type.nullable);
    // What it refers to now, by the target's columns, for the picker to open at that row: none when a column is
    // NULL or a new row's default. (The page shows all its columns: references it doesn't aren't picked.)
    const change = edits.changeOf(row);
    const held = reference.columns.map((index) =>
      shownValue(schema.columns[index], index, row, change),
    );
    const refersTo = held.every((value) => value !== null && value !== undefined) ? held : null;
    let picked: NavPicked | undefined;
    this.picking = true;
    try {
      // The picker is a grid of its own, loaded when first wanted.
      const { NavPicker } = await import('./nav-picker');
      picked = await firstValueFrom(
        this.dialog
          .open<InstanceType<typeof NavPicker>, NavPickerData, NavPicked>(NavPicker, {
            data: { entity, reference, nullable, values: refersTo },
            // At a row, the keyboard goes to it (the dialog takes it first, and gives it up): not to the first field.
            autoFocus: refersTo ? 'dialog' : 'first-tabbable',
            width: '90vw',
            maxWidth: '1200px',
          })
          .afterClosed(),
      );
    } finally {
      this.picking = false;
    }
    if (!picked) {
      return;
    }
    if ('none' in picked) {
      // What was shown for the row it referred to goes too.
      edits.set(row, new Map(reference.columns.map((index) => [index, null])), {
        [reference.navigation]: null,
      });
      this.announce(
        `${reference.navigation} of ${rowLabelOf(row, this.entityChanges)} refers to no row`,
      );
      return;
    }
    const values = new Map<number, unknown>();
    for (const [place, index] of reference.columns.entries()) {
      const target = reference.targetColumns[place];
      const found = target === undefined ? -1 : indexOfColumn(picked.columns, target);
      if (found < 0) {
        edits.refused(
          row,
          schema.columns[index],
          `The rows chosen from have no ${target ?? 'such'} column`,
        );
        return;
      }
      values.set(index, picked.row.v[found]);
    }
    const shown = reference.displayColumn
      ? indexOfColumn(picked.columns, reference.displayColumn)
      : -1;
    // None, when the rows chosen from don't show it: what was shown for the row it referred to goes.
    const display = shown >= 0 ? shortDisplay(picked.row.v[shown]) : null;
    edits.set(row, values, { [reference.navigation]: display });
    this.announce(
      `${reference.navigation} of ${rowLabelOf(row, this.entityChanges)} set${display === null ? '' : ` to ${displayText(display)}`}`,
    );
  }

  /** Says what was done, for screen readers (what couldn't be is an alert). */
  private announce(message: string): void {
    void this.announcer.announce(message, 'polite');
  }

  /** What came of a change: why it couldn't be made, said (the session's problems aside). */
  private after(outcome: Outcome, title: string): void {
    if (!outcome.done && !isSessionProblem(outcome.problem)) {
      this.editProblem.set({ title, reasons: outcome.reasons });
    }
  }

  /** The cells show the entity's changes: new rows at the top, rows' classes, values as they will be. */
  private showChanges(changes: EntityChanges): void {
    this.entityChanges = changes;
    const live = this.current();
    const edits = untracked(this.made)?.edits;
    if (!live || !edits) {
      return;
    }
    const rows = changes.inserts.map(
      (change) =>
        this.newRows.get(change.tempId ?? '') ??
        ({ id: null, k: null, v: [], r: null, tempId: change.tempId ?? '' } satisfies EditRow),
    );
    this.newRows = new Map(rows.map((row) => [row.tempId ?? '', row]));
    const pinned = live.api.getGridOption('pinnedTopRowData') ?? [];
    if (rows.length !== pinned.length || rows.some((row, index) => row !== pinned[index])) {
      live.api.setGridOption('pinnedTopRowData', rows);
    }
    live.api.setGridOption('rowClassRules', rowClassRulesOf(edits) as RowClassRules<GridRow>);
    live.api.refreshCells({ force: true });
    this.refreshFocused(live);
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

  /**
   * The rows asked for anew, from the page shown (and counted): once the cell being edited is, as the grid makes
   * its rows anew (which would end the edit).
   */
  private readAgain(live: Live): void {
    if (live.api.getEditingCells().length > 0) {
      this.waitingToRead = live;
      return;
    }
    this.waitingToRead = null;
    this.ask(live, live.datasource.query, live.page);
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
          live.loaded = true;
          clearFailure(live.api);
          this.selectRow(live);
          this.placeKeyboard(live);
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

  /**
   * Chooses in the grid the row the address chose, when it is among those loaded, and scrolls to it; with
   * `focusChosen`, the keyboard goes to it the first time.
   */
  private selectRow(live: Live): void {
    const wanted = live.row === null ? null : JSON.stringify(live.row);
    const selected = live.api.getSelectedRows()[0]?.id;
    if ((selected ? JSON.stringify(keyOf(selected)) : null) === wanted) {
      return;
    }
    this.following = true;
    try {
      let found = null as IRowNode<GridRow> | null;
      live.api.forEachNode((node) => {
        if (wanted !== null && node.data?.id && JSON.stringify(keyOf(node.data.id)) === wanted) {
          node.setSelected(true, true, 'api');
          found = node;
        }
      });
      if (found) {
        // By its index: ensureNodeVisible looks at each row before it, which asks for their pages.
        if (found.rowIndex !== null) {
          live.api.ensureIndexVisible(found.rowIndex, 'middle');
        }
      } else if (selected) {
        // The row the address chose isn't among those loaded (or none is): none is chosen in the grid.
        live.api.deselectAll('all', 'api');
      }
    } finally {
      this.following = false;
    }
  }

  /**
   * With `focusChosen`, once rows are loaded: the keyboard on the row chosen, or on the condition's field when none
   * is; unless the user has put it elsewhere in what holds the grid (its dialog), outside the grid. (Outside what
   * holds it, the keyboard is where it was as the dialog opened, which the dialog moves.)
   */
  private placeKeyboard(live: Live): void {
    if (!this.focusChosen() || live.keyboardPlaced || !live.loaded) {
      return;
    }
    live.keyboardPlaced = true;
    const host = this.host.nativeElement;
    const scope = host.closest('[role="dialog"], [role="alertdialog"]') ?? host.ownerDocument.body;
    const active = host.ownerDocument.activeElement;
    if (active && active !== scope && scope.contains(active) && !host.contains(active)) {
      return;
    }
    const node = live.api.getSelectedNodes()[0];
    const first = live.api.getAllDisplayedColumns()[0];
    if (node?.rowIndex != null && first) {
      live.api.setFocusedCell(node.rowIndex, first);
    } else {
      this.whereField()?.nativeElement.focus();
    }
  }

  private refreshFocused(live: Live): void {
    const cell = live.api.getFocusedCell();
    if (!cell) {
      return;
    }
    this.focused.set({
      colId: cell.column.getColId(),
      row: rowAt(live.api, cell.rowIndex, cell.rowPinned),
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

/** A row's change among an entity's: by its id, or a new row's by its temporary id. */
function changeIn(changes: EntityChanges, row: GridRow): PendingChange | null {
  if (isNew(row)) {
    return changes.inserts.find((change) => change.tempId === row.tempId) ?? null;
  }
  return row.id ? (changes.rows.get(row.id) ?? null) : null;
}

/** A row as changes name it: by its key and id, or a new one by its temporary id; null for a row without a key. */
function changeRowOf(row: GridRow): ChangeRow | null {
  if (isNew(row)) {
    return { tempId: row.tempId };
  }
  return row.id && row.k ? { key: row.k, rowId: row.id } : null;
}

/**
 * The values a row had, to check it still has them when its deletion is committed: its key's, and those of the
 * columns that may be given values (the entity's own).
 */
function originalsOf(schema: LinkSchema, row: GridRow): Record<string, unknown> {
  const original: Record<string, unknown> = {};
  schema.columns.forEach((column, index) => {
    if (column.isKey || column.canUpdate || column.insert !== 'never') {
      original[column.name] = row.v[index];
    }
  });
  return original;
}

/** The row at a place in the grid: a page's, or a new row's (pinned at the top). */
function rowAt(
  api: GridApi<GridRow>,
  index: number,
  pinned: string | null | undefined,
): GridRow | null {
  if (pinned === 'top') {
    return api.getPinnedTopRow(index)?.data ?? null;
  }
  return pinned ? null : (api.getDisplayedRowAtIndex(index)?.data ?? null);
}

/** A display value as sent with a reference chosen: text cut to what the server keeps. */
function shortDisplay(value: unknown): unknown {
  if (typeof value !== 'string') {
    return value;
  }
  let text = value;
  // Cut by what JSON writes (quotes, escapes) as the server counts it.
  while (JSON.stringify(text).length > displayLength) {
    const over = JSON.stringify(text).length - displayLength;
    text = `${text.slice(0, Math.max(0, text.length - Math.max(over, 1) - 1))}…`;
  }
  return text;
}

/** Whether a new row starts as those of a collection's crumb do (its columns that refer to the crumb's row). */
function startsAs(change: PendingChange, defaults: Values): boolean {
  return Object.entries(defaults).every(
    ([column, value]) => String(change.values[column] ?? '') === String(value),
  );
}

/** Whether the changes shown are the same: those of the same rows, alike. */
function sameChanges(a: EntityChanges, b: EntityChanges): boolean {
  return (
    a === b ||
    (a.rows.size === b.rows.size &&
      a.inserts.length === b.inserts.length &&
      JSON.stringify([...a.rows.values(), ...a.inserts]) ===
        JSON.stringify([...b.rows.values(), ...b.inserts]))
  );
}

/** A row as the grid's actions say it: by its key, or a new row's place among those shown. */
function rowLabelOf(row: GridRow, changes: EntityChanges): string {
  if (isNew(row)) {
    const place = changes.inserts.findIndex((change) => change.tempId === row.tempId);
    return place < 0 ? 'the new row' : `new row ${place + 1}`;
  }
  return row.id ? `row ${keyOf(row.id).join(', ')}` : 'the row';
}

function capital(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
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
