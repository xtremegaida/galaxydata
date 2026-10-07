import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { BehaviorSubject } from 'rxjs';
import { answerChildren, catalogOf, sourceNode } from '../../../testing/catalog';
import { problemBody, sessionOf } from '../../../testing/auth';
import { requestTo, settle } from '../../../testing/http';
import { alertsOf, clickButton, openPage, pageProviders, textOf } from '../../../testing/pages';
import { POLL_INTERVAL } from '../../core/api/poll';
import type { Session } from '../../core/auth/auth-store';
import { browseRoutes } from './browse.routes';
import { SEARCH_WAIT } from './catalog-panel';

describe('CatalogOverview', () => {
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

  async function open(session: Session = sessionOf()) {
    const opened = await openPage('/browse', session);
    await answerChildren(opened.http, null, [sourceNode('shop')]);
    const page = opened.harness.fixture.nativeElement.querySelector(
      'gd-catalog-overview',
    ) as HTMLElement;
    const shown = async () => {
      await settle();
      opened.harness.detectChanges();
    };
    const rows = () =>
      [...page.querySelectorAll('.sources tbody tr')].map((row) =>
        [...row.children].map((cell) => textOf(cell)),
      );
    return { ...opened, page, shown, rows };
  }

  it('lists the connections, how their schemas stand, and what the catalog found wrong', async () => {
    const { http, page, shown, rows } = await open();
    expect(textOf(page.querySelector('h1'))).toBe('Browse');
    (await requestTo(http, '/api/catalog')).flush(
      catalogOf({
        sources: [
          ...catalogOf().sources,
          {
            alias: 'mssql',
            kind: 'sqlserver',
            displayName: null,
            status: 'failed',
            refreshedAt: '2026-10-04T12:00:00Z',
            isReadOnly: false,
            hasSchema: true,
            entities: 4,
            problem: null,
            kindName: 'SQL Server',
            kindIcon: 'database',
          },
        ],
        diagnostics: [
          {
            code: 'GDQ5003',
            severity: 'error',
            message: 'The relation from shop.orders to pg.customers names no column ref',
            subject: 'shop.orders',
            item: { kind: 'relation', id: 2 },
          },
          {
            code: 'GDQ1203',
            severity: 'warning',
            message: 'shop.sales hides the table sales',
            subject: null,
            item: null,
          },
        ],
      }),
    );
    await shown();
    expect(rows().map((row) => [row[0], row[1], row[3], row[4]])).toEqual([
      ['shopThe shop', 'SQLite', '8', 'Read-only'],
      ['pg', 'PostgreSQL', '3', 'Written'],
      ['mssql', 'SQL Server', '4', 'Written'],
    ]);
    expect(rows()[0][2]).toMatch(/^check_circleRead /);
    expect(rows()[2][2]).toBe("errorCouldn't be readThe schema read before is shown.");
    expect([...page.querySelectorAll('.diagnostics li')].map((item) => textOf(item))).toEqual([
      'errorerror: The relation from shop.orders to pg.customers names no column ref GDQ5003',
      'warningwarning: shop.sales hides the table sales GDQ1203',
    ]);
  });

  it('follows the schemas being read', async () => {
    const { http, shown, rows } = await open();
    const reading = catalogOf();
    (await requestTo(http, '/api/catalog')).flush({
      ...reading,
      sources: [{ ...reading.sources[0], status: 'loading' }],
    });
    await shown();
    expect(rows()[0][2]).toBe('Reading the schema…');
    (await requestTo(http, '/api/catalog')).flush({
      ...reading,
      sources: [reading.sources[0]],
    });
    await shown();
    expect(rows()[0][2]).toMatch(/^check_circleRead /);
  });

  it('asks administrators for a first connection, and not others', async () => {
    const admin = await open();
    (await requestTo(admin.http, '/api/catalog')).flush(catalogOf({ sources: [] }));
    await admin.shown();
    expect(textOf(admin.page.querySelector('.empty'))).toBe(
      'There are no connections yet. Make one to browse a database.',
    );
  });

  it("doesn't offer readers to make a connection", async () => {
    const reader = await open(sessionOf('read'));
    (await requestTo(reader.http, '/api/catalog')).flush(catalogOf({ sources: [] }));
    await reader.shown();
    expect(textOf(reader.page.querySelector('.empty'))).toBe('There are no connections yet.');
  });

  it("says why it couldn't read the catalog, and reads it again", async () => {
    const { http, page, shown, rows } = await open();
    (await requestTo(http, '/api/catalog')).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await shown();
    expect(alertsOf(page)).toBe('error Oops. Try again');
    clickButton(page, 'Try again');
    (await requestTo(http, '/api/catalog')).flush(catalogOf());
    await shown();
    expect(alertsOf(page)).toBe('');
    expect(rows().length).toBe(2);
  });
});
