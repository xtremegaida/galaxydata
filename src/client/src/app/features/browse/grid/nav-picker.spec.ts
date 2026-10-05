import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { fakeBrowserProviders } from '../../../../testing/auth';
import {
  answerPage,
  answerPosition,
  columnOf,
  customerReference,
  pageOf,
  pagesFetched,
  rowOf,
} from '../../../../testing/browse';
import { requestTo, settle } from '../../../../testing/http';
import { clickButton, textOf } from '../../../../testing/pages';
import { BROWSE_PAGE_SIZE } from './browse-grid';
import { NavPicker, type NavPicked, type NavPickerData } from './nav-picker';

describe('NavPicker', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        fakeBrowserProviders(),
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: BROWSE_PAGE_SIZE, useValue: 3 },
        provideRouter([]),
      ],
    });
  });

  afterEach(async () => {
    await pagesFetched();
    TestBed.inject(HttpTestingController).verify();
  });

  const customers = [columnOf('id', 'int64', { isKey: true }), columnOf('name')];

  /** The picker for shop.orders' customer, opened; the values the reference holds, if any, found first. */
  async function opening(nullable = true, values: readonly unknown[] | null = null) {
    const dialog = TestBed.inject(MatDialog);
    const ref = dialog.open<NavPicker, NavPickerData, NavPicked>(NavPicker, {
      data: { entity: 'shop.orders', reference: customerReference, nullable, values },
    });
    const closed = firstValueFrom(ref.afterClosed());
    const http = TestBed.inject(HttpTestingController);
    const shown = async () => {
      for (let turn = 0; turn < 3; turn++) {
        await settle();
        TestBed.tick();
      }
      await pagesFetched();
      TestBed.tick();
    };
    await shown();
    const element = document.querySelector<HTMLElement>('mat-dialog-container')!;
    return { element, closed, http, shown };
  }

  /** The picker for shop.orders' customer, shop.customers' rows shown in it. */
  async function opened(nullable = true) {
    const it = await opening(nullable);
    const asked = await answerPage(
      it.http,
      pageOf(
        [rowOf(['42', 'Acme']), rowOf(['43', 'Beta'])],
        { entity: 'shop.customers' },
        customers,
      ),
    );
    await it.shown();
    return { ...it, asked };
  }

  it("shows the target's rows, without links, changes or the inspector", async () => {
    const { element, asked } = await opened();
    expect(asked.source).toEqual({ entity: 'shop.customers' });
    expect(textOf(element.querySelector('h2'))).toBe('Choose a row of shop.customers');
    expect(textOf(element.querySelector('.aside'))).toContain(
      'The row customer of shop.orders refers to.',
    );
    expect(element.querySelector('.edit-bar')).toBeNull();
    expect(element.querySelector('gd-grid-inspector')).toBeNull();
    // It refers to none: nothing was looked for, and nothing is said.
    expect(textOf(element.querySelector('[role=status]'))).toBe('');
    const choose = [...element.querySelectorAll('button')].find((b) => textOf(b) === 'Choose');
    expect(choose?.getAttribute('aria-disabled')).toBe('true');
  });

  it('chooses the row clicked', async () => {
    const { element, closed, shown } = await opened();
    element.querySelector<HTMLElement>('.ag-row[row-index="1"] [col-id=c1]')!.click();
    await shown();
    clickButton(element, 'Choose');
    const picked = await closed;
    expect(picked).toEqual({ row: rowOf(['43', 'Beta']), columns: customers });
  });

  it('chooses the row double-clicked, or Enter on it', async () => {
    const { element, closed } = await opened();
    element
      .querySelector<HTMLElement>('.ag-row[row-index="0"] [col-id=c1]')!
      .dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    expect(await closed).toMatchObject({ row: rowOf(['42', 'Acme']) });
  });

  it('chooses the row Enter is pressed on', async () => {
    const { element, closed } = await opened();
    element
      .querySelector<HTMLElement>('.ag-row[row-index="1"] [col-id=c0]')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    expect(await closed).toMatchObject({ row: rowOf(['43', 'Beta']) });
  });

  it('chooses no row, where the reference may refer to none', async () => {
    const { element, closed } = await opened();
    clickButton(element, 'No row');
    expect(await closed).toEqual({ none: true });
  });

  it('opens at the page of the row the reference refers to, chosen, with the keyboard on it', async () => {
    const { element, closed, http, shown } = await opening(true, ['45']);
    expect(element.querySelector('mat-progress-bar')?.getAttribute('aria-label')).toBe(
      'Finding the row it refers to',
    );
    expect(element.querySelector('gd-browse-grid')).toBeNull();
    // The sixth of shop.customers' rows, by their key: the last on the second page of three.
    expect(await answerPosition(http, { id: '["45"]', index: 5 })).toEqual({
      entity: 'shop.customers',
      columns: ['id'],
      values: ['45'],
    });
    await shown();
    const asked = await answerPage(
      http,
      pageOf(
        [rowOf(['43', 'Beta']), rowOf(['44', 'Gamma']), rowOf(['45', 'Delta'])],
        { entity: 'shop.customers', offset: 3, total: 9 },
        customers,
      ),
    );
    expect(asked.grid?.offset).toBe(3);
    await shown();
    const row = element.querySelector('.ag-row[row-index="5"]')!;
    expect(row.classList).toContain('ag-row-selected');
    expect(row.contains(document.activeElement)).toBe(true);
    expect(textOf(element.querySelector('[role=status]'))).toBe('');
    // Only its page was asked for.
    await pagesFetched();
    expect(http.match((request) => request.url.endsWith('/page'))).toEqual([]);
    clickButton(element, 'Choose');
    expect(await closed).toMatchObject({ row: rowOf(['45', 'Delta']) });
  });

  it('puts the keyboard on the condition when the page it opens at hasn’t the row', async () => {
    const { element, http, shown } = await opening(true, ['44']);
    await answerPosition(http, { id: '["44"]', index: 0 });
    await shown();
    await answerPage(http, pageOf([rowOf(['42', 'Acme'])], {}, customers));
    await shown();
    expect(element.querySelector('.ag-row-selected')).toBeNull();
    expect(document.activeElement).toBe(element.querySelector('gd-browse-grid input'));
  });

  it('leaves the keyboard where it was put while the row was found', async () => {
    const { element, http, shown } = await opening(true, ['42']);
    const cancel = [...element.querySelectorAll('button')].find((b) => textOf(b) === 'Cancel')!;
    cancel.focus();
    await answerPosition(http, { id: '["42"]', index: 0 });
    await shown();
    await answerPage(http, pageOf([rowOf(['42', 'Acme'])], {}, customers));
    await shown();
    expect(element.querySelector('.ag-row[row-index="0"]')!.classList).toContain('ag-row-selected');
    expect(document.activeElement).toBe(cancel);
  });

  it('opens at the first page with the row chosen, when where it is can’t be told', async () => {
    const { element, http, shown } = await opening(true, ['42']);
    await answerPosition(http, { id: '["42"]', index: null });
    await shown();
    expect(
      (await answerPage(http, pageOf([rowOf(['42', 'Acme'])], {}, customers))).grid?.offset ?? 0,
    ).toBe(0);
    await shown();
    expect(element.querySelector('.ag-row[row-index="0"]')!.classList).toContain('ag-row-selected');
    expect(textOf(element.querySelector('[role=status]'))).toBe(
      "Where the row it refers to is among these can't be told: it is chosen if it is on this page.",
    );
  });

  it('opens at the first page when no row has the values it refers to, or they can’t be found, saying so', async () => {
    const missing = await opening(true, ['77']);
    await answerPosition(missing.http);
    await missing.shown();
    expect(textOf(missing.element.querySelector('[role=status]'))).toBe(
      'No row of shop.customers has the values it refers to.',
    );
    expect((await answerPage(missing.http, pageOf([], {}, customers))).grid?.offset ?? 0).toBe(0);
    await missing.shown();
    clickButton(missing.element, 'Cancel');
    await missing.closed;

    const failing = await opening(true, ['42']);
    (await requestTo(failing.http, '/api/browse/position', 'POST')).flush(null, {
      status: 504,
      statusText: 'Gateway timeout',
    });
    await failing.shown();
    expect(textOf(failing.element.querySelector('[role=status]'))).toMatch(
      /^The row it refers to couldn't be found: /,
    );
    expect((await answerPage(failing.http, pageOf([], {}, customers))).grid?.offset ?? 0).toBe(0);
    await failing.shown();
  });

  it("doesn't offer no row where the reference must refer to one, and chooses nothing when cancelled", async () => {
    const { element, closed } = await opened(false);
    expect([...element.querySelectorAll('button')].map((b) => textOf(b))).not.toContain('No row');
    clickButton(element, 'Cancel');
    expect(await closed).toBeUndefined();
  });
});
