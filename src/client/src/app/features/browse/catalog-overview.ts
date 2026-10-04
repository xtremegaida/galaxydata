import { Component, computed, inject, linkedSignal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiClient, type Schema } from '../../core/api/api-client';
import { pollWhile } from '../../core/api/poll';
import { isSessionProblem, problemMessage, problemOf } from '../../core/api/problem';
import { AuthStore } from '../../core/auth/auth-store';
import { followCatalog } from '../../core/catalog/catalog-changes';
import { SchemaStatus } from '../../core/catalog/schema-status';
import { Message } from '../../core/ui/message';
import { sourceKindName } from './tree-nodes';

type Catalog = Schema<'CatalogDto'>;
type CatalogSource = Schema<'CatalogSourceDto'>;

/** Whether a source's schema is still to be read, or being read. */
function reading(source: CatalogSource): boolean {
  return source.status === 'loading' || source.status === 'notLoaded';
}

/**
 * The start of browsing: what to do, the sources with how their schemas stand (followed while they are read), and
 * what building the catalog found wrong (the overlay's items that don't work, names that hide others).
 */
@Component({
  selector: 'gd-catalog-overview',
  imports: [MatButton, MatIcon, MatProgressBar, Message, RouterLink, SchemaStatus],
  template: `
    <div class="browse-page">
      <header class="page-header">
        <h1>Browse</h1>
        <p class="subtitle">
          Choose a table, a view or a virtual entity in the catalog to see what it is made of.
        </p>
      </header>

      @if (catalog.isLoading() && !shown()) {
        <mat-progress-bar mode="indeterminate" aria-label="Loading the catalog" />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">
            {{ message(problem) }}
            <button gdMessageAction matButton type="button" (click)="catalog.reload()">
              Try again
            </button>
          </gd-message>
        }
      </div>

      @if (shown(); as catalog) {
        <section class="section" aria-labelledby="gd-sources">
          <h2 id="gd-sources">Connections</h2>
          @if (catalog.sources.length === 0) {
            <p class="empty">
              There are no connections yet.
              @if (canAdmin()) {
                <a routerLink="/admin/connections/new">Make one</a> to browse a database.
              }
            </p>
          } @else {
            <div class="table-wrap">
              <table class="sources">
                <thead>
                  <tr>
                    <th scope="col">Connection</th>
                    <th scope="col">Kind</th>
                    <th scope="col">Schema</th>
                    <th scope="col" class="number">Entities</th>
                    <th scope="col">Changes</th>
                  </tr>
                </thead>
                <tbody>
                  @for (source of catalog.sources; track source.alias) {
                    <tr>
                      <th scope="row">
                        <span class="alias">{{ source.alias }}</span>
                        @if (source.displayName) {
                          <span class="display-name">{{ source.displayName }}</span>
                        }
                      </th>
                      <td>{{ kindName(source.kind) }}</td>
                      <td>
                        <gd-schema-status
                          [status]="source.status"
                          [refreshedAt]="source.refreshedAt"
                        />
                        @if (source.problem) {
                          <div class="source-problem">{{ source.problem }}</div>
                        } @else if (source.status === 'failed' && source.hasSchema) {
                          <div class="source-problem">The schema read before is shown.</div>
                        }
                      </td>
                      <td class="number">{{ source.entities }}</td>
                      <td>{{ source.isReadOnly ? 'Read-only' : 'Written' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>

        @if (catalog.diagnostics.length > 0) {
          <section class="section" aria-labelledby="gd-diagnostics">
            <h2 id="gd-diagnostics">What the catalog found</h2>
            <ul class="diagnostics">
              @for (diagnostic of catalog.diagnostics; track $index) {
                <li [class]="diagnostic.severity">
                  <mat-icon>{{ iconOf(diagnostic.severity) }}</mat-icon>
                  <span class="cdk-visually-hidden">{{ diagnostic.severity }}:</span>
                  <span class="diagnostic-text">
                    {{ diagnostic.message }}
                    <span class="code">{{ diagnostic.code }}</span>
                  </span>
                </li>
              }
            </ul>
          </section>
        }
      }
    </div>
  `,
  styleUrl: './browse-shared.scss',
  styles: `
    .sources {
      border-collapse: collapse;

      th,
      td {
        padding: 8px 16px 8px 0;
        border-bottom: 1px solid var(--mat-sys-outline-variant);
        text-align: start;
        vertical-align: top;
        font: var(--mat-sys-body-medium);
      }

      thead th {
        color: var(--mat-sys-on-surface-variant);
        font: var(--mat-sys-title-small);
      }

      .number {
        text-align: end;
      }
    }

    .alias {
      display: block;
      font-weight: 500;
    }

    .display-name,
    .source-problem {
      display: block;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .diagnostics {
      margin: 0;
      padding: 0;
      list-style: none;

      li {
        display: flex;
        align-items: flex-start;
        gap: 8px;
        padding: 6px 0;
        font: var(--mat-sys-body-medium);
      }

      .error mat-icon {
        color: var(--mat-sys-error);
      }
    }

    .code {
      margin-inline-start: 8px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-label-small);
    }
  `,
})
export class CatalogOverview {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthStore);

  private readonly followed = followCatalog(() => this.catalog.reload());
  protected readonly catalog = rxResource({
    stream: () => this.api.get('/api/catalog').pipe(this.followed()),
  });
  /** The catalog last read: kept while reading it again fails (as following a schema may). */
  protected readonly shown = linkedSignal<Catalog | undefined, Catalog | undefined>({
    source: () => (this.catalog.hasValue() ? this.catalog.value() : undefined),
    computation: (catalog, previous) => catalog ?? previous?.value,
  });
  protected readonly problem = computed(() => {
    const error = this.catalog.error();
    const problem = error ? problemOf(error) : null;
    return problem && !isSessionProblem(problem) ? problem : null;
  });
  protected readonly canAdmin = computed(() => this.auth.permissions().canAdmin);
  protected readonly message = problemMessage;
  protected readonly kindName = sourceKindName;

  constructor() {
    pollWhile(this.catalog, (catalog) => catalog.sources.some(reading));
  }

  protected iconOf(severity: Schema<'DiagnosticSeverity'>): string {
    return severity === 'error' ? 'error' : severity === 'warning' ? 'warning' : 'info';
  }
}
