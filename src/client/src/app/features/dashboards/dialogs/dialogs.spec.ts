import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { dashboardOf, rowsOf, salesDefinition } from '../../../../testing/dashboards';
import { FakeECharts, fakeEChartsProviders } from '../../../../testing/echarts';
import { requestTo, settle } from '../../../../testing/http';
import { openPage, pageProviders, textOf, wordsOf } from '../../../../testing/pages';
import { FakeResizeObserver } from '../../../../testing/resize';
import { dashboardRoutes } from '../dashboards.routes';
import { DASHBOARD_WAITS } from '../state/waits';
import { iframeSnippet } from './share-dialog';

type Dto = ReturnType<typeof dashboardOf>;

describe("a dashboard's publishing and sharing", () => {
  beforeEach(() => {
    FakeResizeObserver.install();
    TestBed.configureTestingModule({
      providers: [
        pageProviders([{ path: 'dashboards', children: dashboardRoutes }]),
        fakeEChartsProviders(new FakeECharts()),
        { provide: DASHBOARD_WAITS, useValue: { preview: 1, filterTyping: 1 } },
      ],
    });
  });

  afterEach(() => {
    FakeResizeObserver.uninstall();
    TestBed.resetTestingModule();
  });

  /** Opens the dashboard's page, its widgets answered. */
  async function open(dashboard: Dto) {
    const page = await openPage('/dashboards/1');
    (await requestTo(page.http, '/api/dashboards/1')).flush(dashboard);
    await settle(5);
    for (const request of page.http.match((r) => r.url.includes('/data'))) {
      request.flush(rowsOf('Status', []));
    }
    await settle();
    page.harness.detectChanges();
    return page;
  }

  function button(container: ParentNode, text: string): HTMLButtonElement {
    const found = [...container.querySelectorAll<HTMLButtonElement>('button')].find(
      (b) => wordsOf(b) === text,
    );
    if (!found) {
      throw new Error(`No button "${text}"`);
    }
    return found;
  }

  /** The dialog opened by a button of the page's bar. */
  async function dialogOf(page: HTMLElement, harness: { detectChanges(): void }, text: string) {
    button(page, text).click();
    for (let turn = 0; turn < 20 && !document.querySelector('mat-dialog-container'); turn++) {
      await settle(1);
      harness.detectChanges();
    }
    return document.querySelector<HTMLElement>('mat-dialog-container')!;
  }

  const changed = () => {
    const definition = salesDefinition();
    return dashboardOf(definition, {
      working: {
        ...definition,
        widgets: definition.widgets.map((w) =>
          w.id === 'by-city' ? { ...w, title: 'Cities' } : w,
        ),
      },
      hasUnpublishedChanges: true,
    });
  };

  it('publishes the working copy with a note, saying what changed since the last revision', async () => {
    const { page, http, harness } = await open(changed());
    const dialog = await dialogOf(page, harness, 'Publish');
    expect(textOf(dialog.querySelector('.changes'))).toBe('Changed Cities.');
    const note = dialog.querySelector<HTMLInputElement>('input')!;
    note.value = 'Cities named';
    note.dispatchEvent(new Event('input'));
    harness.detectChanges();
    button(dialog, 'Publish').click();
    const post = await requestTo(http, '/api/dashboards/1/publish', 'POST');
    expect(post.request.body).toEqual({ version: 2, note: 'Cities named' });
    post.flush(dashboardOf(changed().working!, { publishedNumber: 2, version: 3 }));
    await settle(5);
    harness.detectChanges();
    expect(document.querySelector('mat-dialog-container')).toBeNull();
    // Published, there is nothing to publish.
    expect([...page.querySelectorAll('button')].some((b) => wordsOf(b) === 'Publish')).toBe(false);
  });

  it('shares it with people found by name, saved at its version', async () => {
    const { page, http, harness } = await open(dashboardOf());
    const dialog = await dialogOf(page, harness, 'Share');
    [...dialog.querySelectorAll<HTMLElement>('mat-radio-button')]
      .find((r) => textOf(r) === 'People you choose')!
      .querySelector('input')!
      .click();
    harness.detectChanges();
    const find = dialog.querySelector<HTMLInputElement>('input[placeholder="Find by name"]')!;
    find.value = 'gr';
    find.dispatchEvent(new Event('input', { bubbles: true }));
    find.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    harness.detectChanges();
    (await requestTo(http, '/api/dashboards/people?text=gr&take=20')).flush([
      { id: 5, userName: 'grace', displayName: 'Grace Hopper' },
    ]);
    await settle();
    harness.detectChanges();
    [...document.querySelectorAll<HTMLElement>('mat-option')]
      .find((o) => textOf(o) === 'Grace Hopper (grace)')!
      .click();
    harness.detectChanges();
    button(dialog, 'Save who sees it').click();
    const put = await requestTo(http, '/api/dashboards/1/sharing', 'PUT');
    expect(put.request.body).toEqual({ everyone: false, users: [5], version: 2 });
    put.flush(
      dashboardOf(salesDefinition(), {
        sharing: {
          everyone: false,
          users: [{ id: 5, userName: 'grace', displayName: 'Grace Hopper' }],
        },
        version: 3,
      }),
    );
    await settle();
    harness.detectChanges();
    expect(textOf(dialog.querySelector('[role=status]'))).toBe('Saved: who sees it');
  });

  it('gives it a public link with the snippet that embeds it, refusing sites by line, and stops it', async () => {
    const { page, http, harness } = await open(dashboardOf());
    const dialog = await dialogOf(page, harness, 'Share');
    dialog.querySelector<HTMLButtonElement>('mat-slide-toggle button')!.click();
    harness.detectChanges();
    const on = await requestTo(http, '/api/dashboards/1/public', 'PUT');
    expect(on.request.body).toEqual({ enabled: true, origins: [], version: 2 });
    const token = 'Abc123_-Abc123_-Abc123';
    const shared = (origins: string[], version: number) =>
      dashboardOf(salesDefinition(), {
        public: {
          token,
          origins,
          enabledBy: 'ada',
          enabledAt: '2026-03-02T09:00:00Z',
          works: true,
        },
        version,
      });
    on.flush(shared([], 3));
    await settle();
    harness.detectChanges();
    const link = `${location.origin}/embed/${token}`;
    const fields = [
      ...dialog.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>(
        'input[readonly], textarea[readonly]',
      ),
    ];
    expect(fields.map((f) => f.value)).toEqual([link, iframeSnippet(link, 'Sales')]);
    const sites = dialog.querySelector<HTMLTextAreaElement>('textarea:not([readonly])')!;
    sites.value = 'https://intranet.example.com\nftp://files';
    sites.dispatchEvent(new Event('input'));
    harness.detectChanges();
    button(dialog, 'Save the sites').click();
    const refused = await requestTo(http, '/api/dashboards/1/public', 'PUT');
    expect(refused.request.body.origins).toEqual(['https://intranet.example.com', 'ftp://files']);
    refused.flush(
      {
        status: 400,
        code: 'invalid-request',
        title: 'Invalid',
        errors: { 'origins[1]': ['Not an http or https origin'] },
      },
      { status: 400, statusText: 'Bad request' },
    );
    await settle();
    harness.detectChanges();
    expect(textOf(dialog.querySelector('.problem'))).toBe(
      'ftp://files: Not an http or https origin',
    );
    // Stopping it asks first.
    dialog.querySelector<HTMLButtonElement>('mat-slide-toggle button')!.click();
    await settle();
    harness.detectChanges();
    const confirm = [...document.querySelectorAll('mat-dialog-container')].at(-1)!;
    expect(textOf(confirm.querySelector('h2'))).toBe('Stop the public link?');
    button(confirm, 'Stop it').click();
    const off = await requestTo(http, '/api/dashboards/1/public', 'PUT');
    expect(off.request.body).toEqual({ enabled: false, origins: null, version: 3 });
    off.flush(dashboardOf(salesDefinition(), { version: 4 }));
  });

  it('copies it under a name of its own, opening the copy', async () => {
    const { page, http, harness } = await open(dashboardOf());
    page.querySelector<HTMLButtonElement>('[aria-label=More]')!.click();
    harness.detectChanges();
    await settle();
    button(document.querySelector('.mat-mdc-menu-panel')!, 'Copy').click();
    for (let turn = 0; turn < 20 && !document.querySelector('mat-dialog-container'); turn++) {
      await settle(1);
      harness.detectChanges();
    }
    const dialog = document.querySelector<HTMLElement>('mat-dialog-container')!;
    expect(dialog.querySelector<HTMLInputElement>('input')!.value).toBe('Sales (copy)');
    button(dialog, 'Copy').click();
    const post = await requestTo(http, '/api/dashboards/1/copy', 'POST');
    expect(post.request.body).toEqual({ name: 'Sales (copy)' });
    post.flush(dashboardOf(salesDefinition(), { id: 9, name: 'Sales (copy)' }));
    await settle(5);
    expect(TestBed.inject(Router).url).toBe('/dashboards/9');
    (await requestTo(http, '/api/dashboards/9')).flush(dashboardOf(salesDefinition(), { id: 9 }));
  });
});
