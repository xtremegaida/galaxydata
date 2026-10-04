import { TestBed } from '@angular/core/testing';
import { MatButtonToggleHarness } from '@angular/material/button-toggle/testing';
import { MatFormFieldHarness } from '@angular/material/form-field/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { MatSelectHarness } from '@angular/material/select/testing';
import { Router } from '@angular/router';
import { problemBody } from '../../../../testing/auth';
import { connectionOf, kinds, type ConnectionDto } from '../../../../testing/connections';
import { requestTo, settle } from '../../../../testing/http';
import { alertsOf, clickButton, openPage, pageProviders, textOf } from '../../../../testing/pages';
import { POLL_INTERVAL } from '../../../core/api/poll';
import { adminRoutes } from '../admin.routes';

const postgres = connectionOf({
  id: 2,
  alias: 'pg',
  kind: 'postgres',
  displayName: null,
  settings: { Host: 'db.example.com', Database: 'shop', Username: 'reader', 'Max Pool Size': '20' },
  secrets: { Password: { hasValue: true } },
  connectionString:
    'Host=db.example.com;Database=shop;Username=reader;Max Pool Size=20;Password=********',
});

describe('ConnectionPage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'admin', children: adminRoutes }]),
        { provide: POLL_INTERVAL, useValue: 1 },
      ],
    });
  });

  async function settled(opened: Awaited<ReturnType<typeof openPage>>) {
    await settle();
    opened.harness.detectChanges();
  }

  async function openNew() {
    const opened = await openPage('/admin/connections/new');
    (await requestTo(opened.http, '/api/connection-kinds')).flush(kinds);
    await settled(opened);
    return opened;
  }

  async function openStored(connection: ConnectionDto = postgres) {
    const opened = await openPage(`/admin/connections/${connection.id}`);
    (await requestTo(opened.http, '/api/connection-kinds')).flush(kinds);
    (await requestTo(opened.http, `/api/connections/${connection.id}`)).flush(connection);
    (await requestTo(opened.http, `/api/connections/${connection.id}/snapshots`)).flush([]);
    await settled(opened);
    return opened;
  }

  /** The errors of the page's fields, by label. */
  async function errorsOf(opened: Awaited<ReturnType<typeof openPage>>) {
    const fields = await opened.loader.getAllHarnesses(MatFormFieldHarness);
    const errors: Record<string, string[]> = {};
    for (const field of fields) {
      const found = await field.getTextErrors();
      if (found.length > 0) {
        errors[(await field.getLabel()) ?? ''] = found;
      }
    }
    return errors;
  }

  async function setInput(
    opened: Awaited<ReturnType<typeof openPage>>,
    label: string,
    value: string,
  ) {
    const field = await opened.loader.getHarness(
      MatFormFieldHarness.with({ floatingLabelText: label }),
    );
    await ((await field.getControl()) as MatInputHarness).setValue(value);
  }

  it('makes a connection of the kind chosen, trying it first', async () => {
    const opened = await openNew();
    const { http, loader, page } = opened;
    expect(textOf(page.querySelector('h1'))).toBe('New connection');
    await (await loader.getHarness(MatSelectHarness)).clickOptions({ text: 'SQLite' });
    expect(textOf(page.querySelector('.subtitle'))).toBe('SQLite');

    clickButton(page, 'bolt Try it');
    await settled(opened);
    expect(await errorsOf(opened)).toEqual({
      Alias: ['Give an alias'],
      'Database file': ['Database file is needed'],
    });

    await setInput(opened, 'Alias', 'shop');
    await setInput(opened, 'Database file', 'C:\\data\\files\\shop.db');
    clickButton(page, 'bolt Try it');
    const tried = await requestTo(http, '/api/connections/test', 'POST');
    expect(tried.request.body).toEqual({
      kind: 'sqlite',
      connection: {
        mode: 'form',
        settings: { 'Data Source': 'C:\\data\\files\\shop.db' },
        secrets: {},
        options: {},
        isReadOnly: true,
      },
      connectionId: null,
    });
    tried.flush({
      ok: true,
      message: 'Opened the SQLite 3.53.3 database: 7 tables',
      elapsedMs: 4.2,
    });
    await settled(opened);
    expect(textOf(page.querySelector('.test-result'))).toBe(
      'check_circle It works. Opened the SQLite 3.53.3 database: 7 tables (4 ms)',
    );

    clickButton(page, 'Make the connection');
    const made = await requestTo(http, '/api/connections', 'POST');
    expect(made.request.body).toMatchObject({ alias: 'shop', kind: 'sqlite', displayName: null });
    made.flush(connectionOf({ id: 5, schemaStatus: 'loading' }), {
      status: 201,
      statusText: 'Created',
    });
    await settle();
    expect(TestBed.inject(Router).url).toBe('/admin/connections/5');
    (await requestTo(http, '/api/connection-kinds')).flush(kinds);
    (await requestTo(http, '/api/connections/5')).flush(connectionOf({ id: 5 }));
    (await requestTo(http, '/api/connections/5/snapshots')).flush([]);
  });

  it('puts a taken alias on its field', async () => {
    const opened = await openNew();
    await (await opened.loader.getHarness(MatSelectHarness)).clickOptions({ text: 'SQLite' });
    await setInput(opened, 'Alias', 'shop');
    await setInput(opened, 'Database file', 'C:\\data\\files\\shop.db');
    clickButton(opened.page, 'Make the connection');
    (await requestTo(opened.http, '/api/connections', 'POST')).flush(
      problemBody('alias-taken', 'The alias is taken', {
        detail: 'There is a connection shop already',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await settled(opened);
    expect(await errorsOf(opened)).toEqual({ Alias: ['There is a connection shop already'] });
  });

  it('keeps a stored secret, and converts the settings to a connection string and back', async () => {
    const opened = await openStored();
    const { http, loader, page } = opened;
    expect(textOf(page.querySelector('h1'))).toBe('pg');
    expect(textOf(page.querySelector('gd-secret-field'))).toBe('lockPassword: storedChangeClear');

    await (
      await loader.getHarness(MatButtonToggleHarness.with({ text: 'Connection string' }))
    ).check();
    const toRaw = await requestTo(http, '/api/connection-kinds/postgres/convert', 'POST');
    expect(toRaw.request.body).toEqual({
      to: 'raw',
      connection: {
        mode: 'form',
        settings: {
          Host: 'db.example.com',
          Database: 'shop',
          Username: 'reader',
          'Max Pool Size': '20',
        },
        secrets: { Password: { action: 'keep' } },
        options: {},
        isReadOnly: true,
      },
    });
    toRaw.flush({
      mode: 'raw',
      connectionString: 'Host=db.example.com;Password=********',
      secrets: { Password: { action: 'keep' } },
      isReadOnly: true,
    });
    await settled(opened);
    expect(page.querySelector('textarea')?.value).toBe('Host=db.example.com;Password=********');

    clickButton(page, 'Save');
    const saved = await requestTo(http, '/api/connections/2', 'PUT');
    expect(saved.request.body).toEqual({
      version: 3,
      displayName: null,
      connection: {
        mode: 'raw',
        connectionString: 'Host=db.example.com;Password=********',
        secrets: { Password: { action: 'keep' } },
        options: {},
        isReadOnly: true,
      },
    });
    saved.flush({ ...postgres, mode: 'raw', version: 4 });
    await settled(opened);
    expect(page.querySelector('textarea')?.value).toBe(postgres.connectionString);

    await (
      await loader.getHarness(MatButtonToggleHarness.with({ text: 'Field by field' }))
    ).check();
    const toForm = await requestTo(http, '/api/connection-kinds/postgres/convert', 'POST');
    expect(toForm.request.body).toMatchObject({
      to: 'form',
      connection: { mode: 'raw', connectionString: postgres.connectionString, secrets: {} },
    });
    toForm.flush({
      mode: 'form',
      settings: {
        Host: 'db.example.com',
        Database: 'shop',
        Username: 'reader',
        'Max Pool Size': '20',
      },
      secrets: { Password: { action: 'keep' } },
      isReadOnly: true,
    });
    await settled(opened);
    expect(textOf(page.querySelector('gd-secret-field'))).toBe('lockPassword: storedChangeClear');
  });

  it('stays in the form when the conversion is refused, and says why', async () => {
    const opened = await openStored();
    const toggle = await opened.loader.getHarness(
      MatButtonToggleHarness.with({ text: 'Connection string' }),
    );
    await toggle.check();
    (await requestTo(opened.http, '/api/connection-kinds/postgres/convert', 'POST')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: { settings: ["Keyword not supported: 'max pool sizes'."] },
      }),
      { status: 400, statusText: 'Bad Request' },
    );
    await settled(opened);
    expect(await toggle.isChecked()).toBe(false);
    expect(alertsOf(opened.page)).toContain("Keyword not supported: 'max pool sizes'.");
  });

  it("says why a connection string can't become the form", async () => {
    const opened = await openStored({ ...postgres, mode: 'raw' });
    const toggle = await opened.loader.getHarness(
      MatButtonToggleHarness.with({ text: 'Field by field' }),
    );
    await toggle.check();
    (await requestTo(opened.http, '/api/connection-kinds/postgres/convert', 'POST')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: { connectionString: ["Format of the initialization string doesn't conform"] },
      }),
      { status: 400, statusText: 'Bad Request' },
    );
    await settled(opened);
    expect(await toggle.isChecked()).toBe(false);
    expect(alertsOf(opened.page)).toContain("Format of the initialization string doesn't conform");
  });

  it("puts the server's errors on their fields, and the others above the form", async () => {
    const opened = await openStored();
    clickButton(opened.page, 'Save');
    (await requestTo(opened.http, '/api/connections/2', 'PUT')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred.', {
        errors: {
          'settings.Host': ['Host is needed'],
          'settings.Pooling': ['Pooling is true or false'],
        },
      }),
      { status: 400, statusText: 'Bad Request' },
    );
    await settled(opened);
    expect(await errorsOf(opened)).toEqual({ Host: ['Host is needed'] });
    expect(textOf(opened.page.querySelector('[role=alert] .unplaced'))).toBe(
      'Pooling is true or false',
    );
  });

  it('reads the schema again on asking, and follows it while it is read', async () => {
    const opened = await openStored(connectionOf());
    const { http, page } = opened;
    clickButton(page, 'refresh Read it again');
    (await requestTo(http, '/api/connections/1/refresh', 'POST')).flush(
      connectionOf({ schemaStatus: 'loading' }),
      { status: 202, statusText: 'Accepted' },
    );
    await settled(opened);
    expect(textOf(page.querySelector('.section gd-schema-status'))).toBe('Reading the schema…');
    (await requestTo(http, '/api/connections/1')).flush(
      connectionOf({ schemaStatus: 'ready', schemaRefreshedAt: '2026-10-04T13:00:00Z' }),
    );
    (await requestTo(http, '/api/connections/1/snapshots')).flush([
      {
        id: 9,
        hash: 'abc',
        tableCount: 8,
        takenAt: '2026-10-04T13:00:00Z',
        checkedAt: '2026-10-04T13:00:00Z',
        changes: { added: 1, removed: 0, changed: 0 },
      },
    ]);
    await settled(opened);
    expect(textOf(page.querySelector('.section gd-schema-status'))).toMatch(/^check_circleRead /);
    expect(textOf(page.querySelector('gd-connection-snapshots'))).toContain(
      '8 tables 1 added, 0 removed, 0 changed',
    );
  });

  it("doesn't let a look at the schema begun before a save undo it", async () => {
    const opened = await openStored(connectionOf({ schemaStatus: 'loading' }));
    const { http, page } = opened;
    const look = await requestTo(http, '/api/connections/1');
    clickButton(page, 'Save');
    (await requestTo(http, '/api/connections/1', 'PUT')).flush(
      connectionOf({ schemaStatus: 'loading', version: 4 }),
    );
    await settled(opened);
    look.flush(connectionOf({ schemaStatus: 'ready', version: 3 }));
    await settled(opened);
    expect(textOf(page.querySelector('.section gd-schema-status'))).toBe('Reading the schema…');
    expect(page.textContent).not.toContain('Someone else changed it');
    (await requestTo(http, '/api/connections/1')).flush(
      connectionOf({ version: 4, schemaRefreshedAt: '2026-10-04T13:00:00Z' }),
    );
    (await requestTo(http, '/api/connections/1/snapshots')).flush([]);
    await settled(opened);
    expect(textOf(page.querySelector('.section gd-schema-status'))).toMatch(/^check_circleRead /);
  });

  it('goes on following the schema after a look fails', async () => {
    const opened = await openStored(connectionOf({ schemaStatus: 'loading' }));
    (await requestTo(opened.http, '/api/connections/1')).flush(
      problemBody('internal-error', 'Oops'),
      {
        status: 500,
        statusText: 'Server Error',
      },
    );
    (await requestTo(opened.http, '/api/connections/1')).flush(
      connectionOf({ schemaRefreshedAt: '2026-10-04T13:00:00Z' }),
    );
    (await requestTo(opened.http, '/api/connections/1/snapshots')).flush([]);
    await settled(opened);
    expect(textOf(opened.page.querySelector('.section gd-schema-status'))).toMatch(
      /^check_circleRead /,
    );
  });

  it('keeps the kind shown when changing it is cancelled', async () => {
    const opened = await openNew();
    const select = await opened.loader.getHarness(MatSelectHarness);
    await select.clickOptions({ text: 'SQLite' });
    await setInput(opened, 'Alias', 'shop');
    // The confirmation waits for an answer, so the option is chosen without the harness.
    await select.open();
    [...document.querySelectorAll<HTMLElement>('mat-option')]
      .find((option) => textOf(option) === 'PostgreSQL')!
      .click();
    await settle();
    clickButton(document.querySelector('mat-dialog-container')!, 'Cancel');
    await settled(opened);
    expect(await select.getValueText()).toBe('SQLite');
    expect(textOf(opened.page.querySelector('.subtitle'))).toBe('SQLite');
  });

  it('keeps a group open while its values are typed and cleared', async () => {
    const opened = await openNew();
    await (await opened.loader.getHarness(MatSelectHarness)).clickOptions({ text: 'SQLite' });
    const panel = opened.page.querySelector('mat-expansion-panel')!;
    expect(panel.classList).not.toContain('mat-expanded');
    (panel.querySelector('mat-expansion-panel-header') as HTMLElement).click();
    await settled(opened);
    await setInput(opened, 'Lock timeout (s)', '10');
    await setInput(opened, 'Lock timeout (s)', '');
    expect(panel.classList).toContain('mat-expanded');
  });

  it("moves focus to what takes the place of a secret's button, or of a removed setting", async () => {
    const opened = await openStored();
    const secret = () => opened.page.querySelector('gd-secret-field')!;
    clickButton(secret(), 'Change');
    await settled(opened);
    expect(document.activeElement).toBe(secret().querySelector('input'));
    clickButton(secret(), 'Keep it');
    await settled(opened);
    clickButton(secret(), 'Clear');
    await settled(opened);
    expect(textOf(document.activeElement)).toBe('Keep it');
    clickButton(opened.page, 'delete');
    await settled(opened);
    expect(document.activeElement?.classList).toContain('add-other');
  });

  it('asks for secrets that can no longer be read', async () => {
    const opened = await openStored({
      ...postgres,
      secretsUnreadable: true,
      secrets: { Password: { hasValue: false } },
    });
    expect(opened.page.textContent).toContain("Its secrets can't be read");
    expect(opened.page.querySelector('gd-secret-field input')).not.toBeNull();
  });

  it('keeps a folder of workbooks read-only, and has no connection string', async () => {
    const opened = await openStored(
      connectionOf({
        id: 3,
        alias: 'xl',
        kind: 'excel',
        settings: { Folder: 'C:\\data\\files\\xl' },
        connectionString: null,
      }),
    );
    expect(await opened.loader.getHarnessOrNull(MatButtonToggleHarness)).toBeNull();
    const readOnly = opened.page.querySelector(
      'fieldset.group mat-slide-toggle button',
    ) as HTMLButtonElement;
    expect([readOnly.disabled, readOnly.getAttribute('aria-checked')]).toEqual([true, 'true']);
    expect(opened.page.textContent).toContain('Excel folder connections are always read-only.');
  });

  it('undoes the changes, and asks before leaving them unsaved', async () => {
    const opened = await openStored();
    await setInput(opened, 'Host', 'other.example.com');
    const router = TestBed.inject(Router);
    const leaving = router.navigateByUrl('/admin/connections');
    await settle();
    clickButton(document.querySelector('mat-dialog-container')!, 'Stay');
    expect(await leaving).toBe(false);
    clickButton(opened.page, 'Undo the changes');
    await settled(opened);
    const host = await opened.loader.getHarness(
      MatFormFieldHarness.with({ floatingLabelText: 'Host' }),
    );
    expect(await ((await host.getControl()) as MatInputHarness).getValue()).toBe('db.example.com');
    const left = router.navigateByUrl('/admin/connections');
    (await requestTo(opened.http, '/api/connections')).flush([]);
    expect(await left).toBe(true);
  });

  it('deletes the connection once the administrator is sure', async () => {
    const opened = await openStored(connectionOf());
    clickButton(opened.page, 'delete Delete shop');
    await settled(opened);
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Delete shop?');
    clickButton(dialog, 'Delete');
    (await requestTo(opened.http, '/api/connections/1?version=3', 'DELETE')).flush(null, {
      status: 204,
      statusText: 'No Content',
    });
    await settle();
    expect(TestBed.inject(Router).url).toBe('/admin/connections');
  });
});
