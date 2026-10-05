import type { HttpTestingController } from '@angular/common/http/testing';
import type { Schema } from '../app/core/api/api-client';
import type {
  ChangePreview,
  ChangeSet,
  CommitResult,
  PendingChange,
} from '../app/core/changes/pending-changes';
import { requestTo } from './http';

/** Where the pending changes are read, and changed. */
export const changesUrl = '/api/changes';
export const opsUrl = '/api/changes/ops';

/** A change to shop.orders (of the shop connection): row 1001's status set to paid, unless said otherwise. */
export function changeOf(changes: Partial<PendingChange> = {}): PendingChange {
  return {
    id: 1,
    kind: 'update',
    entity: 'shop.orders',
    source: 'shop',
    key: ['1001'],
    rowId: '["1001"]',
    tempId: null,
    values: { status: 'paid' },
    original: { status: 'open' },
    display: {},
    updatedAt: '2026-10-05T08:00:00Z',
    ...changes,
  };
}

/** A new row of shop.orders, by its temporary id. */
export function insertOf(tempId: string, changes: Partial<PendingChange> = {}): PendingChange {
  return changeOf({
    kind: 'insert',
    key: null,
    rowId: null,
    tempId,
    values: {},
    original: {},
    ...changes,
  });
}

/** The user's changes, at a version. */
export function setOf(changes: PendingChange[] = [], version = 1): ChangeSet {
  return { version, updatedAt: changes.length > 0 ? '2026-10-05T08:00:00Z' : null, changes };
}

/** Answers the request for the user's changes. */
export async function answerChanges(
  http: HttpTestingController,
  set: ChangeSet = setOf(),
): Promise<void> {
  (await requestTo(http, changesUrl)).flush(set);
}

/** The other tabs, as the changes hear of them: what this one told them, and what they say. */
export class FakeChangesChannel {
  readonly announced: number[] = [];
  /** The versions announced after commits that wrote changes. */
  readonly committed: number[] = [];
  private readonly listeners: ((version: number, committed: boolean) => void)[] = [];

  announce(version: number, committed = false): void {
    this.announced.push(version);
    if (committed) {
      this.committed.push(version);
    }
  }

  listen(heard: (version: number, committed: boolean) => void): void {
    this.listeners.push(heard);
  }

  /** Another tab says the changes are at a version now (after a commit that wrote some). */
  hear(version: number, committed = false): void {
    for (const listener of this.listeners) {
      listener(version, committed);
    }
  }
}

export const previewUrl = '/api/changes/preview';
export const commitUrl = '/api/changes/commit';

export type PreviewScript = Schema<'PreviewScriptDto'>;

/** A script of a preview: shop's, updating order 1001, unless said otherwise. */
export function scriptOf(changes: Partial<PreviewScript> = {}): PreviewScript {
  return {
    source: 'shop',
    kind: 'sqlite',
    dialect: 'SQLite',
    text: "UPDATE orders SET status = 'paid' WHERE id = 1001 AND status = 'open';\n",
    editable: true,
    statements: [
      {
        change: 1,
        kind: 'update',
        description: 'the update of shop.orders (id = 1001)',
        text: "UPDATE orders SET status = 'paid' WHERE id = 1001 AND status = 'open'",
      },
    ],
    ...changes,
  };
}

/** A preview of the changes that may be committed: shop's script, unless said otherwise. */
export function previewOf(changes: Partial<ChangePreview> = {}): ChangePreview {
  return {
    planId: 'plan-1',
    version: 1,
    catalogVersion: 'catalog-1',
    expiresAt: '2026-10-05T09:30:00Z',
    multiConnection: false,
    scripts: [scriptOf()],
    issues: [],
    ...changes,
  };
}

/** What a commit came to: written to shop, the changes left (none), unless said otherwise. */
export function resultOf(changes: Partial<CommitResult> = {}): CommitResult {
  return {
    auditId: 12,
    outcome: 'committed',
    scripts: [
      {
        source: 'shop',
        edited: false,
        status: 'committed',
        error: null,
        statements: [
          { change: 1, description: 'the update of shop.orders (id = 1001)', rowsChanged: 1 },
        ],
      },
    ],
    failure: null,
    inserted: [],
    changes: setOf([], 2),
    warnings: [],
    ...changes,
  };
}
