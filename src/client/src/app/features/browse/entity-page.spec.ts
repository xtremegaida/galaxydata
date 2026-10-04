import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { BehaviorSubject } from 'rxjs';
import {
  answerChildren,
  entityOf,
  searchUrl,
  sourceNode,
  type EntityDto,
} from '../../../testing/catalog';
import { problemBody } from '../../../testing/auth';
import { requestTo, settle } from '../../../testing/http';
import { alertsOf, clickButton, openPage, pageProviders, textOf } from '../../../testing/pages';
import { POLL_INTERVAL } from '../../core/api/poll';
import { CatalogVersion, catalogVersionHeader } from '../../core/catalog/catalog-version';
import { Router } from '@angular/router';
import { keepFocus } from '../../core/browser/keep-focus';
import { browseRoutes } from './browse.routes';
import { SEARCH_WAIT } from './catalog-panel';
import { describeChanges, describeEntity, throughOf } from './entity-page';

describe('EntityPage', () => {
  const wide = new BehaviorSubject<BreakpointState>({ matches: true, breakpoints: {} });

  beforeEach(() => {
    wide.next({ matches: true, breakpoints: {} });
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'browse', children: browseRoutes }]),
        { provide: BreakpointObserver, useValue: { observe: () => wide } },
        { provide: POLL_INTERVAL, useValue: 1 },
        { provide: SEARCH_WAIT, useValue: 0 },
      ],
    });
  });

  const entityUrl = (name: string) => `/api/catalog/entity?name=${name}`;

  /** An entity's page opened, the tree beside it answered (the entity not found in it). */
  async function open(name = 'shop.orders', entity: EntityDto | null = entityOf(), version = 'v1') {
    const opened = await openPage(`/browse/${name}`);
    await answerChildren(opened.http, null, [sourceNode('shop')]);
    (await requestTo(opened.http, searchUrl(name, 20))).flush({
      text: name,
      hits: [],
      more: false,
    });
    const page = opened.harness.fixture.nativeElement.querySelector(
      'gd-entity-page',
    ) as HTMLElement;
    const shown = async () => {
      await settle();
      opened.harness.detectChanges();
    };
    if (entity) {
      (await requestTo(opened.http, entityUrl(name))).flush(entity, {
        headers: { [catalogVersionHeader]: version },
      });
      await shown();
    }
    const facts = () =>
      [...page.querySelectorAll('.facts dt')].map((term) => [
        textOf(term),
        textOf(term.nextElementSibling),
      ]);
    const rowsOf = (table: Element | undefined) =>
      [...(table?.querySelectorAll('tbody tr') ?? [])].map((row) =>
        [...row.children].map((cell) => textOf(cell)),
      );
    return { ...opened, page, shown, facts, rowsOf };
  }

  it('says what the entity is, its columns, and the navigations from its rows', async () => {
    const { page, facts, rowsOf } = await open();
    expect(textOf(page.querySelector('h1'))).toBe('shop.orders');
    expect(textOf(page.querySelector('.subtitle'))).toBe('A table of shop');
    expect(facts()).toEqual([
      ['Full name', 'shop.main.orders'],
      ['Rows', 'About 1,200 rows, as the database estimates'],
      ['Key', 'id'],
      ['Shown by', 'statuswhere other rows refer to its rows'],
      ['Changes', 'You may add, change and delete its rows.'],
    ]);
    const [columns, navigations] = [...page.querySelectorAll('table')];
    expect([...columns.querySelectorAll('thead th')].map((th) => textOf(th))).toEqual([
      'Name',
      'Type',
      'Properties',
      'Changes',
    ]);
    expect(rowsOf(columns)).toEqual([
      [
        'keyKey:id',
        'int64INTEGER',
        'identity',
        "Read-only'id' is part of the key, which can't change: delete the row and insert it again",
      ],
      ['customer_idCustomerWho ordered', 'int64INTEGER', '', 'Can be changedNeeded in a new row'],
      ['status', 'string?TEXT', 'default', 'Can be changed'],
    ]);
    expect(rowsOf(navigations)).toEqual([
      ['customer', 'shop.customers', 'one', 'customer_id → id', ''],
      [
        'order_lines',
        'shop.order_lines',
        'many',
        'id → order_id',
        'from the other sidenot enforced',
      ],
    ]);
    expect(navigations.querySelector('a')?.getAttribute('href')).toBe('/browse/shop.customers');
    expect(textOf(page.querySelector('#gd-entity-columns'))).toBe('Columns (3)');
  });

  it("says once why an entity's rows can't be changed, not for each column", async () => {
    const reason = "shop.orders can't be changed: shop is read-only";
    const { page, facts } = await open(
      'shop.orders',
      entityOf({
        capabilities: {
          canInsert: false,
          canUpdate: false,
          canDelete: false,
          insertReason: reason,
          changeReason: reason,
        },
        hasTriggers: true,
        uniqueKeys: [{ name: 'ux', columns: ['customer_id', 'status'], isDeclared: false }],
        rowCountEstimate: null,
      }),
    );
    expect(facts()).toEqual([
      ['Full name', 'shop.main.orders'],
      ['Key', 'id'],
      ['Unique', 'customer_id, status'],
      ['Shown by', 'statuswhere other rows refer to its rows'],
      ['Triggers', 'Changes to its rows set off triggers'],
      ['Changes', `${reason}.`],
    ]);
    expect([...page.querySelectorAll('table')[0].querySelectorAll('thead th')].length).toBe(3);
  });

  it("shows a virtual entity's query, and what is wrong with it", async () => {
    const { page, facts } = await open(
      'reports.big_spenders',
      entityOf({
        name: 'reports.big_spenders',
        qualifiedName: 'reports.big_spenders',
        kind: 'virtual',
        source: null,
        schema: null,
        table: null,
        rowCountEstimate: null,
        key: null,
        displayColumn: null,
        navigations: [],
        query: 'shop.customers.where(credit_limit > 1000)',
        problem: 'GDQ2001: there is no credit_limit',
        capabilities: {
          canInsert: false,
          canUpdate: false,
          canDelete: false,
          changeReason: 'reports.big_spenders is defined by a query: only tables can be changed',
        },
      }),
    );
    expect(textOf(page.querySelector('.subtitle'))).toBe(
      'A virtual entity: the rows of a query, as the catalog defines it',
    );
    expect(facts()[0]).toEqual(['Key', 'None']);
    expect(textOf(page.querySelector('gd-message'))).toBe(
      "errorThis virtual entity doesn't work: GDQ2001: there is no credit_limit",
    );
    expect(textOf(page.querySelector('pre'))).toBe('shop.customers.where(credit_limit > 1000)');
    expect(textOf(page.querySelector('#gd-entity-navigations + .empty'))).toBe(
      "Its rows aren't linked to other rows.",
    );
  });

  it('says when there is no such entity', async () => {
    const { http, page, shown } = await open('shop.gone', null);
    (await requestTo(http, entityUrl('shop.gone'))).flush(
      problemBody('not-found', 'There is no such entity'),
      { status: 404, statusText: 'Not Found' },
    );
    await shown();
    expect(alertsOf(page)).toBe(
      'error There is no shop.gone in the catalog (any more). Find it in the catalog, or choose another.',
    );
  });

  it("says why it couldn't read the entity, and reads it again", async () => {
    const { http, page, shown } = await open('shop.orders', null);
    (await requestTo(http, entityUrl('shop.orders'))).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await shown();
    expect(alertsOf(page)).toBe("error Couldn't read shop.orders: Oops. Try again");
    clickButton(page, 'Try again');
    (await requestTo(http, entityUrl('shop.orders'))).flush(entityOf());
    await shown();
    expect(alertsOf(page)).toBe('');
    expect(textOf(page.querySelector('h1'))).toBe('shop.orders');
  });

  it('reads the entity again when the catalog changes, showing it meanwhile', async () => {
    const { http, page, shown } = await open();
    TestBed.inject(CatalogVersion).seen('v2');
    await shown();
    const again = await requestTo(http, entityUrl('shop.orders'));
    expect(textOf(page.querySelector('h1'))).toBe('shop.orders');
    again.flush(entityOf({ comment: 'Orders, as placed' }), {
      headers: { [catalogVersionHeader]: 'v2' },
    });
    await shown();
    expect(textOf(page.querySelector('.comment'))).toBe('Orders, as placed');
    expect(http.match(entityUrl('shop.orders'))).toEqual([]);
  });

  it('keeps the entity shown when reading it again fails, and says why', async () => {
    const { http, page, shown } = await open();
    TestBed.inject(CatalogVersion).seen('v2');
    await shown();
    (await requestTo(http, entityUrl('shop.orders'))).flush(null, {
      status: 0,
      statusText: 'Unknown Error',
    });
    await shown();
    expect(textOf(page.querySelector('h1'))).toBe('shop.orders');
    expect(alertsOf(page)).toMatch(/^error Couldn't read shop\.orders: .* Try again$/);
  });

  it("doesn't read the entity again for the answer that says the catalog changed", async () => {
    const { http } = await open('shop.orders', entityOf(), 'v2');
    await settle();
    expect(http.match(entityUrl('shop.orders'))).toEqual([]);
  });

  it("doesn't show the entity before while another is read, nor take focus from the tree", async () => {
    const { http, page, shown, harness } = await open();
    const tree = harness.fixture.nativeElement.querySelector('[role=tree]') as HTMLElement;
    tree.focus();
    const router = TestBed.inject(Router);
    void router.navigate(['/browse', 'shop.customers'], { state: keepFocus });
    await shown();
    expect(page.querySelector('h1')).toBeNull();
    expect(page.querySelector('mat-progress-bar')).not.toBeNull();
    (await requestTo(http, searchUrl('shop.customers', 20))).flush({
      text: 'shop.customers',
      hits: [],
      more: false,
    });
    (await requestTo(http, entityUrl('shop.customers'))).flush(
      entityOf({ name: 'shop.customers', qualifiedName: 'shop.main.customers' }),
    );
    await shown();
    await settle();
    expect(textOf(page.querySelector('h1'))).toBe('shop.customers');
    expect(document.activeElement).toBe(tree);
  });

  it('moves focus to the heading of the entity whose link was followed', async () => {
    const { http, page, shown } = await open();
    const link = page.querySelector<HTMLAnchorElement>('a[href="/browse/shop.customers"]')!;
    link.focus();
    link.click();
    await shown();
    (await requestTo(http, searchUrl('shop.customers', 20))).flush({
      text: 'shop.customers',
      hits: [],
      more: false,
    });
    (await requestTo(http, entityUrl('shop.customers'))).flush(
      entityOf({ name: 'shop.customers', qualifiedName: 'shop.main.customers', navigations: [] }),
    );
    await shown();
    await settle();
    expect(document.activeElement).toBe(page.querySelector('h1'));
    expect(textOf(document.activeElement)).toBe('shop.customers');
  });
});

describe('the words of an entity page', () => {
  it('says what an entity is', () => {
    expect(describeEntity(entityOf({ kind: 'view' }))).toBe('A view of shop');
    expect(describeEntity(entityOf({ source: null }))).toBe('A table');
  });

  it('says what may be done with its rows', () => {
    expect(
      describeChanges({
        canInsert: false,
        canUpdate: true,
        canDelete: true,
        insertReason: 'blob is binary',
      }),
    ).toEqual(['You may change and delete its rows.', 'No rows can be added: blob is binary.']);
    expect(
      describeChanges({
        canInsert: true,
        canUpdate: false,
        canDelete: false,
        changeReason: 'No key.',
      }),
    ).toEqual(['You may add its rows.', 'No key.']);
  });

  it('pairs the columns a navigation goes through', () => {
    expect(
      throughOf({ ...entityOf().navigations[0], columns: ['a', 'b'], targetColumns: ['x', 'y'] }),
    ).toBe('a → x, b → y');
  });
});
