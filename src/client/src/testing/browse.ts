import type { HttpTestingController, TestRequest } from '@angular/common/http/testing';
import type { Schema } from '../app/core/api/api-client';
import { catalogVersionHeader } from '../app/core/catalog/catalog-version';
import { requestTo } from './http';

export type GridColumnDto = Schema<'GridColumnDto'>;
export type GridRowDto = Schema<'GridRowDto'>;
export type BrowsePageDto = Schema<'BrowsePageDto'>;
export type BrowsePageRequest = Schema<'BrowsePageRequest'>;
export type ScalarKind = Schema<'ScalarKind'>;

/** Where the grid asks for its pages. */
export const pageUrl = '/api/browse/page';

/** How each kind's type is written, as the server writes them. */
const typeTexts: Partial<Record<ScalarKind, string>> = {
  decimal: 'decimal(10,2)',
  dateTime: 'datetime',
  dateTimeOffset: 'datetimeoffset',
};

/** A column of shop.orders' rows, read from its table: text that may be null, unless said otherwise. */
export function columnOf(
  name: string,
  kind: ScalarKind = 'string',
  changes: Partial<GridColumnDto> = {},
): GridColumnDto {
  return {
    name,
    type: { kind, nullable: true, text: `${typeTexts[kind] ?? kind}?` },
    isKey: false,
    canUpdate: false,
    insert: 'optional',
    readOnlyReason: null,
    lineage: {
      kind: 'direct',
      sources: [{ column: `shop.orders.${name}`, path: null }],
      expression: null,
    },
    reference: null,
    ...changes,
  };
}

/** shop.orders' columns: its key, id, then status and total. */
export function orderColumns(): GridColumnDto[] {
  return [
    columnOf('id', 'int64', {
      isKey: true,
      type: { kind: 'int64', nullable: false, text: 'int64' },
    }),
    columnOf('status'),
    columnOf('total', 'decimal'),
  ];
}

/** A row by its values, keyed by the first (as text, as row ids hold whole numbers). */
export function rowOf(values: unknown[], key: unknown[] | null = [String(values[0])]): GridRowDto {
  return { id: key ? JSON.stringify(key) : null, k: key, v: values, r: null };
}

/** shop.orders' rows numbered from `from` (ids 1001 on), as many as `count`. */
export function orderRows(count: number, from = 0): GridRowDto[] {
  return Array.from({ length: count }, (_, index) =>
    rowOf([String(1001 + from + index), index % 2 ? 'open' : null, `${from + index}.50`]),
  );
}

/** A page of rows, all there are unless said otherwise; with the schema of `columns` when given. */
export function pageOf(
  rows: GridRowDto[],
  changes: Partial<BrowsePageDto> = {},
  columns: GridColumnDto[] | null = null,
  key: string[] | null = ['id'],
): BrowsePageDto {
  return {
    queryText: 'shop.orders',
    parameters: [],
    entity: 'shop.orders',
    schema: columns
      ? {
          entity: 'shop.orders',
          key,
          capabilities: { canInsert: false, canUpdate: false, canDelete: false },
          columns,
          references: [],
          collections: [],
        }
      : null,
    rows,
    offset: 0,
    hasMore: false,
    total: rows.length,
    ...changes,
  };
}

/** Waits for the grid to fetch the pages it asked for (it waits a moment before it does: blockLoadDebounceMillis). */
export async function pagesFetched(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 20));
}

/** The grid's request for a page, once made (one only). */
export async function pageRequest(http: HttpTestingController): Promise<TestRequest> {
  return requestTo(http, pageUrl, 'POST');
}

/** Answers the grid's request for a page, with the catalog's version. */
export async function answerPage(
  http: HttpTestingController,
  page: BrowsePageDto,
  version = 'v1',
): Promise<BrowsePageRequest> {
  const request = await pageRequest(http);
  request.flush(page, { headers: { [catalogVersionHeader]: version } });
  return request.request.body as BrowsePageRequest;
}

/** Answers the grid's first request (an entity's page opened): shop.orders' columns, and no rows. */
export async function answerGrid(http: HttpTestingController): Promise<void> {
  await answerPage(http, pageOf([], {}, orderColumns()));
}

/** The grid's rows, as text: each row's cells, in order. */
export function gridCells(container: ParentNode): string[][] {
  return [...container.querySelectorAll<HTMLElement>('.ag-row[row-index]')]
    .sort((a, b) => Number(a.getAttribute('row-index')) - Number(b.getAttribute('row-index')))
    .map((row) =>
      [...row.querySelectorAll<HTMLElement>('[col-id]')]
        .sort((a, b) => colIndex(a) - colIndex(b))
        .map((cell) => cell.textContent ?? ''),
    );
}

/** The grid's headers' names. */
export function gridHeaders(container: ParentNode): string[] {
  return [...container.querySelectorAll<HTMLElement>('.ag-header-cell[col-id]')]
    .sort((a, b) => colIndex(a) - colIndex(b))
    .map((header) => header.querySelector('.ag-header-cell-text')?.textContent ?? '');
}

function colIndex(element: HTMLElement): number {
  return Number(element.getAttribute('col-id')?.slice(1));
}
