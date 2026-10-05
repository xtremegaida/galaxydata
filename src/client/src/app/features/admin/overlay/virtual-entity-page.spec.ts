import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { problemBody } from '../../../../testing/auth';
import { pagesFetched } from '../../../../testing/browse';
import { entityOf } from '../../../../testing/catalog';
import { requestTo } from '../../../../testing/http';
import { FakeMonaco, fakeMonacoProviders } from '../../../../testing/monaco';
import {
  checkOf,
  columnsNamed,
  issueOf,
  overlayPage,
  validateUrl,
  virtualEntitiesUrl,
  virtualEntityOf,
} from '../../../../testing/overlay';
import { clickButton, openPage, pageProviders, textOf, wordsOf } from '../../../../testing/pages';
import { executeUrl, orderResultRows, queryPageOf } from '../../../../testing/query';
import { BROWSE_PAGE_SIZE } from '../../browse/grid/grid-settings';
import { adminRoutes } from '../admin.routes';
import { OVERLAY_WAITS } from './overlay-items';

/** The entity a query makes: reports.big_orders, with orders' columns. */
const made = entityOf({
  name: 'reports.big_orders',
  qualifiedName: 'reports.big_orders',
  kind: 'virtual',
  source: null,
  schema: null,
  table: null,
  key: null,
  columns: columnsNamed('id', 'customer_id', 'total'),
  navigations: [],
  query: 'shop.orders.where(total > 100)',
});

describe('VirtualEntityPage', () => {
  let monaco: FakeMonaco;

  beforeEach(() => {
    monaco = new FakeMonaco();
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'admin', children: adminRoutes }]),
        fakeMonacoProviders(monaco),
        { provide: OVERLAY_WAITS, useValue: { check: 1, lookup: 1 } },
        { provide: BROWSE_PAGE_SIZE, useValue: 3 },
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

  async function opened(url: string) {
    const page = overlayPage(await openPage(url));
    await page.shown();
    const editor = () => {
      const found = monaco.live.find((each) => each.options['ariaLabel'] === 'Its query');
      if (!found) {
        throw new Error("The query's editor isn't there");
      }
      return found;
    };
    const write = async (query: string) => {
      editor().model.type(query);
      await page.shown();
    };
    return { ...page, editor, write };
  }

  /** A virtual entity's page, read and tried. */
  async function openedAt(entity = virtualEntityOf()) {
    const page = await opened(`/admin/overlay/virtual-entities/${entity.id}`);
    (await requestTo(page.http, `${virtualEntitiesUrl}/${entity.id}`)).flush(entity);
    await page.shown();
    (await requestTo(page.http, validateUrl(virtualEntitiesUrl, entity.id), 'POST')).flush(
      checkOf({ entity: made }),
    );
    await page.shown();
    return page;
  }

  it('makes a virtual entity: its query checked as it is written, what is wrong marked where it is', async () => {
    const page = await opened('/admin/overlay/virtual-entities/new');
    expect(textOf(page.page.querySelector('h1'))).toBe('New virtual entity');
    await page.typeIn(page.field('Name'), 'reports.big_orders');
    expect(wordsOf(page.page.querySelector('gd-overlay-check'))).toContain(
      'Name it and write its query, and it is tried as you go.',
    );
    await page.write('shop.orders.where(totl > 100)');
    const check = await requestTo(page.http, validateUrl(virtualEntitiesUrl), 'POST');
    expect(check.request.body).toEqual({
      name: 'reports.big_orders',
      query: 'shop.orders.where(totl > 100)',
      key: null,
      description: null,
    });
    check.flush(
      checkOf({
        issues: [
          issueOf("The definition doesn't work: There is no 'totl' here", 'error', 'GDQ2025'),
        ],
        diagnostics: [
          {
            code: 'GDQ2001',
            severity: 'error',
            message: "There is no 'totl' here",
            start: 18,
            end: 22,
          },
        ],
        entity: { ...made, columns: [], problem: "There is no 'totl' here" },
      }),
    );
    await page.shown();
    expect(page.editor().model.markers).toEqual([
      {
        severity: 8,
        message: "There is no 'totl' here (GDQ2001)",
        startLineNumber: 1,
        startColumn: 19,
        endLineNumber: 1,
        endColumn: 23,
      },
    ]);
    const diagnostics = page.page.querySelector('gd-overlay-check .diagnostics')!;
    expect(wordsOf(diagnostics)).toBe("Error: There is no 'totl' here GDQ2001 Line 1, column 19");
    // An entity that doesn't work shows nothing but its problem, said above.
    expect(page.page.querySelector('gd-entity-structure')).toBeNull();
    // Gone to where it is.
    diagnostics.querySelector<HTMLButtonElement>('button')!.click();
    await page.shown();
    expect(page.editor().position).toEqual({ lineNumber: 1, column: 19 });
    expect(document.activeElement).toBe(page.editor().input);

    // Written anew: what was wrong isn't marked in another text, and the entity it makes is shown once tried.
    await page.write('shop.orders.where(total > 100)');
    expect(page.editor().model.markers).toEqual([]);
    (await requestTo(page.http, validateUrl(virtualEntitiesUrl), 'POST')).flush(
      checkOf({ entity: made }),
    );
    await page.shown();
    expect(page.page.querySelector('gd-overlay-check .diagnostics')).toBeNull();
    expect(
      [...page.page.querySelectorAll('gd-overlay-check h3')].map((heading) => textOf(heading)),
    ).toEqual(['The entity it makes']);
    expect(page.page.querySelector('gd-entity-structure')).not.toBeNull();

    // Its key, from the columns its query makes.
    await page.press('Add a key column');
    await page.typeIn(page.field('Key column 1'), 'i');
    expect(page.options()).toEqual(['id', 'customer_id']);
    await page.choose('id');
    const keyed = await requestTo(page.http, validateUrl(virtualEntitiesUrl), 'POST');
    expect(keyed.request.body).toMatchObject({ key: ['id'] });
    keyed.flush(checkOf({ entity: made }));
    await page.shown();

    await page.press('Save');
    const saved = await requestTo(page.http, virtualEntitiesUrl, 'POST');
    expect(saved.request.body).toEqual({
      name: 'reports.big_orders',
      query: 'shop.orders.where(total > 100)',
      key: ['id'],
      description: null,
    });
    saved.flush(virtualEntityOf({ key: ['id'] }));
    await page.shown();
    expect(TestBed.inject(Router).url).toBe('/admin/overlay/virtual-entities/5');
    (await requestTo(page.http, `${virtualEntitiesUrl}/5`)).flush(virtualEntityOf({ key: ['id'] }));
    await page.shown();
    (await requestTo(page.http, validateUrl(virtualEntitiesUrl, 5), 'POST')).flush(checkOf());
    await page.shown();
    expect(page.editor().model.getValue()).toBe('shop.orders.where(total > 100)');
  });

  it('shows its rows on asking, Ctrl+Enter too, and says when the query has changed since', async () => {
    const page = await openedAt();
    await page.press('Show its rows');
    const run = await requestTo(page.http, executeUrl, 'POST');
    expect(run.request.body).toMatchObject({
      text: 'shop.orders.where(total > 100)',
      parameters: [],
    });
    run.flush(queryPageOf(orderResultRows(2), { total: 2, hasMore: false }));
    await page.shown();
    await pagesFetched();
    await page.shown();
    expect(textOf(page.page.querySelector('#gd-virtual-entity-rows'))).toBe('Its rows');
    expect(page.page.querySelectorAll('gd-query-results .ag-row')).toHaveLength(2);

    await page.write('shop.orders.where(total > 200)');
    (await requestTo(page.http, validateUrl(virtualEntitiesUrl, 5), 'POST')).flush(
      checkOf({ entity: made }),
    );
    await page.shown();
    expect(wordsOf(page.page.querySelector('.shown-rows .aside'))).toBe(
      'The query has changed since they were shown. Show them again',
    );
    await page.press('Show them again');
    expect(document.activeElement).toBe(page.page.querySelector('#gd-virtual-entity-rows'));
    const again = await requestTo(page.http, executeUrl, 'POST');
    expect(again.request.body).toMatchObject({ text: 'shop.orders.where(total > 200)' });
    again.flush(queryPageOf(orderResultRows(1), { total: 1, hasMore: false }));
    await page.shown();
    await pagesFetched();
    await page.shown();
    expect(page.page.querySelector('.shown-rows .aside')).toBeNull();
  });

  it('asks for its query, the keyboard on the editor', async () => {
    const page = await opened('/admin/overlay/virtual-entities/new');
    await page.typeIn(page.field('Name'), 'reports.big_orders');
    await page.press('Save');
    expect(textOf(page.page.querySelector('#gd-virtual-entity-query'))).toBe(
      'Write the query whose rows it has',
    );
    expect(document.activeElement).toBe(page.editor().input);
    expect(page.http.match(() => true)).toEqual([]);
  });

  it('says on its name when a virtual entity has it already', async () => {
    const page = await openedAt();
    await page.typeIn(page.field('Name'), 'reports.spend');
    (await requestTo(page.http, validateUrl(virtualEntitiesUrl, 5), 'POST')).flush(checkOf());
    await page.press('Save');
    const saved = await requestTo(page.http, `${virtualEntitiesUrl}/5`, 'PUT');
    expect(saved.request.body).toEqual({
      virtualEntity: {
        name: 'reports.spend',
        query: 'shop.orders.where(total > 100)',
        key: null,
        description: null,
      },
      version: 1,
    });
    saved.flush(
      problemBody('overlay-item-exists', 'The virtual entity is there already', {
        detail: 'There is a virtual entity reports.spend already',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await page.shown();
    expect(textOf(page.errorOf(page.field('Name')))).toBe(
      'There is a virtual entity reports.spend already',
    );
    expect(document.activeElement).toBe(page.field('Name'));
  });

  it('puts the keyboard on its name before its query when both are wanted', async () => {
    const page = await opened('/admin/overlay/virtual-entities/new');
    await page.press('Save');
    expect(textOf(page.errorOf(page.field('Name')))).toBe(
      'Name it, with a namespace: reports.big_orders',
    );
    expect(textOf(page.page.querySelector('#gd-virtual-entity-query'))).toBe(
      'Write the query whose rows it has',
    );
    expect(document.activeElement).toBe(page.field('Name'));
  });

  it("keeps what is wrong with its query marked as its name changes, and lets the one before's rows go", async () => {
    const page = await opened('/admin/overlay/virtual-entities/5');
    (await requestTo(page.http, `${virtualEntitiesUrl}/5`)).flush(
      virtualEntityOf({ query: 'shop.orders.where(totl > 100)' }),
    );
    await page.shown();
    (await requestTo(page.http, validateUrl(virtualEntitiesUrl, 5), 'POST')).flush(
      checkOf({
        diagnostics: [
          {
            code: 'GDQ2001',
            severity: 'error',
            message: "There is no 'totl' here",
            start: 18,
            end: 22,
          },
        ],
      }),
    );
    await page.shown();
    expect(page.editor().model.markers).toHaveLength(1);
    await page.typeIn(page.field('Name'), 'reports.big');
    // Tried again, the query as it was: what is wrong in it stays marked meanwhile.
    expect(page.editor().model.markers).toHaveLength(1);
    expect(page.page.querySelectorAll('gd-overlay-check .diagnostics li')).toHaveLength(1);
    (await requestTo(page.http, validateUrl(virtualEntitiesUrl, 5), 'POST')).flush(checkOf());
    await page.shown();
    expect(page.editor().model.markers).toEqual([]);

    // Its rows shown, then another virtual entity (a link to one it would break): they were this one's.
    await page.press('Show its rows');
    (await requestTo(page.http, executeUrl, 'POST')).flush(
      queryPageOf(orderResultRows(1), { total: 1, hasMore: false }),
    );
    await page.shown();
    await pagesFetched();
    const router = TestBed.inject(Router);
    const went = router.navigateByUrl('/admin/overlay/virtual-entities/8');
    await page.shown();
    // Its name was changed, not saved: leaving asks.
    clickButton(document.querySelector('mat-dialog-container')!, 'Leave');
    expect(await went).toBe(true);
    (await requestTo(page.http, `${virtualEntitiesUrl}/8`)).flush(
      virtualEntityOf({ id: 8, name: 'reports.spend' }),
    );
    await page.shown();
    (await requestTo(page.http, validateUrl(virtualEntitiesUrl, 8), 'POST')).flush(checkOf());
    await page.shown();
    expect(page.page.querySelector('gd-query-results')).toBeNull();
    expect(page.field('Name').value).toBe('reports.spend');
  });

  it('takes no key column of spaces, and counts one added as a change', async () => {
    const page = await openedAt();
    expect(page.isDisabled('Save')).toBe(true);
    await page.press('Add a key column');
    expect(document.activeElement).toBe(page.field('Key column 1'));
    // Nothing named yet, but there is something to undo, or save (which says what is wanted).
    expect(page.isDisabled('Undo the changes')).toBe(false);
    await page.typeIn(page.field('Key column 1'), '   ');
    await page.press('Save');
    expect(textOf(page.errorOf(page.field('Key column 1')))).toBe('Name the column, or remove it');
    expect(document.activeElement).toBe(page.field('Key column 1'));
    await page.press('Remove key column 1');
    expect(document.activeElement).toBe(
      page.page.querySelector('[data-at="add-key"]') as HTMLElement,
    );
    expect(page.isDisabled('Save')).toBe(true);
  });
});
