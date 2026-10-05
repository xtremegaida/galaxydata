import type {
  ColDef,
  ICellRendererComp,
  ICellRendererParams,
  SuppressKeyboardEventParams,
} from 'ag-grid-community';
import type { Schema } from '../../../core/api/api-client';
import {
  type GridColumn,
  type GridRow,
  cellText,
  colIdOf,
  columnDefsOf,
  indexOfColId,
  keyOf,
} from './grid-columns';

export type GridReference = Schema<'GridReferenceDto'>;
export type GridCollection = Schema<'GridCollectionDto'>;

/** A navigation from a row: what a link in one of its cells follows. */
export interface CellLink {
  readonly navigation: string;
  /** The row's key, its values as text, as the address holds them. */
  readonly row: readonly string[];
}

/** Where cells' links lead (for the browser: a new tab, the address copied), and following them. */
export interface GridLinks {
  href(link: CellLink): string;
  follow(link: CellLink): void;
}

/** What the rows refer to (by their columns' values), and the collections of rows that refer to them. */
export interface LinkSchema {
  readonly columns: readonly GridColumn[];
  readonly references: readonly GridReference[];
  readonly collections: readonly GridCollection[];
}

/** What a cell with a link shows: its text (a link, when it leads somewhere), and what is beside it. */
export interface LinkContent {
  readonly text: string;
  readonly link: CellLink | null;
  /** Text beside it: the value itself, beside the display value of the row it refers to. */
  readonly aside: string | null;
  /** Whether an arrow follows the text: drawn, not read (`aria-hidden`). */
  readonly arrow: boolean;
}

/**
 * The references changed in rows not yet committed: a row whose reference's columns have values not committed shows
 * the display value given with them, and no link (to click, or follow with Enter), as crumbs follow references as
 * committed; null when its reference isn't changed.
 */
export type ReferenceChanges = (
  row: GridRow,
  reference: GridReference,
) => { readonly display: unknown } | null;

/** What a cell with a link is given: what it shows, worked out from the grid's, and where links lead. */
export interface LinkCellParams extends ICellRendererParams<GridRow> {
  readonly content: (params: ICellRendererParams<GridRow>) => LinkContent;
  readonly links: GridLinks | null;
}

/** The id of a collection's column in the grid, by its place among the collections. */
export function collectionColIdOf(index: number): string {
  return `n${index}`;
}

/** The place of a collection the grid names by its column's id; -1 when the column isn't a collection's. */
export function indexOfCollectionColId(colId: string): number {
  return /^n\d+$/.test(colId) ? Number(colId.slice(1)) : -1;
}

/** The reference a column's values are part of, if they are. */
export function referenceOf(schema: LinkSchema, index: number): GridReference | null {
  const reference = schema.columns[index]?.reference;
  return reference === null || reference === undefined
    ? null
    : (schema.references[reference] ?? null);
}

/**
 * The link of a cell: the row its column's values refer to, or the rows of a collection that refer to its row. A
 * crumb follows a navigation from a row's key, so rows without one have none; nor do values that refer to no row (a
 * null among them).
 */
export function cellLinkOf(
  schema: LinkSchema,
  colId: string,
  row: GridRow | null | undefined,
): CellLink | null {
  if (!row?.id) {
    return null;
  }
  const collection = schema.collections[indexOfCollectionColId(colId)];
  if (collection) {
    return { navigation: collection.navigation, row: keyOf(row.id) };
  }
  const reference = referenceOf(schema, indexOfColId(colId));
  if (!reference || refersToNone(reference, row)) {
    return null;
  }
  return { navigation: reference.navigation, row: keyOf(row.id) };
}

/** Whether a row's values refer to no row: a null among them. */
export function refersToNone(reference: GridReference, row: GridRow): boolean {
  return reference.columns.some((column) => row.v[column] == null);
}

/** The display value of the row a cell's values refer to, as text; null when there is none to show. */
export function displayOf(column: GridColumn, row: GridRow | null): string | null {
  const reference = column.reference;
  if (reference === null || !row) {
    return null;
  }
  const display = row.r?.[reference];
  return display === null || display === undefined ? null : displayText(display);
}

/**
 * A display value as text. Its type isn't said (it is the column's that shows the rows referred to), so a date-time
 * is told by its form, and read with a space, as the grid shows them.
 */
export function displayText(value: unknown): string {
  if (typeof value !== 'string') {
    return JSON.stringify(value);
  }
  return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(value) ? value.replace('T', ' ') : value;
}

/** What a reference's values are, in a sentence. */
export function referenceText(reference: GridReference): string {
  return `Refers to a row of ${reference.target}, through ${reference.navigation}.`;
}

/** What a collection's rows are, in a sentence. */
export function collectionText(collection: GridCollection): string {
  return collection.multiplicity === 'many'
    ? `The rows of ${collection.target} that refer to this row.`
    : `The row of ${collection.target} that refers to this row.`;
}

/**
 * The grid's columns with their links: the rows' columns, those whose values refer to a row showing its display
 * value (a link to it, with `links`), the value itself beside it; then, with `links` and rows with keys, a column
 * for each collection of rows that refer to them. References changed (`changes`) show what was given with them.
 */
export function linkedColumnDefsOf(
  schema: LinkSchema,
  links: GridLinks | null,
  keyed: boolean,
  changes: ReferenceChanges | null = null,
): ColDef<GridRow>[] {
  const columns = columnDefsOf(schema.columns).map((def, index): ColDef<GridRow> => {
    const reference = referenceOf(schema, index);
    return reference
      ? {
          ...def,
          headerTooltip: `${def.headerTooltip ?? ''} ${referenceText(reference)}`,
          cellClass: undefined,
          cellRenderer: LinkCell,
          cellRendererParams: { content: referenceContent(schema, index, changes), links },
          suppressKeyboardEvent: links
            ? followsOnEnter(schema, colIdOf(index), links, changes)
            : undefined,
          width: Math.max(def.width ?? 0, 200),
        }
      : def;
  });
  return links && keyed ? [...columns, ...collectionColumnDefsOf(schema, links)] : columns;
}

/** The ids of the columns whose cells may have links: those whose values refer to rows, and the collections'. */
export function linkedColIdsOf(schema: LinkSchema, keyed: boolean): string[] {
  if (!keyed) {
    return [];
  }
  return [
    ...schema.columns.flatMap((column, index) =>
      column.reference === null ? [] : [colIdOf(index)],
    ),
    ...schema.collections.map((_, index) => collectionColIdOf(index)),
  ];
}

/** A column for each collection of rows that refer to the rows: a link to them in each row with a key. */
function collectionColumnDefsOf(schema: LinkSchema, links: GridLinks): ColDef<GridRow>[] {
  return schema.collections.map((collection, index) => {
    const colId = collectionColIdOf(index);
    return {
      colId,
      headerName: collection.navigation,
      headerTooltip: `${collection.navigation}: ${collectionText(collection)}`,
      valueGetter: ({ data }) => (data ? (data.id ? `${collection.navigation} ›` : '') : undefined),
      cellRenderer: LinkCell,
      cellRendererParams: {
        content: ({ data }) => {
          const link = cellLinkOf(schema, colId, data);
          return { text: link ? collection.navigation : '', link, aside: null, arrow: !!link };
        },
        links,
      } satisfies Pick<LinkCellParams, 'content' | 'links'>,
      suppressKeyboardEvent: followsOnEnter(schema, colId, links),
      sortable: false,
      filter: false,
      width: 140,
    } satisfies ColDef<GridRow>;
  });
}

/**
 * Enter on a cell with a link follows it. The grid asks before it does anything with a key, and the key goes no
 * further, so a link the keyboard is on (pressed by the pointer) isn't followed twice. A reference changed has no
 * link to follow (the row committed isn't the one it will refer to).
 */
function followsOnEnter(
  schema: LinkSchema,
  colId: string,
  links: GridLinks,
  changes: ReferenceChanges | null = null,
): (params: SuppressKeyboardEventParams<GridRow>) => boolean {
  return ({ event, editing, data }) => {
    if (
      editing ||
      event.type !== 'keydown' ||
      event.key !== 'Enter' ||
      event.ctrlKey ||
      event.metaKey ||
      event.altKey ||
      event.shiftKey
    ) {
      return false;
    }
    const link = cellLinkOf(schema, colId, data);
    const reference = referenceOf(schema, indexOfColId(colId));
    if (!link || (data && reference && changes?.(data, reference))) {
      return false;
    }
    event.preventDefault();
    links.follow(link);
    return true;
  };
}

/** What a cell whose values refer to a row shows: that row's display value, linked, the value itself beside it. */
function referenceContent(
  schema: LinkSchema,
  index: number,
  changes: ReferenceChanges | null,
): (params: ICellRendererParams<GridRow>) => LinkContent {
  const column = schema.columns[index];
  const colId = colIdOf(index);
  const reference = referenceOf(schema, index);
  return (params) => {
    const value: unknown = params.value;
    const row = params.data ?? null;
    const raw = params.valueFormatted ?? cellText(value, column.type);
    if (!row || value === null || value === undefined) {
      return { text: raw, link: null, aside: null, arrow: false };
    }
    const changed = reference && changes ? changes(row, reference) : null;
    const display = changed
      ? changed.display === null || changed.display === undefined
        ? null
        : displayText(changed.display)
      : displayOf(column, row);
    return {
      text: display ?? raw,
      link: changed ? null : cellLinkOf(schema, colId, row),
      aside: display !== null && display !== raw ? raw : null,
      arrow: false,
    };
  };
}

/**
 * A cell that may hold a link. Asked again (another row's values, the address changed), it changes what it shows
 * in place, so a link the keyboard is on stays. It makes elements: text a renderer gives the grid is taken for HTML.
 *
 * Its link isn't a stop of its own for the keyboard (the grid is one; Enter on the cell follows it, see
 * `followsOnEnter`). A click on it
 * follows it without choosing its row; with Ctrl, Shift or Meta, or another button, the browser opens it (a new tab
 * or window); with Alt, which would download it, nothing happens, so its text may be selected.
 */
export class LinkCell implements ICellRendererComp<GridRow> {
  private element!: HTMLElement;
  private shown: HTMLElement | null = null;
  private aside: HTMLElement | null = null;
  private link: CellLink | null = null;
  private links: GridLinks | null = null;

  init(params: LinkCellParams): void {
    this.element = params.eGridCell.ownerDocument.createElement('span');
    this.element.className = 'gd-linked';
    this.show(params);
  }

  getGui(): HTMLElement {
    return this.element;
  }

  refresh(params: LinkCellParams): boolean {
    this.show(params);
    return true;
  }

  private show(params: LinkCellParams): void {
    const content = params.content(params);
    const document = this.element.ownerDocument;
    this.link = content.link;
    this.links = params.links;
    const href = this.link && this.links ? this.links.href(this.link) : null;
    if (!this.shown || (this.shown.tagName === 'A') !== (href !== null)) {
      const shown = href !== null ? this.anchor(document) : document.createElement('span');
      shown.classList.add('gd-display');
      if (this.shown) {
        this.shown.replaceWith(shown);
      } else {
        this.element.prepend(shown);
      }
      this.shown = shown;
    }
    if (href !== null && this.shown.getAttribute('href') !== href) {
      this.shown.setAttribute('href', href);
    }
    this.shown.textContent = content.text;
    if (content.arrow) {
      const arrow = document.createElement('span');
      arrow.setAttribute('aria-hidden', 'true');
      arrow.textContent = ' ›';
      this.shown.append(arrow);
    }
    if (content.aside === null) {
      this.aside?.remove();
      this.aside = null;
    } else {
      if (!this.aside) {
        this.aside = document.createElement('span');
        this.aside.className = 'gd-raw';
        this.element.append(this.aside);
      }
      this.aside.textContent = content.aside;
    }
  }

  private anchor(document: Document): HTMLAnchorElement {
    const element = document.createElement('a');
    element.className = 'gd-link';
    element.tabIndex = -1;
    element.addEventListener('click', (event) => {
      event.stopPropagation();
      if (event.altKey) {
        event.preventDefault();
        return;
      }
      if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey) {
        return;
      }
      event.preventDefault();
      if (this.link && this.links) {
        this.links.follow(this.link);
      }
    });
    return element;
  }
}
