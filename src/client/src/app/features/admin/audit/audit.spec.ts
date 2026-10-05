import { TestBed } from '@angular/core/testing';
import { problemBody } from '../../../../testing/auth';
import { requestTo, settle } from '../../../../testing/http';
import { alertsOf, clickButton, openPage, pageProviders, textOf } from '../../../../testing/pages';
import { adminRoutes } from '../admin.routes';
import { type AdminEvent, type CommitAudit, type CommitSummary, detailLines } from './audit';

function commitOf(changes: Partial<CommitSummary> = {}): CommitSummary {
  return {
    id: 12,
    startedAt: '2026-10-05T08:00:00Z',
    finishedAt: '2026-10-05T08:00:01Z',
    user: 'ada',
    status: 'committed',
    changes: 3,
    sources: ['shop'],
    edited: false,
    anyStatement: false,
    failure: null,
    ...changes,
  };
}

function eventOf(changes: Partial<AdminEvent> = {}): AdminEvent {
  return {
    id: 7,
    at: '2026-10-05T08:00:00Z',
    actor: 'ada',
    action: 'user.updated',
    target: 'user:carol',
    details: '{"role":{"from":"dataManager","to":"read"}}',
    ...changes,
  };
}

/** Entries numbered down from `first`. */
function numbered<T>(count: number, first: number, of: (id: number) => T): T[] {
  return Array.from({ length: count }, (_, index) => of(first - index));
}

describe('The audit', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: pageProviders([{ path: 'admin', children: adminRoutes }]),
    });
  });

  async function opened(url: string) {
    const page = await openPage(url);
    const shown = async () => {
      await settle();
      page.harness.detectChanges();
    };
    return { ...page, shown };
  }

  /** A list of facts, by what each is. */
  const factsOf = (list: Element | null) =>
    Object.fromEntries(
      [...(list?.querySelectorAll('dt') ?? [])].map((term) => [
        textOf(term),
        textOf(term.nextElementSibling),
      ]),
    );
  const hasButton = (page: HTMLElement, text: string) =>
    [...page.querySelectorAll('button')].some((button) => textOf(button) === text);

  const rowsOf = (page: HTMLElement) =>
    [...page.querySelectorAll('tbody tr')].map((row) =>
      [...row.querySelectorAll('td')].map((cell) => textOf(cell)),
    );

  it('lists the commits, newest first, a page at a time', async () => {
    const { page, http, shown } = await opened('/admin/audit');
    const first = await requestTo(http, '/api/audit/commits?take=51');
    first.flush([
      commitOf({ id: 60, edited: true, sources: ['shop', 'wh'] }),
      commitOf({
        id: 59,
        status: 'rolledBack',
        failure: 'The row was changed or deleted since it was read',
        edited: true,
        anyStatement: true,
      }),
      commitOf({ id: 58, status: 'inProgress', finishedAt: null }),
      ...numbered(48, 57, (id) => commitOf({ id, status: 'unknown' })),
    ]);
    await shown();
    // The first page leaves the keyboard where it was.
    expect(document.activeElement?.closest('tbody')).toBeNull();
    expect(textOf(page.querySelector('h1'))).toBe('Audit');
    const rows = rowsOf(page);
    expect(rows.length).toBe(50);
    expect(rows[0]).toEqual([
      '60',
      expect.any(String),
      'ada',
      'Committed Edited',
      '3',
      'shop, wh',
      '',
    ]);
    expect(rows[1]).toEqual([
      '59',
      expect.any(String),
      'ada',
      'Rolled back Edited, any statement',
      '3',
      'shop',
      'The row was changed or deleted since it was read',
    ]);
    expect(page.querySelector('tbody tr:nth-child(2) .badge')?.classList).toContain('warn');
    expect(page.querySelector('tbody tr:first-child .badge')?.classList).not.toContain('warn');
    expect(page.querySelector('tbody tr:nth-child(3) .badge')?.classList).not.toContain('warn');
    expect(page.querySelector('tbody tr:nth-child(4) .badge')?.classList).toContain('warn');
    expect(page.querySelector('a[href="/admin/audit/commits/60"]')).not.toBeNull();
    expect(page.querySelector('a.mdc-tab--active, a[aria-current=page]')?.textContent).toContain(
      'Commits of changes',
    );

    clickButton(page, 'Show more');
    const next = await requestTo(http, '/api/audit/commits?before=11&take=51');
    next.flush([commitOf({ id: 10 })]);
    await shown();
    await shown();
    expect(rowsOf(page).length).toBe(51);
    // The keyboard goes to the first of the page shown, as the button goes.
    expect(hasButton(page, 'Show more')).toBe(false);
    expect(document.activeElement?.textContent).toBe('10');
  });

  it('shows no more when a page is all there is', async () => {
    const { page, http, shown } = await opened('/admin/audit/commits');
    (await requestTo(http, '/api/audit/commits?take=51')).flush(
      numbered(50, 100, (id) => commitOf({ id })),
    );
    await shown();
    expect(rowsOf(page).length).toBe(50);
    expect(hasButton(page, 'Show more')).toBe(false);
  });

  it('lets go of a page asked for as it is left', async () => {
    const { page, http, shown, harness } = await opened('/admin/audit/events');
    (await requestTo(http, '/api/audit/admin-events?take=51')).flush(
      numbered(51, 100, (id) => eventOf({ id })),
    );
    await shown();
    clickButton(page, 'Show more');
    const next = await requestTo(http, '/api/audit/admin-events?before=51&take=51');
    await harness.navigateByUrl('/admin/audit/commits');
    expect(next.cancelled).toBe(true);
    (await requestTo(http, '/api/audit/commits?take=51')).flush([]);
  });

  it("says the commits couldn't be read, and reads them again", async () => {
    const { page, http, shown } = await opened('/admin/audit/commits');
    (await requestTo(http, '/api/audit/commits?take=51')).flush(
      problemBody('internal-error', 'Oops'),
      { status: 500, statusText: 'Error' },
    );
    await shown();
    expect(alertsOf(page)).toBe('error Oops. Try again');
    clickButton(page, 'Try again');
    (await requestTo(http, '/api/audit/commits?take=51')).flush([]);
    await shown();
    expect(alertsOf(page)).toBe('');
    expect(textOf(page.querySelector('.empty'))).toBe('No changes have been committed yet.');
  });

  it('shows a commit: what came of it, and the scripts it ran', async () => {
    const { page, http, shown } = await opened('/admin/audit/commits/11');
    const commit: CommitAudit = {
      id: 11,
      startedAt: '2026-10-05T08:00:00Z',
      finishedAt: '2026-10-05T08:00:02.5Z',
      user: 'ada',
      status: 'partiallyCommitted',
      changes: 2,
      edited: true,
      anyStatement: true,
      catalogVersion: '0123456789abcdef',
      failureKind: 'commit',
      failure: 'Disk full',
      scripts: [
        {
          source: 'shop',
          kind: 'sqlite',
          dialect: 'SQLite',
          edited: false,
          status: 'committed',
          statements: 1,
          rowsChanged: 1,
          error: null,
          text: "UPDATE orders SET status = 'paid' WHERE id = 1001;",
        },
        {
          source: 'wh',
          kind: 'duckdb',
          dialect: 'DuckDB',
          edited: true,
          status: 'commitFailed',
          statements: 2,
          rowsChanged: null,
          error: 'Disk full',
          text: 'UPDATE stock SET qty = 8;',
        },
      ],
    };
    (await requestTo(http, '/api/audit/commits/11')).flush(commit);
    await shown();
    expect(textOf(page.querySelector('h1'))).toBe('Commit 11');
    expect([...page.querySelectorAll('.subtitle .badge')].map((badge) => textOf(badge))).toEqual([
      'Written in part',
      'Edited',
      'Any statement allowed',
    ]);
    const facts = factsOf(page.querySelector('dl.facts'));
    expect(facts['By']).toBe('ada');
    expect(facts['Finished']).toContain('(2.5 s)');
    expect(facts['Why it failed']).toBe('Disk full (commit)');
    expect(facts['Catalog version']).toBe('0123456789abcdef');
    const scripts = [...page.querySelectorAll('section.script')];
    expect(scripts.map((script) => textOf(script.querySelector('h3')))).toEqual([
      'shop · SQLite',
      'wh · DuckDB',
    ]);
    expect(factsOf(scripts[1].querySelector('dl'))).toEqual({
      Outcome: 'Its commit failed',
      Statements: '2, as edited',
      'Rows changed': 'Not counted',
      Error: 'Disk full',
    });
    const text = scripts[0].querySelector('pre')!;
    expect(text.textContent).toBe("UPDATE orders SET status = 'paid' WHERE id = 1001;");
    expect(text.getAttribute('tabindex')).toBe('0');
    expect(text.getAttribute('aria-label')).toBe('The script run on shop');
  });

  it('says a commit not known to have finished, and one not in the audit', async () => {
    const unknown = await opened('/admin/audit/commits/3');
    (await requestTo(unknown.http, '/api/audit/commits/3')).flush({
      ...commitOf({ id: 3, status: 'unknown', finishedAt: null }),
      catalogVersion: 'v',
      failureKind: null,
      scripts: [],
    });
    await unknown.shown();
    expect(factsOf(unknown.page.querySelector('dl.facts'))['Finished']).toBe(
      'Not known to have finished',
    );
    expect(textOf(unknown.page.querySelector('gd-message'))).toContain(
      'whether the changes were written must be checked in the databases',
    );
  });

  it("says a commit isn't in the audit", async () => {
    const { page, http, shown } = await opened('/admin/audit/commits/99');
    (await requestTo(http, '/api/audit/commits/99')).flush(
      problemBody('not-found', 'There is no such commit', {
        detail: 'There is no commit 99 in the audit',
      }),
      { status: 404, statusText: 'Not Found' },
    );
    await shown();
    expect(alertsOf(page)).toBe(
      'error There is no such commit. There is no commit 99 in the audit.',
    );
  });

  it("asks nothing for an address that isn't a commit's", async () => {
    const { page, http, shown } = await opened('/admin/audit/commits/1e2');
    await shown();
    expect(http.match(() => true).length).toBe(0);
    expect(alertsOf(page)).toBe(
      'error There is no such commit. There is no commit 1e2 in the audit.',
    );
  });

  it('lists what administrators did, with what it changed, a page at a time', async () => {
    const { page, http, shown } = await opened('/admin/audit/events');
    (await requestTo(http, '/api/audit/admin-events?take=51')).flush([
      eventOf(),
      eventOf({
        id: 6,
        actor: '(system)',
        action: 'user.created',
        target: 'user:ada',
        details: null,
      }),
    ]);
    await shown();
    expect(rowsOf(page).map((row) => row.slice(1))).toEqual([
      ['ada', 'user.updated', 'user:carol', 'role: dataManager → read'],
      ['(system)', 'user.created', 'user:ada', ''],
    ]);
    expect(hasButton(page, 'Show more')).toBe(false);
    expect(textOf(page.querySelector('[role=status]'))).toBe('');
  });

  it("reads administrators' actions' details as lines", () => {
    expect(
      detailLines(
        JSON.stringify({
          kind: 'sqlite',
          settings: { 'Data Source': { from: 'a.db', to: 'b.db' }, Cache: 'Shared' },
          secrets: { Password: 'cleared' },
          tags: [],
          names: ['Password', 'Token'],
          isReadOnly: { from: false, to: null },
        }),
      ),
    ).toEqual([
      { name: 'kind', value: 'sqlite' },
      { name: 'settings.Data Source', value: 'a.db → b.db' },
      { name: 'settings.Cache', value: 'Shared' },
      { name: 'secrets.Password', value: 'cleared' },
      { name: 'tags', value: 'none' },
      { name: 'names', value: 'Password, Token' },
      { name: 'isReadOnly', value: 'false → none' },
    ]);
    expect(detailLines(null)).toEqual([]);
    expect(detailLines('not json')).toEqual([{ name: '', value: 'not json' }]);
    expect(detailLines('3')).toEqual([{ name: '', value: '3' }]);
  });
});
