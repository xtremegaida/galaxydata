import type { HttpTestingController } from '@angular/common/http/testing';
import type { ChangeSet, PendingChange } from '../app/core/changes/pending-changes';
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
  private readonly listeners: ((version: number) => void)[] = [];

  announce(version: number): void {
    this.announced.push(version);
  }

  listen(heard: (version: number) => void): void {
    this.listeners.push(heard);
  }

  /** Another tab says the changes are at a version now. */
  hear(version: number): void {
    for (const listener of this.listeners) {
      listener(version);
    }
  }
}
