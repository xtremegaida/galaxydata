import { DatePipe } from '@angular/common';
import { Component, computed, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { ApiClient, type Schema } from '../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { Message } from '../../../core/ui/message';

type SchemaChange = Schema<'SchemaChange'>;

const objects: Readonly<Record<Schema<'SchemaObject'>, string>> = {
  source: 'source',
  table: 'table',
  column: 'column',
  primaryKey: 'primary key',
  uniqueKey: 'unique key',
  index: 'index',
  foreignKey: 'foreign key',
};

/** A change between two readings of a schema, in words: "Added column shop.orders.total". */
export function describeChange(change: SchemaChange): string {
  const verb = { added: 'Added', removed: 'Removed', changed: 'Changed' }[change.change];
  const path = [change.schema, change.table, change.name].filter(Boolean).join('.');
  return path ? `${verb} ${objects[change.object]} ${path}` : `${verb} ${objects[change.object]}`;
}

/** A property's change: "type: int32 → int64"; for what was added or removed, its value alone. */
export function describeProperty(property: Schema<'PropertyChange'>): string {
  if (property.from === null) {
    return `${property.property}: ${property.to ?? ''}`;
  }
  if (property.to === null) {
    return `${property.property} was ${property.from}`;
  }
  return `${property.property}: ${property.from} → ${property.to}`;
}

/**
 * The schema's last readings (the newest five), with what changed at each; a reading's changes are shown on
 * asking.
 */
@Component({
  selector: 'gd-connection-snapshots',
  imports: [DatePipe, MatButton, MatIcon, MatProgressBar, Message],
  template: `
    <div role="alert">
      @if (problem(); as problem) {
        <gd-message kind="problem">{{ message(problem) }}</gd-message>
      }
    </div>
    @if (snapshots.hasValue()) {
      @if (snapshots.value().length === 0) {
        <p class="none">It hasn't been read yet.</p>
      }
      <ol class="readings">
        @for (snapshot of snapshots.value(); track snapshot.id) {
          <li class="reading">
            <div class="line">
              <span class="when">{{ snapshot.takenAt | date: 'medium' }}</span>
              <span
                >{{ snapshot.tableCount }}
                {{ snapshot.tableCount === 1 ? 'table' : 'tables' }}</span
              >
              @if (snapshot.changes; as changes) {
                <span class="counts">
                  {{ changes.added }} added, {{ changes.removed }} removed,
                  {{ changes.changed }} changed
                </span>
                @if (changes.added + changes.removed + changes.changed > 0) {
                  <button
                    matButton
                    type="button"
                    [attr.aria-expanded]="open() === snapshot.id"
                    (click)="open.set(open() === snapshot.id ? null : snapshot.id)"
                  >
                    {{ open() === snapshot.id ? 'Hide the changes' : 'Show the changes' }}
                  </button>
                }
              } @else {
                <span class="counts">The first reading</span>
              }
              @if (snapshot.checkedAt !== snapshot.takenAt) {
                <span class="checked">unchanged at {{ snapshot.checkedAt | date: 'medium' }}</span>
              }
            </div>
            @if (open() === snapshot.id) {
              @if (detail.isLoading()) {
                <mat-progress-bar mode="indeterminate" aria-label="Loading the changes" />
              }
              @if (changes(); as changes) {
                <ul class="changes">
                  @for (change of changes; track $index) {
                    <li>
                      <mat-icon class="mark">{{ marks[change.change] }}</mat-icon>
                      {{ describe(change) }}
                      @for (property of change.properties ?? []; track property.property) {
                        <span class="property">{{ describeProperty(property) }}</span>
                      }
                    </li>
                  }
                </ul>
              }
            }
          </li>
        }
      </ol>
    }
  `,
  styles: `
    .readings,
    .changes {
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .reading {
      padding: 8px 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }

    .line {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px 16px;
    }

    .when {
      font: var(--mat-sys-title-small);
    }

    .counts,
    .checked,
    .none {
      color: var(--mat-sys-on-surface-variant);
    }

    .changes li {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px 8px;
      padding: 4px 0 4px 8px;
    }

    .mark {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }

    .property {
      font-family: var(--gd-code-font-family);
      font-size: 0.9em;
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class ConnectionSnapshots {
  private readonly api = inject(ApiClient);

  readonly connectionId = input.required<number>();
  /** When the schema was last read: a new reading lists again. */
  readonly refreshedAt = input<string | null>(null);

  protected readonly open = signal<number | null>(null);
  protected readonly describe = describeChange;
  protected readonly describeProperty = describeProperty;
  protected readonly message = problemMessage;
  protected readonly marks = { added: 'add', removed: 'remove', changed: 'edit' };

  protected readonly snapshots = rxResource({
    params: () => ({ id: this.connectionId(), at: this.refreshedAt() }),
    stream: ({ params }) =>
      this.api.get('/api/connections/{id}/snapshots', { path: { id: params.id } }),
  });

  protected readonly detail = rxResource({
    params: () => {
      const snapshotId = this.open();
      return snapshotId === null ? undefined : { id: this.connectionId(), snapshotId };
    },
    stream: ({ params }) =>
      this.api.get('/api/connections/{id}/snapshots/{snapshotId}', { path: params }),
  });

  protected readonly changes = computed(() =>
    this.detail.hasValue() ? (this.detail.value().changes ?? []) : null,
  );

  protected readonly problem = computed(() => {
    const error = this.snapshots.error() ?? this.detail.error();
    return error ? problemOf(error) : null;
  });
}
