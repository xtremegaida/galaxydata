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
  columnOf,
  customerReference,
  pageOf,
  pagesFetched,
  rowOf,
} from '../../../../testing/browse';
import { settle } from '../../../../testing/http';
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

  /** The picker for shop.orders' customer, shop.customers' rows shown in it. */
  async function opened(nullable = true) {
    const dialog = TestBed.inject(MatDialog);
    const ref = dialog.open<NavPicker, NavPickerData, NavPicked>(NavPicker, {
      data: { entity: 'shop.orders', reference: customerReference, nullable },
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
    const asked = await answerPage(
      http,
      pageOf(
        [rowOf(['42', 'Acme']), rowOf(['43', 'Beta'])],
        { entity: 'shop.customers' },
        customers,
      ),
    );
    await shown();
    const element = document.querySelector<HTMLElement>('mat-dialog-container')!;
    return { element, closed, asked, shown };
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

  it("doesn't offer no row where the reference must refer to one, and chooses nothing when cancelled", async () => {
    const { element, closed } = await opened(false);
    expect([...element.querySelectorAll('button')].map((b) => textOf(b))).not.toContain('No row');
    clickButton(element, 'Cancel');
    expect(await closed).toBeUndefined();
  });
});
