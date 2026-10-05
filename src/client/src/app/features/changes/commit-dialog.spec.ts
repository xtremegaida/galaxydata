import { LiveAnnouncer } from '@angular/cdk/a11y';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { fakeBrowserProviders, problemBody, sessionOf } from '../../../testing/auth';
import {
  FakeChangesChannel,
  answerChanges,
  changeOf,
  commitUrl,
  insertOf,
  previewOf,
  previewUrl,
  resultOf,
  scriptOf,
  setOf,
} from '../../../testing/changes';
import { requestTo, settle } from '../../../testing/http';
import { FakeMonaco, fakeMonacoProviders } from '../../../testing/monaco';
import { alertsOf, clickButton, textOf } from '../../../testing/pages';
import type { UserRole } from '../../core/auth/roles';
import { AuthStore } from '../../core/auth/auth-store';
import {
  type ChangePreview,
  ChangesChannel,
  PendingChanges,
} from '../../core/changes/pending-changes';
import { CommitDialog } from './commit-dialog';

describe('CommitDialog', () => {
  let http: HttpTestingController;
  let monaco: FakeMonaco;

  beforeEach(() => {
    monaco = new FakeMonaco();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        fakeBrowserProviders(),
        fakeMonacoProviders(monaco),
        { provide: ChangesChannel, useClass: FakeChangesChannel },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    TestBed.inject(MatDialog).closeAll();
    http.verify();
  });

  const shown = async () => {
    for (let turn = 0; turn < 3; turn++) {
      await settle();
      TestBed.tick();
    }
  };

  const changes = setOf([
    changeOf(),
    insertOf('t1', { id: 2, entity: 'shop.customers', values: { name: 'Zeta' } }),
    changeOf({ id: 3, source: 'wh', entity: 'wh.stock', key: ['P-100'], rowId: '["P-100"]' }),
  ]);

  /** The dialog of a user (an administrator unless said otherwise), the changes read, previewed as given. */
  async function opened(preview: ChangePreview | null = previewOf(), role: UserRole = 'admin') {
    const auth = TestBed.inject(AuthStore);
    const signedIn = auth.ensure();
    http.expectOne('/api/auth/session').flush(sessionOf(role));
    await signedIn;
    TestBed.inject(PendingChanges);
    TestBed.tick();
    await answerChanges(http, changes);
    const ref = TestBed.inject(MatDialog).open(CommitDialog, { disableClose: true });
    const closed = firstValueFrom(ref.afterClosed());
    await shown();
    if (preview) {
      (await requestTo(http, previewUrl, 'POST')).flush(preview);
      await shown();
    }
    const element = document.querySelector<HTMLElement>('mat-dialog-container')!;
    return { ref, closed, element };
  }

  const tabs = (element: HTMLElement) =>
    [...element.querySelectorAll<HTMLElement>('[role=tab]')].map((tab) => textOf(tab));

  /** The editor of a connection's script. */
  const editorOf = (source: string) => {
    const editor = monaco.live.find(
      (each) => each.options['ariaLabel'] === `The script for ${source}`,
    );
    if (!editor) {
      throw new Error(`No editor for ${source}`);
    }
    return editor;
  };
  const noticeOf = (element: HTMLElement) =>
    textOf(element.querySelector('[role=status] gd-message .text'));

  const wh = scriptOf({
    source: 'wh',
    kind: 'duckdb',
    dialect: 'DuckDB',
    text: 'UPDATE stock SET qty = 8 WHERE code = $1;\n',
    editable: false,
    statements: [
      {
        change: 3,
        kind: 'update',
        description: "the update of wh.stock (code = 'P-100')",
        text: '',
      },
    ],
  });

  it("previews the changes: each connection's script in a tab, in an editor", async () => {
    const { element } = await opened(
      previewOf({ multiConnection: true, scripts: [scriptOf(), wh] }),
    );
    expect(textOf(element.querySelector('h2'))).toBe('Commit the changes');
    expect(textOf(element.querySelector('gd-message'))).toContain(
      'These changes write to shop and wh, which commit one after another',
    );
    expect(tabs(element)).toEqual(['shop', 'wh']);
    expect(textOf(element.querySelector('.about'))).toContain('SQLite: 1 statement: 1 update.');
    expect(textOf(element.querySelector('.statements li'))).toBe(
      'The update of shop.orders (id = 1001)',
    );
    const shop = editorOf('shop');
    expect(shop.model.getValue()).toBe(scriptOf().text);
    expect(shop.model.language).toBe('sql');
    expect(shop.options).toMatchObject({ readOnly: false });
    expect(textOf(element.querySelector('.aside:last-child'))).toContain(
      'The preview may be committed until',
    );

    // DuckDB's scripts are edited by administrators alone; this preview says so.
    element.querySelectorAll<HTMLElement>('[role=tab]')[1].click();
    await shown();
    expect(editorOf('wh').model.language).toBe('pgsql');
    expect(editorOf('wh').options).toMatchObject({
      readOnly: true,
      readOnlyMessage: { value: "Only administrators edit DuckDB's scripts" },
    });
    expect(textOf(element.querySelectorAll('.about')[1])).toContain(
      "Only administrators edit DuckDB's scripts, which run in the application.",
    );
  });

  it('names the scripts tabs, and says what came of a commit', async () => {
    const announcer = TestBed.inject(LiveAnnouncer);
    const announce = vi.spyOn(announcer, 'announce');
    const { element } = await opened();
    expect(element.querySelector('[role=tablist]')?.getAttribute('aria-label')).toBe(
      'The scripts, by connection',
    );
    expect(announce).toHaveBeenCalledWith('Previewed: the scripts are ready to commit');
    clickButton(element, 'Commit');
    (await requestTo(http, commitUrl, 'POST')).flush(resultOf());
    await shown();
    expect(announce).toHaveBeenCalledWith('Committed: the changes were written to shop.');
  });

  it("lists what can't be made as it is, and commits nothing", async () => {
    const { element } = await opened(
      previewOf({
        planId: null,
        expiresAt: null,
        issues: [
          { change: 2, column: 'city', message: "'city' needs a value" },
          { change: 1, column: 'status', message: 'Not one of open, paid' },
          { change: 9, column: null, message: 'The entity is gone' },
          { change: 3, column: null, message: 'wh takes no changes' },
        ],
      }),
    );
    expect(textOf(element.querySelector('.issues h3'))).toBe("4 changes can't be made as they are");
    expect(
      [...element.querySelectorAll('.issues li')].map((item) =>
        textOf(item.querySelector(':scope > span')),
      ),
    ).toEqual([
      "shop.customers, new row 1: 'city' needs a value",
      'shop.orders, row 1001, status: Not one of open, paid',
      'A change: The entity is gone',
      'wh.stock, row P-100: wh takes no changes',
    ]);
    const commit = [...element.querySelectorAll('button')].find(
      (button) => textOf(button) === 'Commit',
    )!;
    expect(commit.getAttribute('aria-disabled')).toBe('true');
    expect(element.textContent).not.toContain('The preview may be committed until');

    clickButton(element, 'Preview again');
    (await requestTo(http, previewUrl, 'POST')).flush(previewOf({ scripts: [] }));
    await shown();
    expect(textOf(element.querySelector('.empty'))).toBe('There are no changes to commit.');
  });

  it('commits the scripts as previewed, and says what came of it', async () => {
    const { element, closed } = await opened();
    clickButton(element, 'Commit');
    await shown();
    expect(element.querySelector('mat-progress-bar')?.getAttribute('aria-label')).toBe(
      'Committing the changes',
    );
    const sent = await requestTo(http, commitUrl, 'POST');
    expect(sent.request.body).toEqual({
      planId: 'plan-1',
      version: 1,
      scripts: null,
      allowAnyStatement: false,
    });
    sent.flush(
      resultOf({
        inserted: [
          { tempId: 't1', entity: 'shop.customers', key: ['9'], rowId: '["9"]', values: {} },
        ],
        warnings: ['statement 1 (line 1) of shop changed no rows'],
      }),
    );
    await shown();
    expect(document.activeElement?.textContent).toBe('What came of the commit');
    expect(textOf(element.querySelector('.outcome [role=status]'))).toContain(
      'Committed: the changes were written to shop.',
    );
    expect([...element.querySelectorAll('.results li')].map((item) => textOf(item))).toEqual([
      'shop: committed, 1 statement, 1 row changed.',
    ]);
    expect(element.textContent).toContain('statement 1 (line 1) of shop changed no rows');
    expect(element.textContent).toContain('1 new row was made.');
    expect(element.textContent).toContain('No changes are left pending.');
    expect(element.querySelector('a[href="/admin/audit/commits/12"]')?.textContent).toBe(
      'Commit 12 in the audit',
    );
    // None left: nothing to preview again.
    expect([...element.querySelectorAll('button')].map((button) => textOf(button))).toEqual([
      'Close',
    ]);
    clickButton(element, 'Close');
    expect((await closed)?.auditId).toBe(12);
  });

  it('commits edited scripts (any statement, when an administrator allows it), and undoes edits', async () => {
    const { element } = await opened();
    const checkbox = element.querySelector<HTMLInputElement>('mat-checkbox input')!;
    expect(checkbox.disabled).toBe(true);
    monaco.last.model.type('DELETE FROM orders WHERE id = 1001;');
    await shown();
    expect(tabs(element)).toEqual(['shop edited']);
    clickButton(element, 'undo Undo my edits');
    await shown();
    expect(monaco.last.model.edits).toEqual([scriptOf().text]);
    expect(tabs(element)).toEqual(['shop']);
    // The button goes: the keyboard goes to the editor.
    expect(document.activeElement).toBe(monaco.last.input);
    // Its line breaks, and the white space after it, aren't edits.
    monaco.last.model.type(scriptOf().text.replace('\n', '\r\n  \n'));
    await shown();
    expect(tabs(element)).toEqual(['shop']);

    // Undone, the script isn't edited, though another preview's is another text.
    monaco.last.model.type('UPDATE z;');
    await shown();
    clickButton(element, 'undo Undo my edits');
    await shown();
    clickButton(element, 'Preview again');
    (await requestTo(http, previewUrl, 'POST')).flush(
      previewOf({ scripts: [scriptOf({ text: 'UPDATE w;\n' })] }),
    );
    await shown();
    expect(element.querySelector('[role=status] gd-message')).toBeNull();
    clickButton(element, 'Preview again');
    (await requestTo(http, previewUrl, 'POST')).flush(previewOf());
    await shown();

    monaco.last.model.type('VACUUM;');
    await shown();
    expect(checkbox.disabled).toBe(false);
    checkbox.click();
    await shown();
    // Not without edits.
    clickButton(element, 'undo Undo my edits');
    await shown();
    clickButton(element, 'Commit');
    const unedited = await requestTo(http, commitUrl, 'POST');
    expect(unedited.request.body).toMatchObject({ scripts: null, allowAnyStatement: false });
    unedited.flush(
      problemBody('plan-stale', "The preview can't be committed", {
        detail: 'The preview expired, or another replaced it: preview the changes again',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await shown();
    (await requestTo(http, previewUrl, 'POST')).flush(previewOf());
    await shown();
    monaco.last.model.type('VACUUM;');
    await shown();
    clickButton(element, 'Commit');
    const sent = await requestTo(http, commitUrl, 'POST');
    expect(sent.request.body).toEqual({
      planId: 'plan-1',
      version: 1,
      scripts: [{ source: 'shop', text: 'VACUUM;' }],
      allowAnyStatement: true,
    });
    sent.flush(resultOf());
    await shown();
  });

  it('places what is wrong with an edited script in it, till it is edited again', async () => {
    const { element } = await opened(previewOf({ scripts: [wh, scriptOf()] }));
    element.querySelectorAll<HTMLElement>('[role=tab]')[1].click();
    await shown();
    const editor = editorOf('shop');
    editor.model.type('UPDATE orders SET status = 1;\nDROP TABLE orders;');
    await shown();
    element.querySelectorAll<HTMLElement>('[role=tab]')[0].click();
    await shown();
    clickButton(element, 'Commit');
    (await requestTo(http, commitUrl, 'POST')).flush(
      problemBody('script-invalid', "The script for shop can't run", {
        source: 'shop',
        problems: [
          { message: 'Only statements that change data run', line: 2, start: 30, length: 18 },
        ],
      }),
      { status: 422, statusText: 'Unprocessable' },
    );
    await shown();
    // Its tab is shown, its problems said, and marked.
    expect(element.querySelector('[role=tab][aria-selected=true]')?.textContent).toContain('shop');
    expect(alertsOf(element)).toContain(
      "The script for shop can't run. Line 2: Only statements that change data run",
    );
    expect(editor.model.markers).toEqual([
      expect.objectContaining({
        message: 'Only statements that change data run',
        startLineNumber: 2,
        startColumn: 1,
        endLineNumber: 2,
        endColumn: 19,
      }),
    ]);
    expect(editor.input.getAttribute('aria-describedby')).toContain('-problems');

    // Another script edited keeps them.
    editorOf('wh').model.type('UPDATE stock SET qty = 9;');
    await shown();
    expect(editor.model.markers.length).toBe(1);

    editor.model.type('UPDATE orders SET status = 1;');
    await shown();
    expect(editor.model.markers).toEqual([]);
    expect(alertsOf(element)).toBe('');

    // Scripts that may not be edited are sent as planned.
    clickButton(element, 'Commit');
    const sent = await requestTo(http, commitUrl, 'POST');
    expect(sent.request.body).toMatchObject({
      scripts: [{ source: 'shop', text: 'UPDATE orders SET status = 1;' }],
    });
    sent.flush(resultOf());
    await shown();
  });

  it('previews again when the preview went out of date, keeping edits whose scripts stay the same', async () => {
    const { element } = await opened(previewOf({ scripts: [scriptOf(), wh] }));
    editorOf('shop').model.type('UPDATE orders SET status = 2;');
    await shown();
    clickButton(element, 'Commit');
    (await requestTo(http, commitUrl, 'POST')).flush(
      problemBody('plan-stale', "The preview can't be committed", {
        detail: 'The changes were changed since they were previewed: preview the changes again',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await shown();
    (await requestTo(http, previewUrl, 'POST')).flush(
      previewOf({ planId: 'plan-2', version: 2, scripts: [scriptOf(), wh] }),
    );
    // Changed elsewhere: read again.
    await answerChanges(http, setOf(changes.changes, 2));
    await shown();
    expect(noticeOf(element)).toBe(
      "The preview couldn't be committed (the changes were changed since they were previewed), so the changes were previewed again: check them, and commit.",
    );
    expect(tabs(element)).toEqual(['shop edited', 'wh']);
    expect(editorOf('shop').model.getValue()).toBe('UPDATE orders SET status = 2;');

    // A script that changed lets its edits go, and says so.
    clickButton(element, 'Preview again');
    (await requestTo(http, previewUrl, 'POST')).flush(
      previewOf({ planId: 'plan-3', version: 2, scripts: [scriptOf({ text: 'UPDATE x;\n' })] }),
    );
    await shown();
    expect(tabs(element)).toEqual(['shop']);
    expect(noticeOf(element)).toBe(
      "Your edits to the script for shop were let go: the changes to commit aren't what they were.",
    );

    expect(editorOf('shop').model.getValue()).toBe('UPDATE x;\n');

    // So are edits to a script there is none of now.
    editorOf('shop').model.type('UPDATE y;');
    await shown();
    clickButton(element, 'Preview again');
    (await requestTo(http, previewUrl, 'POST')).flush(
      previewOf({ planId: 'plan-4', version: 2, scripts: [wh] }),
    );
    await shown();
    expect(noticeOf(element)).toBe(
      "Your edits to the script for shop were let go: the changes to commit aren't what they were.",
    );
  });

  it('says what came of a commit that wrote nothing, which change stopped it, and goes back to the preview', async () => {
    const { element } = await opened();
    editorOf('shop').model.type('UPDATE orders SET status = 4;');
    await shown();
    clickButton(element, 'Commit');
    (await requestTo(http, commitUrl, 'POST')).flush(
      resultOf({
        outcome: 'rolledBack',
        scripts: [
          {
            source: 'shop',
            edited: false,
            status: 'rolledBack',
            error: 'The row was changed or deleted since it was read',
            statements: [{ change: 1, description: 'the update', rowsChanged: 0 }],
          },
        ],
        failure: {
          kind: 'conflict',
          source: 'shop',
          message: 'The row was changed or deleted since it was read',
          change: 1,
          statement: 'the update',
        },
        changes,
      }),
    );
    await shown();
    expect(alertsOf(element)).toBe(
      'error Nothing was written. The row was changed or deleted since it was read.',
    );
    expect(textOf(element.querySelector('.results li'))).toBe(
      'shop: rolled back, 1 statement. The row was changed or deleted since it was read',
    );
    expect(element.textContent).toContain('The change that stopped it: shop.orders, row 1001.');
    expect(element.textContent).toContain('3 changes are still pending.');

    // Closing asks first, as the edits weren't committed.
    clickButton(element, 'Close');
    await shown();
    clickButton(document.querySelector<HTMLElement>('gd-confirm-dialog')!, 'Keep editing');
    await shown();

    clickButton(element, 'Back to the preview');
    (await requestTo(http, previewUrl, 'POST')).flush(previewOf());
    await shown();
    // The edits are there still, and the keyboard is on the dialog's title.
    expect(tabs(element)).toEqual(['shop edited']);
    expect(editorOf('shop').model.getValue()).toBe('UPDATE orders SET status = 4;');
    expect(document.activeElement).toBe(element.querySelector('h2'));
  });

  it('says a commit written in part, and a problem with a commit', async () => {
    const { element } = await opened(previewOf({ scripts: [scriptOf(), wh] }));
    clickButton(element, 'Commit');
    (await requestTo(http, commitUrl, 'POST')).flush(
      problemBody('commit-in-progress', 'The changes are being committed', {
        detail: 'Preview them again once the commit has finished',
      }),
      { status: 409, statusText: 'Conflict' },
    );
    await shown();
    expect(alertsOf(element)).toBe(
      'error The changes are being committed. Preview them again once the commit has finished. Preview again',
    );
    clickButton(element, 'Commit');
    (await requestTo(http, commitUrl, 'POST')).flush(
      resultOf({
        outcome: 'partiallyCommitted',
        scripts: [
          { source: 'shop', edited: false, status: 'committed', error: null, statements: [] },
          {
            source: 'wh',
            edited: false,
            status: 'commitFailed',
            error: 'Disk full',
            statements: [],
          },
        ],
        failure: {
          kind: 'commit',
          source: 'wh',
          message: 'Disk full',
          change: null,
          statement: null,
        },
      }),
    );
    await shown();
    expect(alertsOf(element)).toBe(
      'error The changes were written in part: to shop, not to the others. Disk full.',
    );
    expect([...element.querySelectorAll('.results li')].map((item) => textOf(item))).toEqual([
      'shop: committed, 0 statements.',
      'wh: its commit failed, 0 statements. Disk full',
    ]);
  });

  it("asks before letting edits go, and doesn't close while committing", async () => {
    const { element, closed } = await opened();
    let isClosed = false;
    void closed.then(() => (isClosed = true));
    monaco.last.model.type('UPDATE orders SET status = 3;');
    await shown();
    // Escape is the editor's when it takes it.
    monaco.last.input.addEventListener('keydown', (event) => event.preventDefault());
    monaco.last.input.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    await shown();
    expect(document.querySelector('gd-confirm-dialog')).toBeNull();
    element.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await shown();
    const confirm = document.querySelector<HTMLElement>('gd-confirm-dialog')!;
    expect(textOf(confirm.querySelector('h2'))).toBe('Let your edits go?');
    clickButton(confirm, 'Keep editing');
    await shown();
    expect(isClosed).toBe(false);

    clickButton(element, 'Commit');
    await shown();
    const cancel = [...element.querySelectorAll('button')].find(
      (button) => textOf(button) === 'Cancel',
    )!;
    expect(cancel.disabled).toBe(true);
    element.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await shown();
    expect(document.querySelector('gd-confirm-dialog')).toBeNull();
    expect(monaco.last.options['readOnly']).toBe(true);
    (await requestTo(http, commitUrl, 'POST')).flush(resultOf());
    await shown();
    // Committed, it closes without asking.
    element.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await shown();
    expect(isClosed).toBe(true);
  });

  it('offers data managers no statement but those that change data, nor the audit', async () => {
    const { element } = await opened(previewOf(), 'dataManager');
    expect(element.querySelector('mat-checkbox')).toBeNull();
    clickButton(element, 'Commit');
    // Committed, with changes made since the preview left.
    (await requestTo(http, commitUrl, 'POST')).flush(
      resultOf({ changes: setOf([changeOf({ id: 8 })], 2) }),
    );
    await shown();
    expect(element.querySelector('a[href^="/admin/audit"]')).toBeNull();
    expect(element.textContent).toContain('1 change is still pending.');
    clickButton(element, 'Back to the preview');
    (await requestTo(http, previewUrl, 'POST')).flush(previewOf({ version: 2 }));
    await shown();
    expect(tabs(element)).toEqual(['shop']);
  });

  it("says why the changes couldn't be previewed, and previews them again", async () => {
    const { element } = await opened(null);
    (await requestTo(http, previewUrl, 'POST')).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Error',
    });
    await shown();
    expect(alertsOf(element)).toBe('error Oops. Preview again');
    clickButton(element.querySelector('[role=alert]')!, 'Preview again');
    (await requestTo(http, previewUrl, 'POST')).flush(previewOf());
    await shown();
    expect(alertsOf(element)).toBe('');
    expect(tabs(element)).toEqual(['shop']);
  });
});
