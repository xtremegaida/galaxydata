import { Component, computed, inject, linkedSignal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import {
  MatCell,
  MatCellDef,
  MatColumnDef,
  MatHeaderCell,
  MatHeaderCellDef,
  MatHeaderRow,
  MatHeaderRowDef,
  MatRow,
  MatRowDef,
  MatTable,
} from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { ApiClient } from '../../../core/api/api-client';
import { pollWhile } from '../../../core/api/poll';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { Message } from '../../../core/ui/message';
import type { Connection } from './connection-draft';
import { SchemaStatus } from './schema-status';

/** Whether a connection's schema is still to be read, or being read. */
export function reading(connection: Connection): boolean {
  return connection.schemaStatus === 'loading' || connection.schemaStatus === 'notLoaded';
}

/** The connections, with whether their schemas are read (followed while they are being read). */
@Component({
  selector: 'gd-connection-list',
  imports: [
    MatAnchor,
    MatButton,
    MatCell,
    MatCellDef,
    MatColumnDef,
    MatHeaderCell,
    MatHeaderCellDef,
    MatHeaderRow,
    MatHeaderRowDef,
    MatIcon,
    MatProgressBar,
    MatRow,
    MatRowDef,
    MatTable,
    Message,
    RouterLink,
    SchemaStatus,
  ],
  template: `
    <div class="admin-page">
      <header class="page-header">
        <h1>Connections</h1>
        <a matButton="filled" routerLink="new">
          <mat-icon>add</mat-icon>
          New connection
        </a>
      </header>

      @if (connections.isLoading() && !connections.hasValue()) {
        <mat-progress-bar mode="indeterminate" aria-label="Loading the connections" />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">
            {{ message(problem) }}
            <button gdMessageAction matButton type="button" (click)="connections.reload()">
              Try again
            </button>
          </gd-message>
        }
      </div>

      @if (listed()) {
        @if (rows().length === 0) {
          <p class="empty">No connections yet: make one to browse and query a database.</p>
        } @else {
          <div class="table-wrap">
            <table mat-table [dataSource]="rows()" aria-label="Connections">
              <ng-container matColumnDef="alias">
                <th mat-header-cell *matHeaderCellDef>Alias</th>
                <td mat-cell *matCellDef="let connection">
                  <a [routerLink]="[connection.id]">{{ connection.alias }}</a>
                </td>
              </ng-container>
              <ng-container matColumnDef="displayName">
                <th mat-header-cell *matHeaderCellDef>Name</th>
                <td mat-cell *matCellDef="let connection">{{ connection.displayName }}</td>
              </ng-container>
              <ng-container matColumnDef="kind">
                <th mat-header-cell *matHeaderCellDef>Kind</th>
                <td mat-cell *matCellDef="let connection">{{ kindOf(connection) }}</td>
              </ng-container>
              <ng-container matColumnDef="access">
                <th mat-header-cell *matHeaderCellDef>Changes</th>
                <td mat-cell *matCellDef="let connection">
                  <ul class="badges">
                    <li class="badge">{{ connection.isReadOnly ? 'Read-only' : 'Written' }}</li>
                    @if (connection.secretsUnreadable) {
                      <li class="badge warn">Secrets to enter again</li>
                    }
                  </ul>
                </td>
              </ng-container>
              <ng-container matColumnDef="schema">
                <th mat-header-cell *matHeaderCellDef>Schema</th>
                <td mat-cell *matCellDef="let connection">
                  <gd-schema-status
                    [status]="connection.schemaStatus"
                    [error]="connection.schemaError"
                    [refreshedAt]="connection.schemaRefreshedAt"
                  />
                </td>
              </ng-container>
              <tr mat-header-row *matHeaderRowDef="columns"></tr>
              <tr mat-row *matRowDef="let row; columns: columns"></tr>
            </table>
          </div>
        }
      }
    </div>
  `,
  styleUrl: '../admin-page.scss',
})
export class ConnectionList {
  private readonly api = inject(ApiClient);

  protected readonly connections = rxResource({ stream: () => this.api.get('/api/connections') });
  protected readonly kinds = rxResource({ stream: () => this.api.get('/api/connection-kinds') });
  protected readonly columns = ['alias', 'displayName', 'kind', 'access', 'schema'];
  protected readonly message = problemMessage;

  /** The connections last read: kept while reading them again fails (as following a schema may). */
  protected readonly rows = linkedSignal<Connection[] | undefined, Connection[]>({
    source: () => (this.connections.hasValue() ? this.connections.value() : undefined),
    computation: (connections, previous) => connections ?? previous?.value ?? [],
  });
  protected readonly listed = computed(() => this.connections.hasValue() || this.rows().length > 0);
  protected readonly problem = computed(() => {
    const error = this.connections.error();
    return error ? problemOf(error) : null;
  });

  constructor() {
    pollWhile(this.connections, (connections) => connections.some(reading));
  }

  protected kindOf(connection: Connection): string {
    const kinds = this.kinds.hasValue() ? this.kinds.value() : [];
    return kinds.find((kind) => kind.id === connection.kind)?.displayName ?? connection.kind;
  }
}
