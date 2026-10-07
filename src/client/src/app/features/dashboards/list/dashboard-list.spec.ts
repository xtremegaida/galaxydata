import { TestBed } from '@angular/core/testing';
import type { Schema } from '../../../core/api/api-client';
import { sessionOf } from '../../../../testing/auth';
import { requestTo, settle } from '../../../../testing/http';
import { openPage, pageProviders, textOf, wordsOf } from '../../../../testing/pages';
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
      'Make private',
      'Stop its public link',
      'Delete',
    ]);
    items[1].click();
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
      'Delete',
    ]);
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();
    harness.detectChanges();
    expect((await menuOf(page, harness, 'Grace’s board')).map((i) => wordsOf(i))).toEqual(['Copy']);
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
