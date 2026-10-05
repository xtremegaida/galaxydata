import { TestBed } from '@angular/core/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { MatSortHarness } from '@angular/material/sort/testing';
import { MatTableHarness } from '@angular/material/table/testing';
import { problemBody } from '../../../../testing/auth';
import { requestTo, settle } from '../../../../testing/http';
import { alertsOf, openPage, pageProviders, textOf } from '../../../../testing/pages';
import { adminRoutes } from '../admin.routes';
import { userOf } from '../../../../testing/users';

describe('UserList', () => {
  const users = [
    userOf({ id: 1, userName: 'ada', displayName: 'Ada Lovelace', role: 'admin' }),
    userOf(),
    userOf({
      id: 3,
      userName: 'grace',
      displayName: null,
      role: 'read',
      isDisabled: true,
      mustChangePassword: true,
      lockedOutUntil: '2026-10-04T13:00:00Z',
      lastSignInAt: null,
    }),
  ];

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: pageProviders([{ path: 'admin', children: adminRoutes }]),
    });
  });

  async function open() {
    const opened = await openPage('/admin/users');
    (await requestTo(opened.http, '/api/users')).flush(users);
    await settle();
    opened.harness.detectChanges();
    return opened;
  }

  async function cells(loader: Awaited<ReturnType<typeof open>>['loader']) {
    const table = await loader.getHarness(MatTableHarness);
    return table.getCellTextByIndex();
  }

  it('lists the users, with their roles, states and last sign-ins', async () => {
    const { loader, page } = await open();
    const rows = await cells(loader);
    expect(rows.map((row) => row.slice(0, 4))).toEqual([
      ['ada', 'Ada Lovelace', 'Administrator', ''],
      ['carol', 'Carol Danvers', 'Data manager', ''],
      ['grace', '', 'Reader', 'DisabledLocked outPassword to change'],
    ]);
    expect(rows[2][4]).toBe('Never');
    // The states are a list, each read apart.
    expect(
      [...page.querySelectorAll('tr:last-child .badges li')].map((item) => item.textContent),
    ).toEqual(['Disabled', 'Locked out', 'Password to change']);
    expect(page.querySelector('a[href="/admin/users/3"]')?.textContent).toBe('grace');
  });

  it('finds users by their names', async () => {
    const { loader, page } = await open();
    await (await loader.getHarness(MatInputHarness)).setValue('LOVE');
    expect((await cells(loader)).map((row) => row[0])).toEqual(['ada']);
    await (await loader.getHarness(MatInputHarness)).setValue('nobody');
    expect(textOf(page.querySelector('.empty'))).toBe('No user\'s name has "nobody".');
  });

  it('sorts them as asked', async () => {
    const { loader } = await open();
    const sort = await loader.getHarness(MatSortHarness);
    const [, , role] = await sort.getSortHeaders();
    await role.click();
    expect((await cells(loader)).map((row) => row[0])).toEqual(['ada', 'carol', 'grace']);
    await role.click();
    expect((await cells(loader)).map((row) => row[0])).toEqual(['grace', 'carol', 'ada']);
  });

  it('says what went wrong, and tries again', async () => {
    const opened = await openPage('/admin/users');
    (await requestTo(opened.http, '/api/users')).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    await settle();
    opened.harness.detectChanges();
    expect(alertsOf(opened.page)).toContain('Oops.');
    opened.page.querySelector<HTMLButtonElement>('[role=alert] button')!.click();
    (await requestTo(opened.http, '/api/users')).flush(users);
    await settle();
    opened.harness.detectChanges();
    expect(alertsOf(opened.page)).toBe('');
    expect(await cells(opened.loader)).toHaveLength(3);
  });
});
