import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { fakeBrowserProviders, problemBody, sessionOf } from '../../../testing/auth';
import {
  FakeChangesChannel,
  answerChanges,
  changeOf,
  changesUrl,
  insertOf,
  opsUrl,
  setOf,
} from '../../../testing/changes';
import { requestTo, settle } from '../../../testing/http';
import { AuthStore } from '../auth/auth-store';
import type { UserRole } from '../auth/roles';
import {
  type ChangeOp,
  ChangesChannel,
  type PendingChange,
  PendingChanges,
  foldOp,
} from './pending-changes';

describe('PendingChanges', () => {
  let http: HttpTestingController;
  let channel: FakeChangesChannel;

  const row = { key: ['1001'], rowId: '["1001"]' };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        fakeBrowserProviders(),
        { provide: ChangesChannel, useClass: FakeChangesChannel },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    channel = TestBed.inject(ChangesChannel) as unknown as FakeChangesChannel;
  });

  afterEach(() => http.verify());

  /** The store, for a user signed in with a role. */
  async function storeOf(role: UserRole = 'dataManager'): Promise<PendingChanges> {
    const auth = TestBed.inject(AuthStore);
    const loaded = auth.ensure();
    http.expectOne('/api/auth/session').flush(sessionOf(role));
    await loaded;
    const store = TestBed.inject(PendingChanges);
    TestBed.tick();
    return store;
  }

  /** The store, its changes read. */
  async function loaded(changes: PendingChange[] = [], version = 1): Promise<PendingChanges> {
    const store = await storeOf();
    await answerChanges(http, setOf(changes, version));
    return store;
  }

  it('reads the changes of those who change data', async () => {
    const store = await storeOf('admin');
    expect(store.enabled()).toBe(true);
    expect(store.loaded()).toBe(false);
    await answerChanges(http, setOf([changeOf()]));
    expect(store.loaded()).toBe(true);
    expect(store.count()).toBe(1);
    expect(store.of('shop.orders').rows.get('["1001"]')?.values).toEqual({ status: 'paid' });
    expect(store.of('shop.customers').rows.size).toBe(0);
  });

  it("doesn't ask readers for changes", async () => {
    const store = await storeOf('read');
    expect(store.enabled()).toBe(false);
    store.reload();
    await settle();
    expect(store.count()).toBe(0);
  });

  it("shows an action at once, and the server's answer once it comes", async () => {
    const store = await loaded();
    const done = store.set('shop.orders', row, { status: 'paid' }, { status: 'open' });
    expect(store.saving()).toBe(true);
    expect(store.of('shop.orders').rows.get('["1001"]')).toMatchObject({
      kind: 'update',
      values: { status: 'paid' },
      original: { status: 'open' },
    });

    const request = await requestTo(http, opsUrl, 'POST');
    expect(request.request.body).toEqual({
      ops: [
        {
          op: 'set',
          entity: 'shop.orders',
          key: ['1001'],
          values: { status: 'paid' },
          original: { status: 'open' },
          display: {},
        },
      ],
    });
    request.flush(setOf([changeOf({ id: 7 })], 2));
    expect(await done).toEqual({ done: true });
    expect(store.saving()).toBe(false);
    expect(store.changes().map((change) => change.id)).toEqual([7]);
    expect(channel.announced).toEqual([2]);
  });

  it('sends actions one at a time, in the order they were asked for', async () => {
    const store = await loaded();
    const first = store.set('shop.orders', row, { status: 'paid' }, { status: 'open' });
    const { tempId, done: second } = store.insert('shop.orders', { status: 'new' });
    expect(store.count()).toBe(2);
    await settle();
    const requests = http.match(opsUrl);
    expect(requests.length).toBe(1);
    requests[0].flush(setOf([changeOf()], 2));
    await first;

    const next = await requestTo(http, opsUrl, 'POST');
    expect(next.request.body).toEqual({
      ops: [
        { op: 'insert', entity: 'shop.orders', tempId, values: { status: 'new' }, display: {} },
      ],
    });
    next.flush(setOf([changeOf(), insertOf(tempId, { id: 2, values: { status: 'new' } })], 3));
    expect(await second).toEqual({ done: true });
    expect(store.of('shop.orders').inserts.map((change) => change.tempId)).toEqual([tempId]);
  });

  it('drops an action the server refuses, says why, and goes on with the next', async () => {
    const store = await loaded();
    const refused = store.set('shop.orders', row, { total: '12x' }, { total: '12.50' });
    const next = store.delete('shop.orders', { key: ['1002'], rowId: '["1002"]' }, { id: '1002' });
    (await requestTo(http, opsUrl, 'POST')).flush(
      problemBody('invalid-request', 'One or more validation errors occurred', {
        errors: {
          'ops[0].values.total': ['"12x" isn\'t a number'],
          'ops[0].original.total': ["'total' was read otherwise"],
        },
      }),
      { status: 400, statusText: 'Bad Request' },
    );
    const outcome = await refused;
    expect(outcome.done).toBe(false);
    // The column is named before what is said of its value, unless that names it.
    expect(outcome.done ? [] : outcome.reasons).toEqual([
      'total: "12x" isn\'t a number',
      "'total' was read otherwise",
    ]);
    expect(store.changes().map((change) => change.kind)).toEqual(['delete']);

    (await requestTo(http, opsUrl, 'POST')).flush(
      setOf([changeOf({ kind: 'delete', key: ['1002'], rowId: '["1002"]', values: {} })], 2),
    );
    expect(await next).toEqual({ done: true });
  });

  it("says the problem of an action that couldn't be sent", async () => {
    const store = await loaded();
    const failed = store.revert('shop.orders', row);
    (await requestTo(http, opsUrl, 'POST')).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    const outcome = await failed;
    expect(outcome.done ? [] : outcome.reasons).toEqual(['Oops.']);
    // It may have been made all the same: the changes are read again.
    await answerChanges(http, setOf([], 2));
    expect(store.loaded()).toBe(true);
  });

  it('leaves the answer to a request made before one whose answer was taken', async () => {
    const store = await storeOf();
    const reading = await requestTo(http, changesUrl);
    const done = store.set('shop.orders', row, { status: 'paid' }, { status: 'open' });
    (await requestTo(http, opsUrl, 'POST')).flush(setOf([changeOf()], 5));
    await done;
    reading.flush(setOf([], 4));
    expect(store.count()).toBe(1);
  });

  it('reads the changes again when another tab changed them, once the actions sent are answered', async () => {
    const store = await loaded([], 3);
    channel.hear(3);
    await settle();
    // The same version: nothing to read.
    expect(http.match(changesUrl).length).toBe(0);

    const done = store.set('shop.orders', row, { status: 'paid' }, { status: 'open' });
    const sent = await requestTo(http, opsUrl, 'POST');
    channel.hear(4);
    await settle();
    expect(http.match(changesUrl).length).toBe(0);
    sent.flush(setOf([changeOf()], 4));
    await done;
    await answerChanges(http, setOf([changeOf(), insertOf('elsewhere', { id: 2 })], 5));
    expect(store.count()).toBe(2);
  });

  it('reads the changes again when the tab comes back into view, not as it goes', async () => {
    await loaded();
    const state = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
    document.dispatchEvent(new Event('visibilitychange'));
    await settle();
    expect(http.match(changesUrl).length).toBe(0);
    state.mockReturnValue('visible');
    document.dispatchEvent(new Event('visibilitychange'));
    await answerChanges(http, setOf([changeOf()], 2));
    vi.restoreAllMocks();
  });

  it('lets the changes go when the user may change data no more', async () => {
    const store = await loaded([changeOf()]);
    const auth = TestBed.inject(AuthStore);
    const asked = auth.load();
    http.expectOne('/api/auth/session').flush(sessionOf('read'));
    await asked;
    TestBed.tick();
    expect(store.enabled()).toBe(false);
    expect(store.loaded()).toBe(false);
    expect(store.count()).toBe(0);
  });

  it("leaves a failure to read the changes asked for before an answer taken, and forgets one once they're read", async () => {
    const store = await storeOf();
    const reading = await requestTo(http, changesUrl);
    const done = store.set('shop.orders', row, { status: 'paid' }, { status: 'open' });
    (await requestTo(http, opsUrl, 'POST')).flush(setOf([changeOf()], 2));
    await done;
    reading.flush(problemBody('internal-error', 'Oops'), { status: 500, statusText: 'Error' });
    expect(store.problem()).toBeNull();

    store.reload();
    (await requestTo(http, changesUrl)).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Error',
    });
    expect(store.problem()?.title).toBe('Oops');
    const next = store.revert('shop.orders', row);
    (await requestTo(http, opsUrl, 'POST')).flush(setOf([], 3));
    await next;
    expect(store.problem()).toBeNull();
  });

  it("says why the changes couldn't be read, until they are", async () => {
    const store = await storeOf();
    (await requestTo(http, changesUrl)).flush(problemBody('internal-error', 'Oops'), {
      status: 500,
      statusText: 'Server Error',
    });
    expect(store.problem()?.title).toBe('Oops');
    store.reload();
    await answerChanges(http);
    expect(store.problem()).toBeNull();
    expect(store.loaded()).toBe(true);
  });

  it("clears a source's or an entity's changes", async () => {
    const lines = changeOf({ id: 2, entity: 'shop.order_lines', key: ['1'], rowId: '["1"]' });
    const other = changeOf({ id: 3, source: 'wh', entity: 'wh.stock' });
    const store = await loaded([changeOf(), lines, other]);

    const cleared = store.clear({ entity: 'shop.orders' });
    expect(store.changes().map((change) => change.id)).toEqual([2, 3]);
    (await requestTo(http, `${changesUrl}?entity=shop.orders`, 'DELETE')).flush(
      setOf([lines, other], 2),
    );
    await cleared;

    const bySource = store.clear({ source: 'SHOP' });
    expect(store.changes().map((change) => change.id)).toEqual([3]);
    (await requestTo(http, `${changesUrl}?source=SHOP`, 'DELETE')).flush(setOf([other], 3));
    await bySource;

    const all = store.clear();
    expect(store.count()).toBe(0);
    (await requestTo(http, changesUrl, 'DELETE')).flush(setOf([], 4));
    await all;
  });

  it('names each new row apart', async () => {
    const store = await loaded();
    const first = store.insert('shop.orders');
    const second = store.insert('shop.orders');
    expect(first.tempId).not.toBe(second.tempId);
    (await requestTo(http, opsUrl, 'POST')).flush(setOf([], 2));
    (await requestTo(http, opsUrl, 'POST')).flush(setOf([], 3));
    await Promise.all([first.done, second.done]);
  });
});

describe('foldOp', () => {
  let ids = 0;
  const newId = () => --ids;
  const key = ['1001'];
  const rowId = '["1001"]';

  function folded(changes: PendingChange[], op: ChangeOp, id: string | null = rowId) {
    return foldOp(changes, { entity: 'shop.orders', ...op }, id, newId);
  }

  it("keeps a column's first original, and drops a value set back to it", () => {
    const changed = folded([], {
      op: 'set',
      key,
      values: { status: 'paid', total: '9.50' },
      original: { status: 'open', total: '12.50' },
    });
    expect(changed).toMatchObject([
      {
        kind: 'update',
        key,
        rowId,
        values: { status: 'paid', total: '9.50' },
        original: { status: 'open', total: '12.50' },
      },
    ]);
    const again = folded([...changed], {
      op: 'set',
      key,
      values: { status: 'shipped' },
      original: { status: 'paid' },
    });
    expect(again[0]).toMatchObject({ values: { status: 'shipped' }, original: { status: 'open' } });
    const back = folded([...again], { op: 'set', key, values: { status: 'open' }, original: {} });
    expect(back[0]).toMatchObject({ values: { total: '9.50' }, original: { total: '12.50' } });
    // Nothing changed is no change.
    expect(folded([...back], { op: 'set', key, values: { total: '12.50' } })).toEqual([]);
  });

  it("deletes a row, keeping its first originals; a deleted row's values aren't set", () => {
    const changed = folded([], {
      op: 'set',
      key,
      values: { status: 'paid' },
      original: { status: 'open' },
    });
    const deleted = folded([...changed], {
      op: 'delete',
      key,
      original: { status: 'paid', id: '1001' },
    });
    expect(deleted).toMatchObject([
      { kind: 'delete', values: {}, original: { status: 'open', id: '1001' } },
    ]);
    expect(folded([...deleted], { op: 'set', key, values: { status: 'x' } })).toEqual(deleted);
    expect(folded([...deleted], { op: 'revert', key, columns: ['status'] })).toEqual(deleted);
    expect(folded([...deleted], { op: 'revert', key })).toEqual([]);
  });

  it('adds new rows, sets their values, and drops them', () => {
    const added = folded([], { op: 'insert', tempId: 't1', values: { status: 'new' } }, null);
    expect(added).toMatchObject([{ kind: 'insert', tempId: 't1', values: { status: 'new' } }]);
    expect(folded([...added], { op: 'insert', tempId: 't1' }, null)).toEqual(added);
    const set = folded(
      [...added],
      { op: 'set', tempId: 't1', values: { total: '1.00' }, display: { customer: 'Acme' } },
      null,
    );
    expect(set[0]).toMatchObject({
      values: { status: 'new', total: '1.00' },
      display: { customer: 'Acme' },
    });
    const reverted = folded([...set], { op: 'revert', tempId: 't1', columns: ['STATUS'] }, null);
    // A new row stays when its values go.
    expect(reverted[0]).toMatchObject({ kind: 'insert', values: { total: '1.00' } });
    expect(
      folded([...reverted], { op: 'revert', tempId: 't1', columns: ['total'] }, null)[0],
    ).toMatchObject({ kind: 'insert', values: {} });
    expect(folded([...reverted], { op: 'delete', tempId: 't1' }, null)).toEqual([]);
    expect(folded([], { op: 'set', tempId: 'gone', values: { total: '1' } }, null)).toEqual([]);
  });

  it("reverts a row's columns, and the row when none is left", () => {
    const changed = folded([], {
      op: 'set',
      key,
      values: { status: 'paid', total: '9.50' },
      original: { status: 'open', total: '12.50' },
    });
    const one = folded([...changed], { op: 'revert', key, columns: ['total', 'missing'] });
    expect(one[0]).toMatchObject({ values: { status: 'paid' }, original: { status: 'open' } });
    expect(folded([...one], { op: 'revert', key, columns: ['status'] })).toEqual([]);
    expect(folded([], { op: 'revert', key })).toEqual([]);
  });

  it('tells rows of other entities, and other rows, apart', () => {
    const other = folded([], { op: 'set', key, values: { a: 1 }, original: { a: 0 } });
    const changes = foldOp(
      [...other],
      { op: 'set', entity: 'shop.customers', key, values: { a: 2 }, original: { a: 0 } },
      rowId,
      newId,
    );
    expect(changes.map((change) => change.entity)).toEqual(['shop.orders', 'shop.customers']);
    // A new change has its entity's source, as far as the changes say it.
    const another = folded(
      [changeOf()],
      { op: 'delete', key: ['1002'], original: { id: '1002' } },
      '["1002"]',
    );
    expect(another.map((change) => [change.rowId, change.source])).toEqual([
      [rowId, 'shop'],
      ['["1002"]', 'shop'],
    ]);
  });
});
