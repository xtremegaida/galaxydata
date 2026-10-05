import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { AgGridAngular } from 'ag-grid-angular';
import { fakeBrowserProviders, problemBody, sessionOf } from '../../../../testing/auth';
import {
  answerPage,
  answerPosition,
  customerReference,
  editablePageOf,
  gridCells,
  linkedColumns,
  newRowCells,
  orderRows,
  pageOf,
  pagesFetched,
  rowOf,
} from '../../../../testing/browse';
import {
  FakeChangesChannel,
  answerChanges,
  changeOf,
  commitUrl,
  insertOf,
  opsUrl,
  previewOf,
  previewUrl,
  resultOf,
  setOf,
} from '../../../../testing/changes';
import { requestTo, settle } from '../../../../testing/http';
import { alertsOf, clickButton, textOf } from '../../../../testing/pages';
import { AuthStore } from '../../../core/auth/auth-store';
import type { UserRole } from '../../../core/auth/roles';
import { type BrowseCrumb, crumbOf } from '../../../core/browse/browse-url';
import {
  type ChangeSet,
  ChangesChannel,
  PendingChanges,
} from '../../../core/changes/pending-changes';
import type { BrowseSource } from './browse-datasource';
import { BROWSE_PAGE_SIZE, BrowseGrid, type InsertDefaults } from './browse-grid';
import type { GridColumn, GridRow } from './grid-columns';
import type { GridReference } from './grid-links';

/** The grid as browsing has it, its rows changed there. */
@Component({
  imports: [BrowseGrid],
  template: `<gd-browse-grid
    [source]="source()"
    [crumb]="crumb()"
    [editing]="editing()"
    [insertDefaults]="defaults()"
    (crumbChange)="crumb.set($event)"
  />`,
})
class Host {
  readonly source = signal<BrowseSource>({ entity: 'shop.orders' });
  readonly crumb = signal<BrowseCrumb>(crumbOf('shop.orders'));
  readonly editing = signal(true);
  readonly defaults = signal<InsertDefaults | null>(null);
}

describe('BrowseGrid, changing rows', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        fakeBrowserProviders(),
        { provide: ChangesChannel, useClass: FakeChangesChannel },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: BROWSE_PAGE_SIZE, useValue: 3 },
        provideRouter([]),
      ],
    });
  });

  afterEach(async () => {
    await pagesFetched();
    try {
      TestBed.inject(HttpTestingController).verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  /**
   * The grid of a user (a data manager unless said otherwise), with their changes, showing shop.orders' first
   * page: rows 1001 to 1003, which may be changed (unless the page says otherwise).
   */
  async function opened(
    options: {
      role?: UserRole;
      changes?: ChangeSet;
      page?: ReturnType<typeof editablePageOf>;
      defaults?: InsertDefaults;
      editing?: boolean;
    } = {},
  ) {
    const http = TestBed.inject(HttpTestingController);
    const auth = TestBed.inject(AuthStore);
    const signedIn = auth.ensure();
    http.expectOne('/api/auth/session').flush(sessionOf(options.role ?? 'dataManager'));
    await signedIn;
    const changes = TestBed.inject(PendingChanges);
    TestBed.tick();
    if (changes.enabled()) {
      await answerChanges(http, options.changes ?? setOf());
    }
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.defaults.set(options.defaults ?? null);
    fixture.componentInstance.editing.set(options.editing ?? true);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const shown = async () => {
      for (let turn = 0; turn < 3; turn++) {
        await settle();
        fixture.detectChanges();
      }
      await pagesFetched();
      fixture.detectChanges();
    };
    await answerPage(http, options.page ?? editablePageOf(orderRows(3), { total: 3 }));
    await shown();
    const api = () =>
      (
        fixture.debugElement.query(By.directive(AgGridAngular))
          .componentInstance as AgGridAngular<GridRow>
      ).api;
    return { fixture, http, element, shown, api, changes };
  }

  /** The cells of the column saying each row's change: the words said, by row (not new rows). */
  function states(element: HTMLElement): string[] {
    return [...element.querySelectorAll<HTMLElement>('.ag-row[row-index]')]
      .filter((row) => !row.closest('.ag-grid-pinned-top-rows'))
      .filter((row) => row.querySelector('[col-id=gd-state]'))
      .sort((a, b) => Number(a.getAttribute('row-index')) - Number(b.getAttribute('row-index')))
      .map((row) => textOf(row.querySelector('[col-id=gd-state] .cdk-visually-hidden')));
  }

  it('offers the changes the entity takes to those who change data, saying the keys', async () => {
    const { element, api } = await opened();
    const bar = element.querySelector<HTMLElement>('.edit-bar');
    expect(bar?.getAttribute('role')).toBe('group');
    expect(bar?.getAttribute('aria-label')).toBe('Changes to the rows');
    expect([...bar!.querySelectorAll('button')].map((button) => textOf(button))).toEqual([
      'add Add a row',
      'delete Delete the row',
      'undo Revert the row',
    ]);
    expect(textOf(bar!.querySelector('.hint'))).toContain('Ctrl+Delete: delete the row');
    expect(element.querySelector('.ag-header-cell[col-id=gd-state]')).not.toBeNull();
    expect(api().getColumn('gd-state')?.getPinned()).toBe('left');
    expect(gridCells(element).map((row) => row.length)).toEqual([3, 3, 3]);
  });

  it("marks the cells and rows a preview found can't be committed, and says why", async () => {
    const paid = changeOf({
      key: ['1002'],
      rowId: '["1002"]',
      values: { status: 'lost' },
      original: { status: 'open' },
    });
    const { element, http, api, shown, changes } = await opened({ changes: setOf([paid]) });
    const previewing = changes.preview();
    (await requestTo(http, previewUrl, 'POST')).flush(
      previewOf({
        planId: null,
        issues: [
          { change: 1, column: 'status', message: "'status' takes open or paid" },
          { change: 1, column: null, message: 'The row is locked' },
        ],
      }),
    );
    await previewing;
    await shown();
    const cell = element.querySelector('.ag-row[row-index="1"] [col-id=c1]');
    expect(cell?.classList).toContain('gd-invalid');
    expect(element.querySelector('.ag-row[row-index="1"] [col-id=c2]')?.classList).not.toContain(
      'gd-invalid',
    );
    expect(states(element)[1]).toBe("Changed, can't be committed");
    api().setFocusedCell(1, 'c1');
    await shown();
    expect(
      [...element.querySelectorAll('gd-grid-inspector .note')].map((note) => textOf(note)),
    ).toEqual([
      'Changed, not committed: it was open.',
      "Can't be committed: 'status' takes open or paid",
      "Can't be committed: The row is locked",
    ]);

    // Changed again, it is of no issue till the next preview.
    api().getDisplayedRowAtIndex(1)!.setDataValue('c1', 'paid');
    (await requestTo(http, opsUrl, 'POST')).flush(
      setOf([{ ...paid, values: { status: 'paid' }, updatedAt: '2026-10-05T08:05:00Z' }], 2),
    );
    await shown();
    expect(element.querySelector('.ag-row[row-index="1"] [col-id=c1]')?.classList).not.toContain(
      'gd-invalid',
    );
    expect(states(element)[1]).toBe('Changed');
  });

  it('reads its rows again from the page shown, once a cell being edited is', async () => {
    const { element, http, api, shown, changes } = await opened({
      page: editablePageOf(orderRows(3), { total: 7, hasMore: true }),
    });
    api().paginationGoToPage(1);
    await shown();
    await answerPage(http, editablePageOf(orderRows(3, 3), { offset: 3, total: 7, hasMore: true }));
    await shown();
    api().startEditingCell({ rowIndex: 3, colKey: 'c1' });
    await shown();
    (TestBed.inject(ChangesChannel) as unknown as FakeChangesChannel).hear(1, true);
    await shown();
    expect(http.match((request) => request.url === '/api/browse/page').length).toBe(0);
    expect(element.querySelector('.ag-cell-inline-editing')).not.toBeNull();
    api().stopEditing(true);
    await shown();
    const asked = await answerPage(
      http,
      editablePageOf(orderRows(3, 3), { offset: 3, total: 7, hasMore: true }),
    );
    expect(asked.grid).toMatchObject({ offset: 3 });
    await shown();
    expect(changes.count()).toBe(0);
  });

  it('reads its rows again, from the page shown, once changes are committed here or in another tab', async () => {
    const paid = changeOf({
      key: ['1002'],
      rowId: '["1002"]',
      values: { status: 'paid' },
      original: { status: 'open' },
    });
    const { element, http, shown, changes } = await opened({ changes: setOf([paid]) });
    expect(gridCells(element)[1]).toEqual(['1002', 'paid', '1.50']);
    const committing = changes.commit({ planId: 'plan-1', version: 1, allowAnyStatement: false });
    (await requestTo(http, commitUrl, 'POST')).flush(resultOf());
    await committing;
    await shown();
    const rows = orderRows(3).map((row, index) =>
      index === 1 ? rowOf(['1002', 'paid', '1.50']) : row,
    );
    const asked = await answerPage(http, editablePageOf(rows, { total: 3 }));
    expect(asked.grid).toMatchObject({ offset: 0 });
    await shown();
    expect(gridCells(element)[1]).toEqual(['1002', 'paid', '1.50']);
    expect(states(element)[1]).toBe('');

    (TestBed.inject(ChangesChannel) as unknown as FakeChangesChannel).hear(5, true);
    await answerChanges(http, setOf([], 5));
    await answerPage(http, editablePageOf(rows, { total: 3 }));
    await shown();
    // Not for a commit that wrote nothing.
    const failing = changes.commit({ planId: 'plan-2', version: 5, allowAnyStatement: false });
    (await requestTo(http, commitUrl, 'POST')).flush(
      resultOf({ outcome: 'rolledBack', changes: setOf([], 5) }),
    );
    await failing;
    await shown();
    expect(http.match((request) => request.url === '/api/browse/page').length).toBe(0);
  });

  it('offers no changes to readers', async () => {
    const reader = await opened({ role: 'read' });
    expect(reader.element.querySelector('.edit-bar')).toBeNull();
    expect(reader.element.querySelector('[col-id=gd-state]')).toBeNull();
  });

  it('offers no changes where the entity takes none', async () => {
    const { element } = await opened({ page: pageOf(orderRows(3), {}, linkedColumns()) });
    expect(element.querySelector('.edit-bar')).toBeNull();
    expect(element.querySelector('[col-id=gd-state]')).toBeNull();
  });

  it('changes a cell edited: shown at once, marked, and kept as a pending change', async () => {
    const { element, http, api, shown, changes } = await opened();
    api().startEditingCell({ rowIndex: 0, colKey: 'c2' });
    await shown();
    const input = element.querySelector<HTMLInputElement>('.ag-cell-inline-editing input')!;
    expect(input.value).toBe('0.50');
    input.value = '9.95';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    api().stopEditing();
    await shown();
    expect(gridCells(element)[0]).toEqual(['1001', 'NULL', '9.95']);
    expect(element.querySelector('.ag-row[row-index="0"] [col-id=c2]')?.classList).toContain(
      'gd-dirty',
    );
    expect(states(element)[0]).toBe('Changed');

    const request = await requestTo(http, opsUrl, 'POST');
    expect(request.request.body).toEqual({
      ops: [
        {
          op: 'set',
          entity: 'shop.orders',
          key: ['1001'],
          values: { total: '9.95' },
          original: { total: '0.50' },
          display: {},
        },
      ],
    });
    request.flush(setOf([changeOf({ values: { total: '9.95' }, original: { total: '0.50' } })], 2));
    await shown();
    expect(changes.count()).toBe(1);
    expect(gridCells(element)[0]).toEqual(['1001', 'NULL', '9.95']);
  });

  it("says why a value typed can't be, and sends nothing", async () => {
    const { element, api, shown } = await opened();
    api().getDisplayedRowAtIndex(1)!.setDataValue('c2', '1.555');
    await shown();
    expect(alertsOf(element)).toContain(
      "Couldn't change total: 1.555 has more than 2 places after the point",
    );
    expect(gridCells(element)[1]).toEqual(['1002', 'open', '1.50']);
    await clickButton(element, 'Dismiss');
    await shown();
    expect(alertsOf(element)).toBe('');
  });

  it('undoes a change the server refuses, and says why', async () => {
    const { element, http, api, shown } = await opened();
    api().getDisplayedRowAtIndex(1)!.setDataValue('c1', 'gone');
    // At once, as the grid draws the cell again.
    expect(gridCells(element)[1]).toEqual(['1002', 'gone', '1.50']);
    await shown();
    expect(gridCells(element)[1]).toEqual(['1002', 'gone', '1.50']);
    (await requestTo(http, opsUrl, 'POST')).flush(
      problemBody('invalid-request', 'Invalid', {
        errors: { 'ops[0].entity': ['shop.orders takes no changes now'] },
      }),
      { status: 400, statusText: 'Bad Request' },
    );
    await shown();
    expect(gridCells(element)[1]).toEqual(['1002', 'open', '1.50']);
    expect(alertsOf(element)).toContain(
      "Couldn't change the row: shop.orders takes no changes now",
    );
  });

  it('deletes the row the keyboard is on with the values it had, and restores it', async () => {
    const { element, http, api, shown } = await opened();
    api().setFocusedCell(2, 'c1');
    await shown();
    await clickButton(element, 'delete Delete the row');
    await shown();
    expect(element.querySelector('.ag-row[row-index="2"]')?.classList).toContain('gd-deleted');
    expect(states(element)[2]).toBe('To be deleted');
    const request = await requestTo(http, opsUrl, 'POST');
    expect(request.request.body).toEqual({
      ops: [
        {
          op: 'delete',
          entity: 'shop.orders',
          key: ['1003'],
          original: { id: '1003', status: null, total: '2.50' },
        },
      ],
    });
    request.flush(
      setOf([changeOf({ kind: 'delete', key: ['1003'], rowId: '["1003"]', values: {} })], 2),
    );
    await shown();
    // Its cells can't be changed now.
    api().startEditingCell({ rowIndex: 2, colKey: 'c1' });
    await shown();
    expect(element.querySelector('.ag-cell-inline-editing')).toBeNull();

    await clickButton(element, 'restore_from_trash Restore the row');
    await shown();
    expect(element.querySelector('.ag-row[row-index="2"]')?.classList).not.toContain('gd-deleted');
    const revert = await requestTo(http, opsUrl, 'POST');
    expect(revert.request.body).toEqual({
      ops: [{ op: 'revert', entity: 'shop.orders', key: ['1003'] }],
    });
    revert.flush(setOf([], 3));
    await shown();
  });

  it('adds a row at the top, with what new rows start with, editing the first value it needs', async () => {
    const { element, http, api, shown } = await opened({
      defaults: { values: { status: 'new' }, display: {} },
    });
    await clickButton(element, 'add Add a row');
    await shown();
    const pinned = api().getPinnedTopRow(0)?.data as GridRow & { tempId: string };
    expect(pinned.tempId).toMatch(/^new-/);
    const request = await requestTo(http, opsUrl, 'POST');
    expect(request.request.body).toEqual({
      ops: [
        {
          op: 'insert',
          entity: 'shop.orders',
          tempId: pinned.tempId,
          values: { status: 'new' },
          display: {},
        },
      ],
    });
    const row = element.querySelector('.ag-grid-pinned-top-rows .ag-row');
    expect(row?.classList).toContain('gd-inserted');
    expect(textOf(row?.querySelector('[col-id=c1]'))).toBe('new');
    // The key is needed, so it is edited first.
    expect(
      element
        .querySelector('.ag-grid-pinned-top-rows .ag-row .ag-cell-inline-editing')
        ?.getAttribute('col-id'),
    ).toBe('c0');
    api().stopEditing(true);
    request.flush(setOf([insertOf(pinned.tempId, { values: { status: 'new' } })], 2));
    await shown();
    expect(api().getPinnedTopRow(0)?.data).toBe(pinned);
    expect(newRowCells(element)).toEqual([['', 'new', '']]);
    expect(textOf(element.querySelector('.ag-grid-pinned-top-rows .ag-row [col-id=c2]'))).toBe('');
    expect(
      element.querySelector('.ag-grid-pinned-top-rows .ag-row [col-id=c2]')?.classList,
    ).toContain('gd-invalid');

    // Its values are set by its temporary id, without originals.
    api().getPinnedTopRow(0)!.setDataValue('c2', '5');
    await shown();
    const set = await requestTo(http, opsUrl, 'POST');
    expect(set.request.body).toEqual({
      ops: [
        {
          op: 'set',
          entity: 'shop.orders',
          tempId: pinned.tempId,
          values: { total: '5' },
          display: {},
        },
      ],
    });
    set.flush(setOf([insertOf(pinned.tempId, { values: { status: 'new', total: '5' } })], 3));
    await shown();

    // Dropped, it goes.
    api().setFocusedCell(0, 'c1', 'top');
    await shown();
    await clickButton(element, 'delete Drop the new row');
    await shown();
    expect(api().getPinnedTopRowCount()).toBe(0);
    const drop = await requestTo(http, opsUrl, 'POST');
    expect(drop.request.body).toEqual({
      ops: [{ op: 'delete', entity: 'shop.orders', tempId: pinned.tempId }],
    });
    drop.flush(setOf([], 4));
    await shown();
  });

  it('reverts a cell from the inspector, and the row from the bar', async () => {
    const change = changeOf({
      values: { status: 'paid', total: '9.99' },
      original: { status: null, total: '0.50' },
    });
    const { element, http, api, shown } = await opened({ changes: setOf([change]) });
    expect(gridCells(element)[0]).toEqual(['1001', 'paid', '9.99']);
    api().setFocusedCell(0, 'c1');
    await shown();
    const inspector = element.querySelector<HTMLElement>('gd-grid-inspector')!;
    expect(textOf(inspector)).toContain('Changed, not committed: it was NULL.');
    const revert = [...inspector.querySelectorAll<HTMLElement>('button')].find(
      (button) => textOf(button) === 'Revert',
    )!;
    revert.focus();
    revert.click();
    await shown();
    expect(gridCells(element)[0]).toEqual(['1001', 'NULL', '9.99']);
    // The button went with the change: the keyboard is back on the cell.
    expect(document.activeElement?.getAttribute('col-id')).toBe('c1');
    const reverted = await requestTo(http, opsUrl, 'POST');
    expect(reverted.request.body).toEqual({
      ops: [{ op: 'revert', entity: 'shop.orders', key: ['1001'], columns: ['status'] }],
    });
    reverted.flush(
      setOf([changeOf({ values: { total: '9.99' }, original: { total: '0.50' } })], 2),
    );
    await shown();

    await clickButton(element, 'undo Revert the row');
    await shown();
    expect(gridCells(element)[0]).toEqual(['1001', 'NULL', '0.50']);
    const row = await requestTo(http, opsUrl, 'POST');
    expect(row.request.body).toEqual({
      ops: [{ op: 'revert', entity: 'shop.orders', key: ['1001'] }],
    });
    row.flush(setOf([], 3));
    await shown();
  });

  it('marks values changed elsewhere since they were changed here', async () => {
    const change = changeOf({ values: { total: '9.99' }, original: { total: '0.25' } });
    const { element, api, shown } = await opened({ changes: setOf([change]) });
    expect(element.querySelector('.ag-row[row-index="0"] [col-id=c2]')?.classList).toContain(
      'gd-conflict',
    );
    api().setFocusedCell(0, 'c2');
    await shown();
    expect(textOf(element.querySelector('gd-grid-inspector'))).toContain(
      'Changed elsewhere since: it is 0.50 now.',
    );
  });

  it('shows changes read again (another tab made them)', async () => {
    const { element, http, shown, changes } = await opened();
    changes.reload();
    await answerChanges(
      http,
      setOf([changeOf({ key: ['1002'], rowId: '["1002"]', values: { status: 'shipped' } })], 2),
    );
    await shown();
    expect(gridCells(element)[1]).toEqual(['1002', 'shipped', '1.50']);
    expect(states(element)).toEqual(['', 'Changed', '']);
  });

  /** Presses a key on the cell the keyboard is on, as the browser would. */
  function press(init: KeyboardEventInit): void {
    document.activeElement?.dispatchEvent(
      new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init }),
    );
  }

  it('offers no changes where the page offers none', async () => {
    const { element } = await opened({ editing: false });
    expect(element.querySelector('.edit-bar')).toBeNull();
    expect(element.querySelector('[col-id=gd-state]')).toBeNull();
  });

  it('changes rows by the keys: Delete sets NULL, Ctrl+Z reverts, Ctrl+Delete deletes, each said', async () => {
    const { element, http, api, shown } = await opened();
    const announced = vi.spyOn(TestBed.inject(LiveAnnouncer), 'announce');
    api().setFocusedCell(1, 'c1');
    await shown();
    press({ key: 'Delete' });
    await shown();
    expect(gridCells(element)[1]).toEqual(['1002', 'NULL', '1.50']);
    const cleared = await requestTo(http, opsUrl, 'POST');
    expect(cleared.request.body).toMatchObject({
      ops: [{ op: 'set', key: ['1002'], values: { status: null }, original: { status: 'open' } }],
    });
    cleared.flush(
      setOf(
        [
          changeOf({
            key: ['1002'],
            rowId: '["1002"]',
            values: { status: null },
            original: { status: 'open' },
          }),
        ],
        2,
      ),
    );
    await shown();

    press({ key: 'z', ctrlKey: true });
    await shown();
    expect(gridCells(element)[1]).toEqual(['1002', 'open', '1.50']);
    const reverted = await requestTo(http, opsUrl, 'POST');
    expect(reverted.request.body).toEqual({
      ops: [{ op: 'revert', entity: 'shop.orders', key: ['1002'], columns: ['status'] }],
    });
    reverted.flush(setOf([], 3));
    await shown();
    expect(announced).toHaveBeenCalledWith('status of row 1002 reverted', 'polite');

    press({ key: 'Delete', ctrlKey: true });
    await shown();
    expect(element.querySelector('.ag-row[row-index="1"]')?.classList).toContain('gd-deleted');
    (await requestTo(http, opsUrl, 'POST')).flush(
      setOf([changeOf({ kind: 'delete', key: ['1002'], rowId: '["1002"]', values: {} })], 4),
    );
    await shown();
    expect(announced).toHaveBeenCalledWith('Row 1002 to be deleted', 'polite');
    const bar = element.querySelector('.edit-bar')!;
    expect(
      [...bar.querySelectorAll('button')].map((button) => button.getAttribute('aria-label')),
    ).toEqual([null, 'Restore the row: row 1002', 'Revert the row: row 1002']);
  });

  it("says why a row can't be deleted", async () => {
    const page = editablePageOf(orderRows(3), { total: 3 });
    const { element, api, shown } = await opened({
      page: {
        ...page,
        schema: page.schema && {
          ...page.schema,
          capabilities: {
            canInsert: true,
            canUpdate: true,
            canDelete: false,
            changeReason: 'Rows of shop.orders are kept',
          },
        },
      },
    });
    api().setFocusedCell(0, 'c1');
    await shown();
    const button = [...element.querySelectorAll<HTMLElement>('.edit-bar button')].find(
      (each) => textOf(each) === 'delete Delete the row',
    )!;
    expect(button.getAttribute('aria-disabled')).toBe('true');
    press({ key: 'Delete', ctrlKey: true });
    await shown();
    expect(alertsOf(element)).toContain("Couldn't delete the row: Rows of shop.orders are kept");
  });

  it("shows the new rows of the crumb's row, not those of other rows", async () => {
    const { element, http, api, shown } = await opened({
      defaults: { values: { status: 'open' }, display: {} },
      changes: setOf([
        insertOf('here', { values: { status: 'open', total: '1' } }),
        insertOf('there', { id: 2, values: { status: 'closed' } }),
      ]),
    });
    expect(api().getPinnedTopRowCount()).toBe(1);
    expect(newRowCells(element)).toEqual([['', 'open', '1']]);
    // Another's reference changed to this row's, it is shown here too.
    api().setFocusedCell(0, 'c1', 'top');
    await shown();
    expect(textOf(element.querySelector('gd-grid-inspector'))).toContain('A new row');
    TestBed.inject(PendingChanges).set('shop.orders', { tempId: 'there' }, { status: 'open' });
    await shown();
    expect(api().getPinnedTopRowCount()).toBe(2);
    (await requestTo(http, opsUrl, 'POST')).flush(setOf([], 2));
    await shown();
  });

  it('chooses the row a reference refers to, in the picker (F2)', async () => {
    const columns = linkedColumns().map((column, index) =>
      index === 1 ? { ...column, canUpdate: true } : column,
    );
    const page = editablePageOf(
      [{ ...rowOf(['1001', '42', 'open']), r: ['Acme'] }],
      { total: 1 },
      columns,
    );
    const { element, http, api, shown } = await opened({
      page: { ...page, schema: page.schema && { ...page.schema, references: [customerReference] } },
    });
    api().setFocusedCell(0, 'c1');
    await shown();
    const open = vi.spyOn(TestBed.inject(MatDialog), 'open');
    document.activeElement?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'F2', bubbles: true, cancelable: true }),
    );
    await shown();
    const dialog = document.querySelector<HTMLElement>('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Choose a row of shop.customers');
    // It opens at the row the reference refers to: the dialog takes the keyboard (not its first field) till the
    // row is found.
    expect(open.mock.calls[0][1]).toMatchObject({ autoFocus: 'dialog' });
    expect(await answerPosition(http)).toEqual({
      entity: 'shop.customers',
      columns: ['id'],
      values: ['42'],
    });
    await shown();
    const asked = await answerPage(
      http,
      pageOf([rowOf(['42', 'Acme']), rowOf(['43', 'Beta'])], { entity: 'shop.customers' }, [
        { ...columns[0], name: 'id' },
        { ...columns[2], name: 'name' },
      ]),
    );
    expect(asked.source).toEqual({ entity: 'shop.customers' });
    await shown();
    // No row may be chosen: customer_id may be NULL.
    expect([...dialog.querySelectorAll('button')].map((button) => textOf(button))).toContain(
      'No row',
    );
    dialog.querySelector<HTMLElement>('.ag-row[row-index="1"] [col-id=c1]')!.click();
    await shown();
    await clickButton(dialog, 'Choose');
    await shown();
    expect(document.querySelector('mat-dialog-container')).toBeNull();
    const request = await requestTo(http, opsUrl, 'POST');
    expect(request.request.body).toEqual({
      ops: [
        {
          op: 'set',
          entity: 'shop.orders',
          key: ['1001'],
          values: { customer_id: '43' },
          original: { customer_id: '42' },
          display: { customer: 'Beta' },
        },
      ],
    });
    expect(textOf(element.querySelector('.ag-row[row-index="0"] [col-id=c1]'))).toBe('Beta43');
    expect(element.querySelector('.ag-row[row-index="0"] [col-id=c1] a')).toBeNull();
    request.flush(setOf([], 2));
    await shown();
  });

  it('opens the picker at the row the reference refers to as changed', async () => {
    const columns = linkedColumns().map((column, index) =>
      index === 1 ? { ...column, canUpdate: true } : column,
    );
    const page = editablePageOf(
      [{ ...rowOf(['1001', '42', 'open']), r: ['Acme'] }],
      { total: 1 },
      columns,
    );
    const { http, api, shown } = await opened({
      page: { ...page, schema: page.schema && { ...page.schema, references: [customerReference] } },
      changes: setOf([
        changeOf({ key: ['1001'], values: { customer_id: '43' }, original: { customer_id: '42' } }),
      ]),
    });
    api().setFocusedCell(0, 'c1');
    await shown();
    press({ key: 'F2' });
    await shown();
    expect((await answerPosition(http)).values).toEqual(['43']);
    await shown();
    await answerPage(
      http,
      pageOf([rowOf(['43', 'Beta'])], { entity: 'shop.customers' }, [
        { ...columns[0], name: 'id' },
        { ...columns[2], name: 'name' },
      ]),
    );
    await shown();
    clickButton(document.querySelector<HTMLElement>('mat-dialog-container')!, 'Cancel');
    await shown();
  });

  it('opens the picker at its first page, nothing looked for, for a reference to no row', async () => {
    const columns = linkedColumns().map((column, index) =>
      index === 1 ? { ...column, canUpdate: true } : column,
    );
    const page = editablePageOf(
      [{ ...rowOf(['1001', null, 'open']), r: [null] }],
      { total: 1 },
      columns,
    );
    const { http, api, shown } = await opened({
      page: { ...page, schema: page.schema && { ...page.schema, references: [customerReference] } },
    });
    api().setFocusedCell(0, 'c1');
    await shown();
    const open = vi.spyOn(TestBed.inject(MatDialog), 'open');
    press({ key: 'F2' });
    await shown();
    expect(open.mock.calls[0][1]).toMatchObject({
      autoFocus: 'first-tabbable',
      data: { values: null },
    });
    const asked = await answerPage(
      http,
      pageOf([rowOf(['42', 'Acme'])], { entity: 'shop.customers' }, [
        { ...columns[0], name: 'id' },
        { ...columns[2], name: 'name' },
      ]),
    );
    expect(asked.grid?.offset ?? 0).toBe(0);
    await shown();
    clickButton(document.querySelector<HTMLElement>('mat-dialog-container')!, 'Cancel');
    await shown();
  });

  /** shop.orders' rows with a composite reference: customer by (customer_id, tenant_id), and tenant by tenant_id. */
  function tenantsPage(nullable = false) {
    const type = { kind: 'int64' as const, nullable, text: nullable ? 'int64?' : 'int64' };
    const columns: GridColumn[] = [
      { ...linkedColumns()[0] },
      { ...linkedColumns()[1], name: 'tenant_id', canUpdate: true, reference: 0, type },
      { ...linkedColumns()[1], name: 'customer_id', canUpdate: true, reference: 1, type },
    ];
    const references: GridReference[] = [
      { ...customerReference, navigation: 'tenant', target: 'shop.tenants', columns: [1] },
      {
        ...customerReference,
        columns: [2, 1],
        targetColumns: ['id', 'tenant_id'],
        displayColumn: null,
      },
    ];
    const page = editablePageOf(
      [{ ...rowOf(['10', '1', '5']), r: ['One', 'Acme'] }],
      { total: 1 },
      columns,
    );
    return { ...page, schema: page.schema && { ...page.schema, references } };
  }

  /** The picker opened on the customer's cell, its rows answered. */
  async function picker(
    it: Awaited<ReturnType<typeof opened>>,
    columns: GridColumn[],
    rows: GridRow[],
  ): Promise<HTMLElement> {
    it.api().setFocusedCell(0, 'c2');
    await it.shown();
    press({ key: 'F2' });
    // Twice: the picker opens once.
    press({ key: 'F2' });
    await it.shown();
    expect(document.querySelectorAll('mat-dialog-container').length).toBe(1);
    // Found by the target's columns, in their order.
    expect(await answerPosition(it.http)).toEqual({
      entity: 'shop.customers',
      columns: ['id', 'tenant_id'],
      values: ['5', '1'],
    });
    await it.shown();
    await answerPage(it.http, pageOf(rows, { entity: 'shop.customers' }, columns));
    await it.shown();
    return document.querySelector<HTMLElement>('mat-dialog-container')!;
  }

  const customers = (columns: string[]) =>
    columns.map((name, index) => ({ ...linkedColumns()[index === 0 ? 0 : 2], name }));

  it('sets all the columns of a composite reference to the row chosen, in their order', async () => {
    const it = await opened({ page: tenantsPage() });
    const dialog = await picker(it, customers(['tenant_id', 'id', 'name']), [
      rowOf(['1', '5', 'Acme']),
      rowOf(['2', '7', 'Beta']),
    ]);
    expect([...dialog.querySelectorAll('button')].map((button) => textOf(button))).not.toContain(
      'No row',
    );
    dialog.querySelector<HTMLElement>('.ag-row[row-index="1"] [col-id=c2]')!.click();
    await it.shown();
    clickButton(dialog, 'Choose');
    await it.shown();
    const request = await requestTo(it.http, opsUrl, 'POST');
    expect(request.request.body).toEqual({
      ops: [
        {
          op: 'set',
          entity: 'shop.orders',
          key: ['10'],
          values: { customer_id: '7', tenant_id: '2' },
          original: { customer_id: '5', tenant_id: '1' },
          // The customers show no column for their rows: nothing is shown for the one chosen.
          display: { customer: null },
        },
      ],
    });
    request.flush(setOf([], 2));
    await it.shown();
  });

  it("says when the rows chosen from haven't a column the reference needs", async () => {
    const it = await opened({ page: tenantsPage() });
    const dialog = await picker(it, customers(['id', 'name']), [rowOf(['7', 'Beta'])]);
    dialog.querySelector<HTMLElement>('.ag-row[row-index="0"] [col-id=c1]')!.click();
    await it.shown();
    clickButton(dialog, 'Choose');
    await it.shown();
    expect(alertsOf(it.element)).toContain(
      "Couldn't change tenant_id: The rows chosen from have no tenant_id column",
    );
  });

  it('sets a reference to refer to no row, with nothing shown for it', async () => {
    const it = await opened({ page: tenantsPage(true) });
    const dialog = await picker(it, customers(['tenant_id', 'id', 'name']), [
      rowOf(['1', '5', 'Acme']),
    ]);
    clickButton(dialog, 'No row');
    await it.shown();
    const request = await requestTo(it.http, opsUrl, 'POST');
    expect(request.request.body).toMatchObject({
      ops: [{ values: { customer_id: null, tenant_id: null }, display: { customer: null } }],
    });
    expect(textOf(it.element.querySelector('.ag-row[row-index="0"] [col-id=c2]'))).toBe('NULL');
    request.flush(setOf([], 2));
    await it.shown();
  });

  it("says a new row's reference not given is its default, not NULL", async () => {
    const page = tenantsPage(true);
    const { element, api, shown, http } = await opened({
      page,
      changes: setOf([insertOf('t1')]),
    });
    api().setFocusedCell(0, 'c2', 'top');
    await shown();
    const inspector = textOf(element.querySelector('gd-grid-inspector'));
    expect(inspector).toContain("The column's default");
    expect(inspector).not.toContain('refers to no row');
    expect(inspector).toContain('F2, or a double click, chooses the row it refers to.');

    // The picker for it opens at the first page, nothing looked for, the keyboard on its first field.
    const open = vi.spyOn(TestBed.inject(MatDialog), 'open');
    press({ key: 'F2' });
    await shown();
    expect(open.mock.calls[0][1]).toMatchObject({
      autoFocus: 'first-tabbable',
      data: { values: null },
    });
    await answerPage(http, pageOf([], { entity: 'shop.customers' }, customers(['id', 'name'])));
    await shown();
    clickButton(document.querySelector<HTMLElement>('mat-dialog-container')!, 'Cancel');
    await shown();
  });
});
