import { DOCUMENT } from '@angular/common';
import {
  DestroyRef,
  Injectable,
  type Signal,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { type Observable, firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../api/api-client';
import { type Problem, isSessionProblem, problemMessage, problemOf } from '../api/problem';
import { AuthStore } from '../auth/auth-store';

export type PendingChange = Schema<'PendingChangeDto'>;
export type ChangeSet = Schema<'ChangeSetDto'>;
export type ChangeOp = Schema<'ChangeOpDto'>;
/** Why a change can't be made: the change, the column at fault (if one is), and the reason. */
export type ChangeIssue = Schema<'ChangeIssueDto'>;
export type ChangePreview = Schema<'ChangePreviewDto'>;
export type CommitRequest = Schema<'CommitChangesRequest'>;
export type CommitResult = Schema<'CommitResultDto'>;

/** What came of asking the server: its answer, or the problem. */
export type Answered<T> = { readonly answer: T } | { readonly problem: Problem };

/** Values by column (or display values by navigation), as rows' values are sent. */
export type Values = Readonly<Record<string, unknown>>;

/**
 * A row changes are of: one that is there, by its key (its values as rows give them) and its id (as rows have it);
 * or a new one, by its temporary id.
 */
export type ChangeRow =
  { readonly key: readonly unknown[]; readonly rowId: string } | { readonly tempId: string };

/** What came of an action: done; or not, why (the problem), and what was wrong, a sentence each. */
export type Outcome =
  | { readonly done: true }
  | { readonly done: false; readonly problem: Problem; readonly reasons: readonly string[] };

/** The changes of an entity's rows: those there by their ids, and new rows, in the order they were made. */
export interface EntityChanges {
  readonly rows: ReadonlyMap<string, PendingChange>;
  readonly inserts: readonly PendingChange[];
}

/** Which changes to drop at once: all, a source's (a connection's alias), or an entity's. */
export interface ClearScope {
  readonly source?: string;
  readonly entity?: string;
}

/** An action asked for: its request, what it does to the changes meanwhile, and who waits for what came of it. */
interface Action {
  readonly request: () => Observable<ChangeSet>;
  readonly fold: (changes: readonly PendingChange[]) => readonly PendingChange[];
  readonly resolve: (outcome: Outcome) => void;
}

/** What a tab says when it changed the user's changes: their version now, and whether a commit wrote some. */
interface Announcement {
  readonly version: number;
  readonly committed?: boolean;
}

/**
 * Issues found (by a preview, or a commit that failed) at a version of the changes, with when each change was last
 * changed then: an issue goes once its change is changed again. Until the changes at that version are read here,
 * when they were changed isn't known, and the issues aren't shown.
 */
interface FoundIssues {
  readonly issues: readonly ChangeIssue[];
  readonly version: number;
  readonly changedAt: ReadonlyMap<number, string> | null;
}

/** Tells the application's other tabs when the user's changes changed here, and hears when they changed in theirs. */
@Injectable({ providedIn: 'root' })
export class ChangesChannel {
  private readonly channel =
    typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel('gd.changes');

  constructor() {
    inject(DestroyRef).onDestroy(() => this.channel?.close());
  }

  /** Tells the other tabs the changes' version now, and whether a commit wrote some. */
  announce(version: number, committed = false): void {
    this.channel?.postMessage({ version, committed } satisfies Announcement);
  }

  /** Hears the version another tab says the changes are at now, and whether a commit there wrote some. */
  listen(heard: (version: number, committed: boolean) => void): void {
    this.channel?.addEventListener('message', (event: MessageEvent<Announcement | null>) => {
      if (typeof event.data?.version === 'number') {
        heard(event.data.version, event.data.committed === true);
      }
    });
  }
}

const noChanges: EntityChanges = { rows: new Map(), inserts: [] };

/**
 * The signed-in user's pending changes, for those who change data: kept on the server until committed or reverted.
 * Actions (setting values, new rows, deleting, reverting, clearing) are shown at once, as the server will make them,
 * and sent one at a time, in order; the server's answer is the changes as they are. An action the server refuses
 * goes, and what came of it says why. Another tab's changes (it says so), and coming back to the tab, read them
 * again, once the actions sent are answered. Previews and commits wait for the actions asked for before them; what
 * a preview found wrong with changes is kept until they change, and commits that wrote changes are counted (here
 * and in other tabs), so the rows shown are read again.
 */
@Injectable({ providedIn: 'root' })
export class PendingChanges {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthStore);
  private readonly channel = inject(ChangesChannel);

  /** The changes as the server last said them. */
  private readonly confirmed = signal<ChangeSet | null>(null);
  /** Actions not answered yet: the first is being sent. */
  private readonly queue = signal<readonly Action[]>([]);
  private readonly loadProblem = signal<Problem | null>(null);
  private sending = false;
  /** Waiting for the actions asked for to be answered (previews and commits). */
  private settling: (() => void)[] = [];
  /** Whether the changes are to be read again once the actions are answered. */
  private reloadWanted = false;
  /** Requests whose answers are the changes, numbered: an answer to an earlier one than the last taken is left. */
  private asked = 0;
  private taken = 0;
  private optimisticIds = 0;
  /** What the last preview found wrong. */
  private readonly found = signal<FoundIssues | null>(null);
  /** The change whose statement stopped the last commit, and why. */
  private readonly failed = signal<FoundIssues | null>(null);
  private readonly written = signal(0);

  /** Whether the user may change data, and so has changes. */
  readonly enabled = computed(() => this.auth.permissions().canEditData);
  /** The changes, as they will be once the actions asked for are made. */
  readonly changes: Signal<readonly PendingChange[]> = computed(() =>
    this.queue().reduce(
      (changes, action) => action.fold(changes),
      this.confirmed()?.changes ?? ([] as readonly PendingChange[]),
    ),
  );
  readonly count = computed(() => this.changes().length);
  /** Whether the changes have been read. */
  readonly loaded = computed(() => this.confirmed() !== null);
  /** Why the changes couldn't be read. */
  readonly problem = this.loadProblem.asReadonly();
  /** Whether actions are waiting for the server. */
  readonly saving = computed(() => this.queue().length > 0);
  /** The changes by entity. */
  readonly byEntity = computed(() => indexOf(this.changes()));
  /**
   * What the last preview found wrong with changes, and the change that stopped the last commit, by change: of those
   * not changed since (the same while they are, so other changes don't make them anew).
   */
  readonly issues = computed(
    () => {
      const issues = new Map<number, ChangeIssue[]>();
      const changes = new Map(this.changes().map((change) => [change.id, change]));
      for (const found of [this.failed(), this.found()]) {
        for (const issue of found?.issues ?? []) {
          const change = changes.get(issue.change);
          if (change && change.updatedAt === found?.changedAt?.get(issue.change)) {
            issues.set(issue.change, [...(issues.get(issue.change) ?? []), issue]);
          }
        }
      }
      return issues as ReadonlyMap<number, readonly ChangeIssue[]>;
    },
    { equal: sameIssues },
  );
  /** Counts the commits that wrote changes (or may have), here or in another tab: the rows shown are read again. */
  readonly commits = this.written.asReadonly();

  constructor() {
    effect(() => {
      const enabled = this.enabled();
      untracked(() => (enabled ? this.load() : this.reset()));
    });
    this.channel.listen((version, committed) => {
      if (committed) {
        this.written.update((count) => count + 1);
      }
      if (version !== untracked(this.confirmed)?.version) {
        this.reload();
      }
    });
    const document = inject(DOCUMENT);
    const visible = () => {
      if (document.visibilityState === 'visible') {
        this.reload();
      }
    };
    document.addEventListener('visibilitychange', visible);
    inject(DestroyRef).onDestroy(() => document.removeEventListener('visibilitychange', visible));
  }

  /** The changes of an entity's rows. */
  of(entity: string): EntityChanges {
    return this.byEntity().get(entity) ?? noChanges;
  }

  /** What the last preview found wrong with a change (not changed since). */
  issuesOf(change: PendingChange | null): readonly ChangeIssue[] {
    return (change && this.issues().get(change.id)) ?? [];
  }

  /**
   * What committing the changes would run on each connection, and what is wrong with changes (kept, for the rows
   * to show): once the actions asked for are answered.
   */
  async preview(): Promise<Answered<ChangePreview>> {
    await this.settled();
    try {
      const preview = await firstValueFrom(this.api.post('/api/changes/preview'));
      const confirmed = untracked(this.confirmed);
      const same = preview.version === confirmed?.version;
      this.found.set({
        issues: preview.issues,
        version: preview.version,
        changedAt: same ? changedAtOf(confirmed) : null,
      });
      if (!same) {
        // Changed elsewhere since they were read here.
        this.reload();
      }
      return { answer: preview };
    } catch (error: unknown) {
      return { problem: problemOf(error) };
    }
  }

  /**
   * Commits a preview's plan (once the actions asked for are answered; those asked for meanwhile, and reading the
   * changes again, wait for it): the changes left are the server's; the change whose statement failed is marked,
   * with why; a commit that wrote changes is counted, and told the other tabs. A commit that may have run (no
   * answer, a failure) reads the changes again, and is counted.
   */
  async commit(request: CommitRequest): Promise<Answered<CommitResult>> {
    while (this.sending || untracked(this.queue).length > 0) {
      await this.settled();
    }
    this.sending = true;
    const number = ++this.asked;
    try {
      const result = await firstValueFrom(this.api.post('/api/changes/commit', { body: request }));
      const wrote = result.outcome !== 'rolledBack';
      if (this.take(number, result.changes)) {
        this.channel.announce(result.changes.version, wrote);
      }
      const failure = result.failure;
      this.found.set(null);
      this.failed.set(
        failure?.change != null
          ? {
              issues: [{ change: failure.change, column: null, message: failure.message }],
              version: result.changes.version,
              changedAt: changedAtOf(result.changes),
            }
          : null,
      );
      if (wrote) {
        this.written.update((count) => count + 1);
      }
      return { answer: result };
    } catch (error: unknown) {
      const problem = problemOf(error);
      if (![400, 403, 409, 422].includes(problem.status) && !isSessionProblem(problem)) {
        this.written.update((count) => count + 1);
        this.reloadWanted = true;
      }
      return { problem };
    } finally {
      this.sending = false;
      this.finished();
    }
  }

  /** Reads the changes again (once the actions sent are answered). */
  reload(): void {
    if (!untracked(this.enabled)) {
      return;
    }
    if (this.sending) {
      this.reloadWanted = true;
      return;
    }
    this.load();
  }

  /**
   * New values for columns of a row (and display values for the rows navigations then lead to). The values a row
   * that is there had (`original`) are needed when a column is first changed, to check it still has them when the
   * change is committed.
   */
  set(
    entity: string,
    row: ChangeRow,
    values: Values,
    original: Values = {},
    display: Values = {},
  ): Promise<Outcome> {
    const op: ChangeOp = { op: 'set', entity, ...rowOf(row), values, display };
    if ('key' in row) {
      op.original = original;
    }
    return this.act(
      () => this.ops([op]),
      (changes) => this.folded(changes, op, row),
    );
  }

  /** A new row, with values given; its temporary id names it until it is committed. */
  insert(
    entity: string,
    values: Values = {},
    display: Values = {},
  ): { readonly tempId: string; readonly done: Promise<Outcome> } {
    const tempId = newTempId();
    const op: ChangeOp = { op: 'insert', entity, tempId, values, display };
    const row: ChangeRow = { tempId };
    return {
      tempId,
      done: this.act(
        () => this.ops([op]),
        (changes) => this.folded(changes, op, row),
      ),
    };
  }

  /** Deletes a row that is there (with the values it had, to check it still has them), or drops a new one. */
  delete(entity: string, row: ChangeRow, original: Values = {}): Promise<Outcome> {
    const op: ChangeOp = { op: 'delete', entity, ...rowOf(row) };
    if ('key' in row) {
      op.original = original;
    }
    return this.act(
      () => this.ops([op]),
      (changes) => this.folded(changes, op, row),
    );
  }

  /** Drops a row's change (a new row too), or only its values for some columns. */
  revert(entity: string, row: ChangeRow, columns?: readonly string[]): Promise<Outcome> {
    const op: ChangeOp = { op: 'revert', entity, ...rowOf(row) };
    if (columns) {
      op.columns = [...columns];
    }
    return this.act(
      () => this.ops([op]),
      (changes) => this.folded(changes, op, row),
    );
  }

  /** Drops the changes, or a source's or an entity's. */
  clear(scope: ClearScope = {}): Promise<Outcome> {
    return this.act(
      () => this.api.delete('/api/changes', { query: { ...scope } }),
      (changes) =>
        changes.filter(
          (change) =>
            !(
              (scope.source === undefined ||
                change.source.toLowerCase() === scope.source.toLowerCase()) &&
              (scope.entity === undefined || change.entity === scope.entity)
            ),
        ),
    );
  }

  private ops(ops: ChangeOp[]): Observable<ChangeSet> {
    return this.api.post('/api/changes/ops', { body: { ops } });
  }

  /** Once the actions asked for are answered. */
  private settled(): Promise<void> {
    if (!this.sending && untracked(this.queue).length === 0) {
      return Promise.resolve();
    }
    return new Promise((resolve) => this.settling.push(resolve));
  }

  private act(request: () => Observable<ChangeSet>, fold: Action['fold']): Promise<Outcome> {
    return new Promise((resolve) => {
      this.queue.update((queue) => [...queue, { request, fold, resolve }]);
      this.next();
    });
  }

  /**
   * An action or a commit answered: the next action is sent; else the changes are read again when that is wanted,
   * and what waits for the actions to be answered goes on.
   */
  private finished(): void {
    if (untracked(this.queue).length > 0) {
      this.next();
      return;
    }
    if (this.reloadWanted) {
      this.reloadWanted = false;
      this.load();
    }
    const settling = this.settling;
    this.settling = [];
    settling.forEach((settled) => settled());
  }

  /** Sends the first action waiting, unless one is being sent (or a commit). */
  private next(): void {
    const action = untracked(this.queue)[0];
    if (this.sending || !action) {
      return;
    }
    this.sending = true;
    const number = ++this.asked;
    const done = (outcome: Outcome) => {
      this.queue.update((queue) => queue.filter((each) => each !== action));
      this.sending = false;
      action.resolve(outcome);
      this.finished();
    };
    action.request().subscribe({
      next: (set) => {
        if (this.take(number, set)) {
          this.channel.announce(set.version);
        }
        done({ done: true });
      },
      error: (error: unknown) => {
        const problem = problemOf(error);
        // Refused (400), it wasn't made. Otherwise (no answer, a failure, a timeout of a proxy's) it may have
        // been: the changes are read again.
        if (problem.status !== 400 && !isSessionProblem(problem)) {
          this.reloadWanted = true;
        }
        done({ done: false, problem, reasons: reasonsOf(problem) });
      },
    });
  }

  private load(): void {
    const number = ++this.asked;
    this.api.get('/api/changes').subscribe({
      next: (set) => {
        this.take(number, set);
        this.loadProblem.set(null);
      },
      error: (error: unknown) => {
        const problem = problemOf(error);
        if (number > this.taken && !isSessionProblem(problem)) {
          this.loadProblem.set(problem);
        }
      },
    });
  }

  /** Takes the changes an answer says, unless a later request's answer was taken. */
  private take(number: number, set: ChangeSet): boolean {
    if (number < this.taken) {
      return false;
    }
    this.taken = number;
    this.confirmed.set(set);
    this.loadProblem.set(null);
    // Issues found at this version are of these changes as they are.
    for (const found of [this.found, this.failed]) {
      const issues = untracked(found);
      if (issues && issues.changedAt === null && issues.version === set.version) {
        found.set({ ...issues, changedAt: changedAtOf(set) });
      }
    }
    return true;
  }

  private reset(): void {
    this.confirmed.set(null);
    this.loadProblem.set(null);
    this.found.set(null);
    this.failed.set(null);
  }

  /** The changes as an operation leaves them (as the server merges them), for showing it before it is answered. */
  private folded(
    changes: readonly PendingChange[],
    op: ChangeOp,
    row: ChangeRow,
  ): readonly PendingChange[] {
    return foldOp(changes, op, 'rowId' in row ? row.rowId : null, () => --this.optimisticIds);
  }
}

/**
 * The changes as the server would leave them after an operation, as far as the client can tell: a row has one
 * change; a column's original is the one first given; a value set back to its original is no change; deleting a
 * changed row keeps its originals; deleting a new row drops it; a change to a row with no values left goes. What the
 * server would refuse leaves them as they are (the server says why). Values are compared as sent.
 */
export function foldOp(
  changes: readonly PendingChange[],
  op: ChangeOp,
  rowId: string | null,
  newId: () => number,
): readonly PendingChange[] {
  const entity = op.entity ?? '';
  const tempId = op.tempId ?? null;
  const index = changes.findIndex(
    (change) =>
      change.entity === entity &&
      (tempId !== null ? change.tempId === tempId : rowId !== null && change.rowId === rowId),
  );
  const found = index >= 0 ? changes[index] : null;
  const replaced = (change: PendingChange | null) =>
    change === null
      ? changes.filter((_, at) => at !== index)
      : index >= 0
        ? changes.map((each, at) => (at === index ? change : each))
        : [...changes, change];
  const made = (kind: PendingChange['kind']): PendingChange => ({
    id: newId(),
    kind,
    entity,
    source: changes.find((change) => change.entity === entity)?.source ?? '',
    key: op.key ? [...op.key] : null,
    rowId,
    tempId,
    values: {},
    original: {},
    display: {},
    updatedAt: '',
  });
  switch (op.op) {
    case 'insert':
      return found
        ? changes
        : replaced({ ...made('insert'), values: { ...op.values }, display: { ...op.display } });
    case 'set': {
      if (tempId !== null) {
        return found
          ? replaced({
              ...found,
              values: { ...found.values, ...op.values },
              display: { ...found.display, ...op.display },
            })
          : changes;
      }
      if (found?.kind === 'delete') {
        return changes;
      }
      const change = found ?? made('update');
      const values = { ...change.values };
      const original = { ...change.original };
      for (const [column, value] of Object.entries(op.values ?? {})) {
        const was = column in original ? original[column] : op.original?.[column];
        if (sameValue(value, was)) {
          delete values[column];
          delete original[column];
        } else {
          values[column] = value;
          original[column] = was;
        }
      }
      const display = { ...change.display, ...op.display };
      return replaced(
        Object.keys(values).length === 0 ? null : { ...change, values, original, display },
      );
    }
    case 'delete': {
      if (tempId !== null) {
        return found ? replaced(null) : changes;
      }
      const change = found ?? made('delete');
      return replaced({
        ...change,
        kind: 'delete',
        values: {},
        display: {},
        original: { ...op.original, ...change.original },
      });
    }
    case 'revert': {
      if (!found) {
        return changes;
      }
      if (!op.columns) {
        return replaced(null);
      }
      if (found.kind === 'delete') {
        return changes;
      }
      const values = { ...found.values };
      const original = { ...found.original };
      for (const column of op.columns) {
        const kept = keptName(values, column);
        if (kept !== null) {
          delete values[kept];
          delete original[kept];
        }
      }
      return replaced(
        found.kind === 'update' && Object.keys(values).length === 0
          ? null
          : { ...found, values, original },
      );
    }
    default:
      return changes;
  }
}

/** When each change was last changed, by its id. */
function changedAtOf(set: ChangeSet | null): ReadonlyMap<number, string> {
  return new Map((set?.changes ?? []).map((change) => [change.id, change.updatedAt]));
}

/** Whether two indexes of issues say the same of the same changes. */
function sameIssues(
  a: ReadonlyMap<number, readonly ChangeIssue[]>,
  b: ReadonlyMap<number, readonly ChangeIssue[]>,
): boolean {
  return a.size === b.size && [...a].every(([id, issues]) => sameValue(issues, b.get(id)));
}

/** Whether two values are the same as sent (JSON's). */
export function sameValue(a: unknown, b: unknown): boolean {
  return JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
}

/** A column a change has a value for: named so, or so but for case when that is one. */
function keptName(values: Values, name: string): string | null {
  if (name in values) {
    return name;
  }
  const found = Object.keys(values).filter((key) => key.toLowerCase() === name.toLowerCase());
  return found.length === 1 ? found[0] : null;
}

function rowOf(row: ChangeRow): Pick<ChangeOp, 'key' | 'tempId'> {
  return 'tempId' in row ? { tempId: row.tempId } : { key: [...row.key] };
}

function indexOf(changes: readonly PendingChange[]): ReadonlyMap<string, EntityChanges> {
  const index = new Map<string, { rows: Map<string, PendingChange>; inserts: PendingChange[] }>();
  for (const change of changes) {
    let entity = index.get(change.entity);
    if (!entity) {
      entity = { rows: new Map(), inserts: [] };
      index.set(change.entity, entity);
    }
    if (change.kind === 'insert') {
      entity.inserts.push(change);
    } else if (change.rowId !== null) {
      entity.rows.set(change.rowId, change);
    }
  }
  return index;
}

/**
 * What was wrong, a sentence each: what the server said of each field (the column's name before what it said of a
 * value), or the problem's message.
 */
function reasonsOf(problem: Problem): string[] {
  const errors = problem.errors;
  if (!errors || Object.keys(errors).length === 0) {
    return [problemMessage(problem)];
  }
  return Object.entries(errors).flatMap(([field, messages]) => {
    const column = /^ops\[\d+\]\.(?:values|original)\.(.+)$/.exec(field)?.[1];
    return messages.map((message) =>
      column && !message.includes(`'${column}'`) ? `${column}: ${message}` : message,
    );
  });
}

let tempIds = 0;

/** A temporary id for a new row, unique among the user's (other tabs' too). */
function newTempId(): string {
  const random =
    typeof crypto !== 'undefined' && 'randomUUID' in crypto
      ? crypto.randomUUID().slice(0, 8)
      : Math.random().toString(36).slice(2, 10);
  return `new-${random}-${++tempIds}`;
}
