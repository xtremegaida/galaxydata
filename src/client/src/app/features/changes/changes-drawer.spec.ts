import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { fakeBrowserProviders, problemBody, sessionOf } from '../../../testing/auth';
import {
  FakeChangesChannel,
  answerChanges,
  changeOf,
  changesUrl,
  insertOf,
  opsUrl,
  setOf,
} from '../../../testing/changes';
import { requestTo, settle } from '../../../testing/http';
import { alertsOf, clickButton, textOf } from '../../../testing/pages';
import { AuthStore } from '../../core/auth/auth-store';
import { type ChangeSet, ChangesChannel } from '../../core/changes/pending-changes';
import { ChangesDrawer } from './changes-drawer';

describe('ChangesDrawer', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        fakeBrowserProviders(),
        { provide: ChangesChannel, useClass: FakeChangesChannel },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** The drawer of a data manager, their changes read (when given). */
  async function opened(set: ChangeSet | null = setOf()) {
    const auth = TestBed.inject(AuthStore);
    const signedIn = auth.ensure();
    http.expectOne('/api/auth/session').flush(sessionOf('dataManager'));
    await signedIn;
    const fixture = TestBed.createComponent(ChangesDrawer);
    fixture.detectChanges();
    if (set) {
      await answerChanges(http, set);
    }
    const shown = async () => {
      await settle();
      fixture.detectChanges();
    };
    await shown();
    return { fixture, element: fixture.nativeElement as HTMLElement, shown };
  }

  const changes = setOf([
    changeOf({
      values: { status: 'paid', placed_at: '2026-03-01T10:30:00' },
      original: { status: null, placed_at: '2026-03-01T09:00:00' },
    }),
    insertOf('t1', { id: 2, values: { customer_id: '43' }, display: { customer: 'Beta' } }),
    changeOf({ id: 3, kind: 'delete', key: ['1004'], rowId: '["1004"]', values: {} }),
    changeOf({
      id: 4,
      source: 'wh',
      entity: 'wh.stock',
      key: ['7', 'A'],
      rowId: '["7","A"]',
      values: { qty: 5 },
      original: { qty: 4 },
    }),
  ]);

  it('lists the changes by connection, entity and row', async () => {
    const { element } = await opened(changes);
    expect(textOf(element.querySelector('.summary'))).toBe('4 rows changed, not committed.');
    const sources = [...element.querySelectorAll<HTMLElement>('section.source')];
    expect(sources.map((source) => textOf(source.querySelector('h3')))).toEqual([
      'database shop',
      'database wh',
    ]);
    const rows = [...sources[0].querySelectorAll<HTMLElement>('li.row')].map((row) => [
      textOf(row.querySelector('.row-label')),
      textOf(row.querySelector('.badge')),
      [...row.querySelectorAll('.columns li')].map((column) =>
        textOf(column).replace(/\s*close$/, ''),
      ),
      [...row.querySelectorAll('.display')].map((shown) => textOf(shown)),
    ]);
    expect(rows).toEqual([
      [
        'Row 1001',
        'Changed',
        [
          'status: NULL→ becomes paid',
          'placed_at: 2026-03-01 09:00:00→ becomes 2026-03-01 10:30:00',
        ],
        [],
      ],
      ['New row 1', 'New', ['customer_id: 43'], ['customer: Beta']],
      ['Row 1004', 'To be deleted', [], []],
    ]);
    expect(textOf(sources[1].querySelector('.row-label'))).toBe('Row 7, A');
    const link = sources[0].querySelector<HTMLAnchorElement>('h4 a');
    expect(link?.getAttribute('href')).toBe('/browse/shop.orders');
  });

  it("reverts a column's change, and a row's", async () => {
    const { element, shown } = await opened(changes);
    element
      .querySelector<HTMLElement>('button[aria-label="Revert status of shop.orders Row 1001"]')!
      .click();
    await shown();
    const column = await requestTo(http, opsUrl, 'POST');
    expect(column.request.body).toEqual({
      ops: [{ op: 'revert', entity: 'shop.orders', key: ['1001'], columns: ['status'] }],
    });
    column.flush(setOf(changes.changes.slice(1), 2));
    await shown();

    const drop = element.querySelector<HTMLElement>(
      'button[aria-label="Drop the new row: shop.orders New row 1"]',
    )!;
    drop.click();
    await shown();
    const row = await requestTo(http, opsUrl, 'POST');
    expect(row.request.body).toEqual({
      ops: [{ op: 'revert', entity: 'shop.orders', tempId: 't1' }],
    });
    row.flush(setOf(changes.changes.slice(2), 3));
    await shown();
    expect(element.querySelector('button[aria-label^="Restore the row"]')).not.toBeNull();
  });

  it("clears an entity's, a connection's or all the changes, once the user says so", async () => {
    const { element, shown } = await opened(changes);
    element
      .querySelector<HTMLElement>('button[aria-label="Clear the changes to wh.stock"]')!
      .click();
    await shown();
    const dialog = document.querySelector<HTMLElement>('mat-dialog-container')!;
    expect(textOf(dialog)).toContain(
      "This reverts the changes to wh.stock: they won't be committed.",
    );
    clickButton(dialog, 'Cancel');
    await shown();
    await settle();

    element.querySelector<HTMLElement>('button[aria-label="Clear the changes to shop"]')!.click();
    await shown();
    clickButton(document.querySelector<HTMLElement>('mat-dialog-container')!, 'Clear');
    await shown();
    (await requestTo(http, `${changesUrl}?source=shop`, 'DELETE')).flush(
      setOf(changes.changes.slice(3), 2),
    );
    await shown();
    expect(element.querySelectorAll('section.source').length).toBe(1);

    clickButton(element, 'delete_sweep Clear all');
    await shown();
    clickButton(document.querySelector<HTMLElement>('mat-dialog-container')!, 'Clear');
    await shown();
    (await requestTo(http, changesUrl, 'DELETE')).flush(setOf([], 3));
    await shown();
    expect(textOf(element.querySelector('.summary'))).toBe(
      'No changes: rows changed in browsing wait here until they are committed.',
    );
    expect(element.querySelector('.actions')).toBeNull();
  });

  it("says why a revert couldn't be made", async () => {
    const { element, shown } = await opened(changes);
    element
      .querySelector<HTMLElement>('button[aria-label="Revert status of shop.orders Row 1001"]')!
      .click();
    await shown();
    (await requestTo(http, opsUrl, 'POST')).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await shown();
    expect(alertsOf(element)).toContain("Couldn't revert: Oops.");
    await answerChanges(http, changes);
  });

  it("says when the changes couldn't be read, and reads them again", async () => {
    const { element, shown } = await opened(null);
    expect(textOf(element.querySelector('.summary'))).toBe('Reading the changes…');
    (await requestTo(http, changesUrl)).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await shown();
    expect(alertsOf(element)).toContain("Couldn't read the changes: Oops.");
    clickButton(element, 'Try again');
    await answerChanges(http, setOf([changeOf()]));
    await shown();
    expect(alertsOf(element)).toBe('');
    expect(textOf(element.querySelector('.summary'))).toBe('1 row changed, not committed.');
  });

  it('names a connection not known yet as being saved, and shows nothing for references to no row', async () => {
    const { element } = await opened(
      setOf([
        changeOf({
          source: '',
          values: { customer_id: null },
          original: { customer_id: '42' },
          display: { customer: null },
        }),
      ]),
    );
    expect(textOf(element.querySelector('h3'))).toBe('database Saving…');
    expect(
      element
        .querySelector('button[aria-label^="Clear the changes to "]')
        ?.getAttribute('aria-label'),
    ).toBe('Clear the changes to shop.orders');
    expect(element.querySelector('.display')).toBeNull();
  });

  it('gives the keyboard to its heading when the button it was on goes', async () => {
    const { element, shown } = await opened(changes);
    const drop = element.querySelector<HTMLElement>(
      'button[aria-label="Drop the new row: shop.orders New row 1"]',
    )!;
    drop.focus();
    drop.click();
    await shown();
    await settle();
    expect(document.activeElement).toBe(element.querySelector('h2'));
    (await requestTo(http, opsUrl, 'POST')).flush(setOf([], 2));
    await shown();
  });

  it('gives the keyboard to its heading when opened', async () => {
    const { fixture, element, shown } = await opened();
    fixture.componentRef.setInput('opened', true);
    await shown();
    await settle();
    expect(document.activeElement).toBe(element.querySelector('h2'));
  });

  it('says it is to close', async () => {
    const { fixture, element } = await opened();
    let closed = 0;
    fixture.componentInstance.closed.subscribe(() => closed++);
    element.querySelector<HTMLElement>('button[aria-label=Close]')!.click();
    expect(closed).toBe(1);
  });
});
