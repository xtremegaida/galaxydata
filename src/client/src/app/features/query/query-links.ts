import type {
  ColDef,
  ICellRendererComp,
  ICellRendererParams,
  SuppressKeyboardEventParams,
} from 'ag-grid-community';
import { type GridColumn, type GridRow, cellText, columnDefsOf } from '../browse/grid/grid-columns';
import type { GridCollection } from '../browse/grid/grid-links';
import type { ColumnLink, ResultSchema } from './query-datasource';

/** What a link of a query's rows follows: a column's (by its ordinal), or the rows that refer to a row. */
export type LinkTarget = { readonly column: number } | { readonly related: number };

/** Following the links of a query's rows (where they lead is the server's to say). */
export interface ResultLinks {
  follow(target: LinkTarget, row: GridRow): void;
}

/** The id of a column of the rows that refer to each row, by its place among them. */
export function relatedColIdOf(index: number): string {
  return `n${index}`;
}

/** The place among the related rows of a column the grid names by its id; -1 for another column. */
export function indexOfRelatedColId(colId: string): number {
  return /^n\d+$/.test(colId) ? Number(colId.slice(1)) : -1;
}

/**
 * The columns of a query's rows as the grid's columns are: every one by its ordinal (hidden ones too, which links
 * need), the row's key marked when the rows are an entity's.
 */
export function gridColumnsOf(schema: ResultSchema): GridColumn[] {
  const key = schema.rowIdentity?.keyOrdinals ?? [];
  return schema.columns.map((column) => ({
    name: column.name,
    type: column.type,
    isKey: key.includes(column.ordinal),
    canUpdate: false,
    insert: 'never',
    readOnlyReason: null,
    lineage: column.lineage,
    reference: null,
  }));
}

/** The kinds of rows that refer to each row (when the rows are an entity's), as collections. */
export function relatedOf(schema: ResultSchema): GridCollection[] {
  return (schema.rowIdentity?.related ?? []).map((link) => ({
    navigation: link.navigation ?? 'related',
    target: link.target ?? '',
    multiplicity: link.multiplicity ?? 'many',
  }));
}

/** Whether a link leads anywhere from a row: a row's, from a NULL (a foreign key of none), doesn't. */
export function leadsSomewhere(link: ColumnLink, row: GridRow | null): boolean {
  if (!row) {
    return false;
  }
  return link.kind !== 'row' || link.ordinals.every((ordinal) => row.v[ordinal] !== null);
}

/** Where a link leads, in a sentence. */
export function linkText(link: ColumnLink): string {
  const through = link.navigation ? `, through ${link.navigation}` : '';
  switch (link.kind) {
    case 'row':
      return `The row of ${link.target ?? 'another entity'} it refers to${through}.`;
    case 'collection':
      return link.multiplicity === 'many'
        ? `The rows of ${link.target ?? 'another entity'} it was worked out from${through}.`
        : `The row of ${link.target ?? 'another entity'} it was worked out from${through}.`;
    default:
      return 'The rows of its group, which it was worked out from.';
  }
}

/** What a cell with a link is given: what it shows, and following it. */
interface LinkParams extends ICellRendererParams<GridRow> {
  readonly content: (params: ICellRendererParams<GridRow>) => {
    readonly text: string;
    readonly leads: boolean;
    readonly arrow: boolean;
  };
  readonly follow: (row: GridRow) => void;
}

/**
 * The grid's columns of a query's rows: each by its ordinal, hidden ones hidden; those whose values lead somewhere
 * (a row they refer to, the rows they were worked out from) as links; then, when the rows are an entity's, a column
 * for each kind of rows that refer to them, a link in each row.
 */
export function resultColumnDefsOf(schema: ResultSchema, links: ResultLinks): ColDef<GridRow>[] {
  const columns = gridColumnsOf(schema);
  const defs = columnDefsOf(columns).map((def, index): ColDef<GridRow> => {
    const column = schema.columns[index];
    const link = column.link;
    if (column.hidden) {
      return { ...def, hide: true, lockVisible: true, suppressMovable: true };
    }
    if (!link) {
      return def;
    }
    const follow = (row: GridRow) => links.follow({ column: index }, row);
    return {
      ...def,
      headerTooltip: [def.headerTooltip, `Leads to: ${linkText(link)}`].filter(Boolean).join(' '),
      cellRenderer: ResultLinkCell,
      cellRendererParams: {
        content: (params: ICellRendererParams<GridRow>) => ({
          text: params.valueFormatted ?? cellText(params.value, column.type),
          leads: leadsSomewhere(link, params.data ?? null),
          arrow: false,
        }),
        follow,
      } satisfies Pick<LinkParams, 'content' | 'follow'>,
      suppressKeyboardEvent: followsOnEnter((row) => leadsSomewhere(link, row), follow),
    };
  });
  // When the rows are an entity's, a column for each kind of rows that refer to them.
  const related = relatedOf(schema).map((collection, index): ColDef<GridRow> => {
    const follow = (row: GridRow) => links.follow({ related: index }, row);
    return {
      colId: relatedColIdOf(index),
      headerName: collection.navigation,
      headerTooltip: `${collection.navigation}: the ${collection.multiplicity === 'many' ? 'rows' : 'row'} of ${collection.target} that refer to the row.`,
      valueGetter: ({ data }) => (data ? `${collection.navigation} ›` : undefined),
      cellRenderer: ResultLinkCell,
      cellRendererParams: {
        content: ({ data }: ICellRendererParams<GridRow>) => ({
          text: data ? collection.navigation : '',
          leads: !!data,
          arrow: !!data,
        }),
        follow,
      } satisfies Pick<LinkParams, 'content' | 'follow'>,
      suppressKeyboardEvent: followsOnEnter((row) => row !== null, follow),
      sortable: false,
      filter: false,
      width: 140,
    };
  });
  return [...defs, ...related];
}

/** Enter on a cell whose link leads somewhere follows it, before the grid does anything with the key. */
function followsOnEnter(
  leads: (row: GridRow | null) => boolean,
  follow: (row: GridRow) => void,
): (params: SuppressKeyboardEventParams<GridRow>) => boolean {
  return ({ event, editing, data }) => {
    if (
      editing ||
      event.type !== 'keydown' ||
      event.key !== 'Enter' ||
      event.ctrlKey ||
      event.metaKey ||
      event.altKey ||
      event.shiftKey ||
      !data ||
      !leads(data)
    ) {
      return false;
    }
    event.preventDefault();
    follow(data);
    return true;
  };
}

/**
 * A cell whose value may lead somewhere: where is the server's to say, so it isn't an address (a link to open
 * elsewhere), but text marked as a link, which a click (or Enter on the cell) follows. It isn't a stop of its own
 * for the keyboard: the grid's cell is. Asked again, it changes what it shows in place.
 */
export class ResultLinkCell implements ICellRendererComp<GridRow> {
  private element!: HTMLElement;
  private params!: LinkParams;

  init(params: LinkParams): void {
    this.element = params.eGridCell.ownerDocument.createElement('span');
    this.element.addEventListener('click', (event) => {
      const row = this.params.data;
      if (!this.element.classList.contains('gd-link') || !row || event.button !== 0) {
        return;
      }
      // Not choosing the row (nor anything else the grid does with a click).
      event.stopPropagation();
      if (!event.altKey) {
        this.params.follow(row);
      }
    });
    this.show(params);
  }

  getGui(): HTMLElement {
    return this.element;
  }

  refresh(params: LinkParams): boolean {
    this.show(params);
    return true;
  }

  private show(params: LinkParams): void {
    this.params = params;
    const content = params.content(params);
    this.element.className = content.leads ? 'gd-link' : '';
    if (content.leads) {
      this.element.setAttribute('role', 'link');
    } else {
      this.element.removeAttribute('role');
    }
    this.element.textContent = content.text;
    if (content.arrow) {
      const arrow = this.element.ownerDocument.createElement('span');
      arrow.setAttribute('aria-hidden', 'true');
      arrow.textContent = ' ›';
      this.element.append(arrow);
    }
  }
}
