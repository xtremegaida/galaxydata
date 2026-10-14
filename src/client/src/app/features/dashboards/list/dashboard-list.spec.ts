import { TestBed } from '@angular/core/testing';
import { MatSelectHarness } from '@angular/material/select/testing';
import { Router } from '@angular/router';
import type { Schema } from '../../../core/api/api-client';
import { sessionOf } from '../../../../testing/auth';
import { catalogOf } from '../../../../testing/catalog';
import { dashboardOf, salesDefinition } from '../../../../testing/dashboards';
import { requestTo, settle } from '../../../../testing/http';
import { clickButton, openPage, pageProviders, textOf, wordsOf } from '../../../../testing/pages';
import { dashboardRoutes } from '../dashboards.routes';

type Summary = Schema<'DashboardSummaryDto'>;

function summaryOf(extra: Partial<Summary> = {}): Summary {
  return {
    id: 1,
    name: 'Sales',
    description: null,
    owner: 'ada',
    isMine: true,
    sharing: 'private',
    isPublic: false,
    publishedNumber: 1,
    publishedAt: '2026-03-01T09:00:00Z',
    hasUnpublishedChanges: false,
    updatedAt: '2026-03-01T09:00:00Z',
    version: 2,
    ...extra,
  };
}

describe('the dashboards', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [pageProviders([{ path: 'dashboards', children: dashboardRoutes }])],
    });
  });

  afterEach(() => TestBed.resetTestingModule());

  const others = summaryOf({
    id: 2,
    name: 'Grace’s board',
    owner: 'grace',
    isMine: false,
    sharing: 'everyone',
    isPublic: true,
    version: 7,
  });

  async function open(role: 'admin' | 'read' = 'admin') {
    const page = await openPage('/dashboards', sessionOf(role));
    (await requestTo(page.http, '/api/dashboards')).flush([summaryOf(), others]);
    await settle();
    page.harness.detectChanges();
    return page;
  }

  async function menuOf(page: HTMLElement, harness: { detectChanges(): void }, name: string) {
    page.querySelector<HTMLButtonElement>(`[aria-label="Actions of ${name}"]`)!.click();
    harness.detectChanges();
    await settle();
    return [...document.querySelectorAll<HTMLElement>('.mat-mdc-menu-panel [mat-menu-item]')];
  }

  async function confirmed(harness: { detectChanges(): void }, text: string) {
    await settle();
    harness.detectChanges();
    const dialog = document.querySelector('mat-dialog-container')!;
    [...dialog.querySelectorAll<HTMLButtonElement>('button')]
      .find((b) => textOf(b) === text)!
      .click();
  }

  it("lets administrators narrow another's dashboard: private, its link stopped", async () => {
    const { page, http, harness } = await open();
    const items = await menuOf(page, harness, 'Grace’s board');
    expect(items.map((i) => wordsOf(i))).toEqual([
      'Copy',
      'Export',
      'Make private',
      'Stop its public link',
      'Delete',
    ]);
    items.find((i) => wordsOf(i) === 'Make private')!.click();
    await confirmed(harness, 'Make private');
    const put = await requestTo(http, '/api/dashboards/2/sharing', 'PUT');
    expect(put.request.body).toEqual({ everyone: false, users: [], version: 7 });
    put.flush({});
    (await requestTo(http, '/api/dashboards')).flush([
      summaryOf(),
      { ...others, sharing: 'private' },
    ]);
    await settle();
    harness.detectChanges();
    expect(textOf(page.querySelector('[role=status] .done'))).toBe('Grace’s board: made private');
  });

  it('offers viewers what is theirs to do: their own to edit and delete, others to copy', async () => {
    const { page, harness } = await open('read');
    expect((await menuOf(page, harness, 'Sales')).map((i) => wordsOf(i))).toEqual([
      'Edit',
      'Copy',
      'Export',
      'Delete',
    ]);
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();
    harness.detectChanges();
    expect((await menuOf(page, harness, 'Grace’s board')).map((i) => wordsOf(i))).toEqual([
      'Copy',
      'Export',
    ]);
  });

  it('exports a dashboard as a file the server names', async () => {
    const { page, harness } = await open();
    const exported = (await menuOf(page, harness, 'Sales')).find((i) => wordsOf(i) === 'Export')!;
    expect(exported.getAttribute('href')).toBe('/api/dashboards/1/export');
    expect(exported.hasAttribute('download')).toBe(true);
  });

  /** A dashboard's file: the sales dashboard, its orders read from hw (not here), with a palette. */
  function fileOf(extra: Partial<Schema<'DashboardExport'>> = {}): Schema<'DashboardExport'> {
    const sales = salesDefinition();
    return {
      format: 'galaxydata.dashboard',
      version: 1,
      name: 'Sales',
      description: 'Orders and their customers',
      exportedAt: '2026-10-14T09:00:00Z',
      definition: {
        ...sales,
        sources: [{ ...sales.sources[0], entity: 'hw.web.orders' }, sales.sources[1]],
        palette: 3,
      },
      palettes: [
        {
          id: 3,
          name: 'Statuses',
          description: null,
          definition: {
            colors: [{ light: '#111111', dark: null }],
            assign: 'label',
            distinct: true,
            whenOut: 'repeat',
            matching: {
              ignoreCase: true,
              ignoreWhitespace: false,
              ignoreBrackets: false,
              ignoreAccents: false,
            },
            overrides: [],
          },
        },
      ],
      ...extra,
    };
  }

  /** Chooses a file in the page's file input, as the browser would, and waits for what it opens. */
  async function choose(
    page: HTMLElement,
    harness: { detectChanges(): void },
    name: string,
    text: string,
  ) {
    const input = page.querySelector<HTMLInputElement>('input[type=file]')!;
    Object.defineProperty(input, 'files', {
      configurable: true,
      value: [new File([text], name, { type: 'application/json' })],
    });
    input.dispatchEvent(new Event('change'));
    for (let turn = 0; turn < 20 && !document.querySelector('mat-dialog-container'); turn++) {
      await settle(1);
      harness.detectChanges();
    }
  }

  it('imports a file under a name of its own, its connections taken to those here, and opens it', async () => {
    const { page, http, harness, root } = await open('read');
    const file = fileOf();
    await choose(page, harness, 'sales.dashboard.json', JSON.stringify(file));
    (await requestTo(http, '/api/catalog')).flush(catalogOf());
    await settle();
    harness.detectChanges();
    const dialog = document.querySelector<HTMLElement>('mat-dialog-container')!;
    expect(textOf(dialog.querySelector('h2'))).toBe('Import Sales');
    expect(dialog.querySelector<HTMLInputElement>('input')!.value).toBe('Sales 2');
    expect(textOf(dialog)).toContain('Statuses: the same palettes here are used');
    const selects = await root.getAllHarnesses(MatSelectHarness);
    expect(await Promise.all(selects.map((s) => s.getValueText()))).toEqual([
      'hw (not here)',
      'shop · SQLite',
    ]);
    await selects[0].clickOptions({ text: /^pg/ });
    clickButton(dialog, 'Import');
    const post = await requestTo(http, '/api/dashboards/import', 'POST');
    expect(post.request.body).toEqual({ dashboard: file, name: 'Sales 2', aliases: { hw: 'pg' } });
    post.flush(dashboardOf(salesDefinition(), { id: 9, name: 'Sales 2' }));
    const router = TestBed.inject(Router);
    for (let turn = 0; turn < 20 && router.url !== '/dashboards/9'; turn++) {
      await settle(1);
    }
    expect(router.url).toBe('/dashboards/9');
  });

  it("says why it can't import a file, or what the server refused", async () => {
    const { page, http, harness } = await open();
    await choose(page, harness, 'notes.json', '{"format":"galaxydata.query"}');
    harness.detectChanges();
    expect(textOf(page.querySelector('[role=status] .done'))).toBe(
      "Couldn't import notes.json: It isn't a dashboard's file.",
    );
    await choose(page, harness, 'later.json', JSON.stringify(fileOf({ version: 2 })));
    harness.detectChanges();
    expect(textOf(page.querySelector('[role=status] .done'))).toBe(
      "Couldn't import later.json: It is of version 2; files here are of version 1.",
    );
    expect(document.querySelector('mat-dialog-container')).toBeNull();

    await choose(page, harness, 'sales.json', JSON.stringify(fileOf()));
    (await requestTo(http, '/api/catalog')).flush(catalogOf());
    await settle();
    harness.detectChanges();
    const dialog = document.querySelector<HTMLElement>('mat-dialog-container')!;
    clickButton(dialog, 'Import');
    (await requestTo(http, '/api/dashboards/import', 'POST')).flush(
      {
        status: 400,
        code: 'invalid-request',
        title: 'The request is invalid',
        errors: { 'dashboard.definition.sources[0].entity': ["'x y' isn't an entity's path"] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();
    harness.detectChanges();
    expect(wordsOf(dialog.querySelector('[role=alert]'))).toBe(
      "Couldn't import it: dashboard.definition.sources[0].entity: 'x y' isn't an entity's path",
    );
  });

  it('deletes a dashboard at its version, after asking', async () => {
    const { page, http, harness } = await open();
    const items = await menuOf(page, harness, 'Sales');
    items.find((i) => wordsOf(i) === 'Delete')!.click();
    await confirmed(harness, 'Delete');
    const deleted = await requestTo(http, '/api/dashboards/1?version=2', 'DELETE');
    deleted.flush(null);
    (await requestTo(http, '/api/dashboards')).flush([others]);
  });
});
