import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MatFormFieldHarness } from '@angular/material/form-field/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { MatSelectHarness } from '@angular/material/select/testing';
import { Router } from '@angular/router';
import { problemBody } from '../../../../testing/auth';
import { requestTo, settle } from '../../../../testing/http';
import { clickButton, openPage, pageProviders } from '../../../../testing/pages';
import { adminRoutes } from '../admin.routes';
import { userOf } from './user-list.spec';

describe('NewUser', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: pageProviders([{ path: 'admin', children: adminRoutes }]),
    });
  });

  async function open() {
    const opened = await openPage('/admin/users/new');
    (await requestTo(opened.http, '/api/auth/password-policy')).flush({
      minimumLength: 12,
      maximumLength: 256,
    });
    await settle();
    opened.harness.detectChanges();
    const errors = async () =>
      Promise.all(
        (await opened.loader.getAllHarnesses(MatFormFieldHarness)).map((field) =>
          field.getTextErrors(),
        ),
      );
    const make = async () =>
      (await opened.loader.getHarness(MatButtonHarness.with({ text: 'Make the user' }))).click();
    return { ...opened, errors, make };
  }

  it('checks what it can before making the user', async () => {
    const { loader, errors, make, page } = await open();
    await make();
    expect(await errors()).toEqual([
      ['Give a user name'],
      [],
      [],
      ['Give a password, or make one up'],
    ]);
    expect(document.activeElement).toBe(page.querySelector('input[autocomplete=off]'));
    const [userName, , password] = await loader.getAllHarnesses(MatInputHarness);
    await userName.setValue('-carol');
    expect((await errors())[0][0]).toContain('starts with a letter or digit');
    await userName.setValue('carol');
    await password.setValue('Carol was here');
    expect((await errors())[3]).toEqual(['A password may not hold the user name']);
  });

  it('makes the user, with a password made up, and opens them', async () => {
    const { loader, make, http } = await open();
    const [userName, displayName] = await loader.getAllHarnesses(MatInputHarness);
    await userName.setValue('carol');
    await displayName.setValue(' Carol Danvers ');
    await (await loader.getHarness(MatSelectHarness)).clickOptions({ text: 'Data manager' });
    await (
      await loader.getHarness(
        MatButtonHarness.with({ selector: '[aria-label="Make up a password"]' }),
      )
    ).click();
    const [, , password] = await loader.getAllHarnesses(MatInputHarness);
    const made = (await password.getValue()) as string;
    expect(made).toHaveLength(20);
    await make();
    const request = await requestTo(http, '/api/users', 'POST');
    expect(request.request.body).toEqual({
      userName: 'carol',
      displayName: 'Carol Danvers',
      role: 'dataManager',
      password: made,
    });
    request.flush(userOf({ id: 7 }), { status: 201, statusText: 'Created' });
    await settle();
    expect(TestBed.inject(Router).url).toBe('/admin/users/7');
    (await requestTo(http, '/api/users/7')).flush(userOf({ id: 7 }));
  });

  it("doesn't ask to leave once the user is made, even after having asked before", async () => {
    const { loader, make, http } = await open();
    const [userName, , password] = await loader.getAllHarnesses(MatInputHarness);
    await userName.setValue('carol');
    await password.setValue('a password long enough');
    const router = TestBed.inject(Router);
    const leaving = router.navigateByUrl('/admin/users');
    await settle();
    clickButton(document.querySelector('mat-dialog-container')!, 'Stay');
    expect(await leaving).toBe(false);
    await make();
    (await requestTo(http, '/api/users', 'POST')).flush(userOf({ id: 7 }), {
      status: 201,
      statusText: 'Created',
    });
    await settle();
    expect(document.querySelector('mat-dialog-container')).toBeNull();
    expect(router.url).toBe('/admin/users/7');
    (await requestTo(http, '/api/users/7')).flush(userOf({ id: 7 }));
  });

  it('puts the server’s refusals on their fields', async () => {
    const { loader, make, http, errors } = await open();
    const [userName, , password] = await loader.getAllHarnesses(MatInputHarness);
    await userName.setValue('carol');
    await password.setValue('a password long enough');
    await make();
    (await requestTo(http, '/api/users', 'POST')).flush(
      problemBody('user-name-taken', 'The user name is taken', {
        detail: 'There is a user carol already (user names ignore case)',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await settle();
    expect((await errors())[0]).toEqual(['There is a user carol already (user names ignore case)']);

    await userName.setValue('carol2');
    await make();
    (await requestTo(http, '/api/users', 'POST')).flush(
      problemBody('weak-password', "The password won't do", {
        detail: 'A password needs at least 16 characters',
      }),
      { status: 422, statusText: 'Unprocessable' },
    );
    await settle();
    expect((await errors())[3]).toEqual(['A password needs at least 16 characters']);
  });
});
