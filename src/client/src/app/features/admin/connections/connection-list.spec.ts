import { TestBed } from '@angular/core/testing';
import { MatTableHarness } from '@angular/material/table/testing';
import { connectionOf, kinds } from '../../../../testing/connections';
import { requestTo, settle } from '../../../../testing/http';
import { problemBody } from '../../../../testing/auth';
import { alertsOf, openPage, pageProviders, textOf } from '../../../../testing/pages';
import { POLL_INTERVAL } from '../../../core/api/poll';
import { adminRoutes } from '../admin.routes';

describe('ConnectionList', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'admin', children: adminRoutes }]),
        { provide: POLL_INTERVAL, useValue: 1 },
      ],
    });
  });

  it('lists the connections, their kinds, whether they are written, and their schemas', async () => {
    const { http, loader, harness, page } = await openPage('/admin/connections');
    (await requestTo(http, '/api/connection-kinds')).flush(kinds);
    (await requestTo(http, '/api/connections')).flush([
      connectionOf(),
      connectionOf({
        id: 2,
        alias: 'pg',
        kind: 'postgres',
        displayName: null,
        isReadOnly: false,
        secretsUnreadable: true,
        schemaStatus: 'failed',
        schemaError: 'password authentication failed',
      }),
    ]);
    await settle();
    harness.detectChanges();
    const rows = await (await loader.getHarness(MatTableHarness)).getCellTextByIndex();
    expect(rows.map((row) => row.slice(0, 3))).toEqual([
      ['shop', 'The shop', 'SQLite'],
      ['pg', '', 'PostgreSQL'],
    ]);
    expect(rows[0][3]).toBe('Read-only');
    expect(rows[1][3]).toBe('WrittenSecrets to enter again');
    expect(rows[1][4]).toBe("errorCouldn't be read");
    expect(textOf(page.querySelector('a[href="/admin/connections/2"]'))).toBe('pg');
  });

  it('looks again while a schema is being read', async () => {
    const { http, page, harness } = await openPage('/admin/connections');
    (await requestTo(http, '/api/connection-kinds')).flush(kinds);
    (await requestTo(http, '/api/connections')).flush([connectionOf({ schemaStatus: 'loading' })]);
    await settle();
    harness.detectChanges();
    expect(textOf(page.querySelector('gd-schema-status'))).toBe('Reading the schema…');
    (await requestTo(http, '/api/connections')).flush([connectionOf({ schemaStatus: 'ready' })]);
    await settle();
    harness.detectChanges();
    expect(textOf(page.querySelector('gd-schema-status'))).toMatch(/^check_circleRead /);
    await settle(10);
    http.expectNone('/api/connections');
  });

  it('keeps the connections shown while a look fails, and looks again', async () => {
    const { http, page, harness } = await openPage('/admin/connections');
    (await requestTo(http, '/api/connection-kinds')).flush(kinds);
    (await requestTo(http, '/api/connections')).flush([connectionOf({ schemaStatus: 'loading' })]);
    (await requestTo(http, '/api/connections')).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await settle();
    harness.detectChanges();
    expect(textOf(page.querySelector('a[href="/admin/connections/1"]'))).toBe('shop');
    expect(alertsOf(page)).toContain('Oops.');
    (await requestTo(http, '/api/connections')).flush([connectionOf()]);
    await settle();
    harness.detectChanges();
    expect(textOf(page.querySelector('gd-schema-status'))).toMatch(/^check_circleRead /);
    expect(alertsOf(page)).toBe('');
  });

  it('asks for a first connection when there are none', async () => {
    const { http, page, harness } = await openPage('/admin/connections');
    (await requestTo(http, '/api/connection-kinds')).flush(kinds);
    (await requestTo(http, '/api/connections')).flush([]);
    await settle();
    harness.detectChanges();
    expect(textOf(page.querySelector('.empty'))).toContain('No connections yet');
  });
});
