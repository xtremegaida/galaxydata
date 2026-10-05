import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MatDialogHarness } from '@angular/material/dialog/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { MatSelectHarness } from '@angular/material/select/testing';
import { MatSlideToggleHarness } from '@angular/material/slide-toggle/testing';
import { MatSnackBarHarness } from '@angular/material/snack-bar/testing';
import { Router } from '@angular/router';
import { problemBody, sessionOf } from '../../../../testing/auth';
import { requestTo, settle } from '../../../../testing/http';
import { alertsOf, clickButton, openPage, pageProviders, textOf } from '../../../../testing/pages';
import { adminRoutes } from '../admin.routes';
import { userOf } from '../../../../testing/users';

describe('UserPage', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: pageProviders([{ path: 'admin', children: adminRoutes }]),
    });
  });

  async function open(user = userOf(), session = sessionOf()) {
    const opened = await openPage(`/admin/users/${user.id}`, session);
    (await requestTo(opened.http, `/api/users/${user.id}`)).flush(user);
    await settle();
    opened.harness.detectChanges();
    const button = (text: string) => opened.loader.getHarness(MatButtonHarness.with({ text }));
    return { ...opened, button };
  }

  it('shows the user, and saves what is changed, to the version read', async () => {
    const { loader, root, page, http, button } = await open();
    expect(textOf(page.querySelector('h1'))).toBe('Carol Danvers');
    expect(await (await button('Save')).isDisabled()).toBe(true);

    await (await loader.getHarness(MatInputHarness)).setValue('Captain Marvel');
    await (await loader.getHarness(MatSelectHarness)).clickOptions({ text: 'Reader' });
    await (await loader.getHarness(MatSlideToggleHarness)).check();
    await (await button('Save')).click();
    const request = await requestTo(http, '/api/users/2', 'PUT');
    expect(request.request.body).toEqual({
      displayName: 'Captain Marvel',
      role: 'read',
      isDisabled: true,
      version: 4,
    });
    request.flush(
      userOf({ displayName: 'Captain Marvel', role: 'read', isDisabled: true, version: 5 }),
    );
    await settle();
    expect(textOf(page.querySelector('h1'))).toBe('Captain Marvel');
    expect(await (await button('Save')).isDisabled()).toBe(true);
    expect(await (await root.getHarness(MatSnackBarHarness)).getMessage()).toBe('Saved.');
  });

  it("won't let administrators demote, disable, reset or delete themselves", async () => {
    const { loader, page } = await open(
      userOf({ id: 1, userName: 'ada', displayName: 'Ada Lovelace', role: 'admin' }),
    );
    expect(await (await loader.getHarness(MatSelectHarness)).isDisabled()).toBe(true);
    expect(await (await loader.getHarness(MatSlideToggleHarness)).isDisabled()).toBe(true);
    expect(textOf(page.querySelector('mat-hint'))).toBe(
      'How the application shows them; their user name when empty',
    );
    expect(page.textContent).toContain("You can't change your own role");
    expect(
      await loader.getHarnessOrNull(MatButtonHarness.with({ text: /Reset the password/ })),
    ).toBeNull();
    expect(await loader.getHarnessOrNull(MatButtonHarness.with({ text: /Delete/ }))).toBeNull();
  });

  it('says when someone else changed the user, and reads it again', async () => {
    const { loader, page, http, button } = await open();
    await (await loader.getHarness(MatInputHarness)).setValue('Captain Marvel');
    await (await button('Save')).click();
    (await requestTo(http, '/api/users/2', 'PUT')).flush(
      problemBody('concurrency-conflict', 'carol was changed since it was read', {
        detail: 'Read it again, and make the change again',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await settle();
    expect(alertsOf(page)).toContain(
      'carol was changed since it was read. Read it again, and make the change again.',
    );
    clickButton(page, 'Read it again');
    await settle();
    // The change made here would be lost: the page asks first.
    const dialog = document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Read it again?');
    clickButton(dialog, 'Read it again');
    (await requestTo(http, '/api/users/2')).flush(userOf({ displayName: 'Carol D.', version: 6 }));
    await settle();
    expect(await (await loader.getHarness(MatInputHarness)).getValue()).toBe('Carol D.');
    expect(alertsOf(page)).toBe('');
  });

  it('unlocks a user who is locked out, keeping the changes not saved', async () => {
    const { http, loader, page } = await open(userOf({ lockedOutUntil: '2026-10-04T13:00:00Z' }));
    expect(page.textContent).toContain('Locked out until');
    await (await loader.getHarness(MatInputHarness)).setValue('Captain Marvel');
    clickButton(page, 'lock_open Unlock');
    (await requestTo(http, '/api/users/2/unlock', 'POST')).flush(userOf({ version: 5 }));
    await settle();
    expect(page.textContent).not.toContain('Locked out until');
    expect(document.activeElement).toBe(page.querySelector('#gd-user-facts'));
    expect(await (await loader.getHarness(MatInputHarness)).getValue()).toBe('Captain Marvel');
    clickButton(page, 'Save');
    const saved = await requestTo(http, '/api/users/2', 'PUT');
    expect(saved.request.body).toMatchObject({ displayName: 'Captain Marvel', version: 5 });
    saved.flush(userOf({ displayName: 'Captain Marvel', version: 6 }));
  });

  it('deletes a user once the administrator is sure', async () => {
    const { root, http, button } = await open();
    await (await button('delete Delete')).click();
    const dialog = await root.getHarness(MatDialogHarness);
    expect(await dialog.getTitleText()).toBe('Delete Carol Danvers?');
    const container = document.querySelector('mat-dialog-container')!;
    const described = container.getAttribute('aria-describedby')!;
    expect(textOf(document.getElementById(described))).toContain('They can no longer sign in');
    await (await dialog.getHarness(MatButtonHarness.with({ text: 'Delete' }))).click();
    const request = await requestTo(http, '/api/users/2?version=4', 'DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    expect(TestBed.inject(Router).url).toBe('/admin/users');
    (await requestTo(http, '/api/users')).flush([]);
  });

  it("resets a user's password with one made up", async () => {
    const { root, http, page } = await open();
    clickButton(page, 'lock_reset Reset the password');
    (await requestTo(http, '/api/auth/password-policy')).flush({
      minimumLength: 12,
      maximumLength: 256,
    });
    await settle();
    const dialog = await root.getHarness(MatDialogHarness);
    await (
      await dialog.getHarness(
        MatButtonHarness.with({ selector: '[aria-label="Make up a password"]' }),
      )
    ).click();
    const password = (await (await dialog.getHarness(MatInputHarness)).getValue()) as string;
    expect(password).toMatch(/^[A-Za-z2-9._-]{20}$/);
    await (await dialog.getHarness(MatButtonHarness.with({ text: 'Reset the password' }))).click();
    const request = await requestTo(http, '/api/users/2/reset-password', 'POST');
    expect(request.request.body).toEqual({ password });
    request.flush(userOf({ mustChangePassword: true }));
    await settle();
    expect(await root.getHarnessOrNull(MatDialogHarness)).toBeNull();
    expect(page.textContent).toContain('Password to change');
  });

  it('says why a reset was refused, and stays open while it is asked', async () => {
    const { http, page } = await open();
    clickButton(page, 'lock_reset Reset the password');
    (await requestTo(http, '/api/auth/password-policy')).flush({
      minimumLength: 12,
      maximumLength: 256,
    });
    await settle();
    const dialog = () => document.querySelector('mat-dialog-container')!;
    const input = dialog().querySelector('input')!;
    input.value = 'a password long enough';
    input.dispatchEvent(new Event('input'));
    clickButton(dialog(), 'Reset the password');
    const request = await requestTo(http, '/api/users/2/reset-password', 'POST');
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    clickButton(dialog(), 'Cancel');
    await settle();
    expect(document.querySelector('mat-dialog-container')).not.toBeNull();
    request.flush(
      problemBody('weak-password', "The password won't do", {
        detail: 'A password needs at least 16 characters',
      }),
      { status: 422, statusText: 'Unprocessable' },
    );
    await settle();
    expect(textOf(dialog().querySelector('mat-error'))).toBe(
      'A password needs at least 16 characters',
    );
    clickButton(dialog(), 'Cancel');
    await settle();
    expect(document.querySelector('mat-dialog-container')).toBeNull();
  });

  it('asks before leaving changes unsaved', async () => {
    const { loader, http } = await open();
    await (await loader.getHarness(MatInputHarness)).setValue('Captain Marvel');
    const router = TestBed.inject(Router);
    // The navigation waits for the dialog, so the harnesses, which wait for it, can't be used.
    const leaving = router.navigateByUrl('/admin/users');
    await settle();
    const dialog = () => document.querySelector('mat-dialog-container')!;
    expect(textOf(dialog().querySelector('h2'))).toBe('Leave without saving?');
    clickButton(dialog(), 'Stay');
    expect(await leaving).toBe(false);
    expect(router.url).toBe('/admin/users/2');

    const again = router.navigateByUrl('/admin/users');
    await settle();
    clickButton(dialog(), 'Leave');
    expect(await again).toBe(true);
    (await requestTo(http, '/api/users')).flush([]);
  });
});
