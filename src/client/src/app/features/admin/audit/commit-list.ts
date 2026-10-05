import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiClient } from '../../../core/api/api-client';
import { problemMessage } from '../../../core/api/problem';
import { Message } from '../../../core/ui/message';
import { AuditNav, type CommitSummary, auditPagesOf, commitStatusLabels, warns } from './audit';

/** The commits of changes, newest first: who, when, what came of them, and where they wrote. */
@Component({
  selector: 'gd-commit-list',
  imports: [AuditNav, DatePipe, MatButton, MatProgressBar, Message, RouterLink],
  template: `
    <div class="admin-page">
      <header class="page-header">
        <h1>Audit</h1>
        <p class="subtitle">What was done to the data and to the application, newest first.</p>
      </header>
      <gd-audit-nav>
        <div role="alert">
          @if (pages.problem(); as problem) {
            <gd-message kind="problem">
              {{ message(problem) }}
              <button gdMessageAction matButton type="button" (click)="pages.load()">
                Try again
              </button>
            </gd-message>
          }
        </div>
        @if (pages.rows().length > 0) {
          <div class="table-wrap">
            <table class="audit" aria-label="Commits of changes">
              <thead>
                <tr>
                  <th scope="col">Commit</th>
                  <th scope="col">Started</th>
                  <th scope="col">By</th>
                  <th scope="col">Outcome</th>
                  <th scope="col">Changes</th>
                  <th scope="col">Connections</th>
                  <th scope="col">Why it failed</th>
                </tr>
              </thead>
              <tbody>
                @for (commit of pages.rows(); track commit.id) {
                  <tr>
                    <td>
                      <a [routerLink]="[commit.id]" [attr.data-entry]="commit.id">{{
                        commit.id
                      }}</a>
                    </td>
                    <td>{{ commit.startedAt | date: 'medium' }}</td>
                    <td>{{ commit.user }}</td>
                    <td>
                      <ul class="badges">
                        <li class="badge" [class.warn]="warns(commit.status)">
                          {{ statuses[commit.status] }}
                        </li>
                        @if (commit.edited) {
                          <li class="badge">
                            {{ commit.anyStatement ? 'Edited, any statement' : 'Edited' }}
                          </li>
                        }
                      </ul>
                    </td>
                    <td>{{ commit.changes }}</td>
                    <td>{{ commit.sources.join(', ') }}</td>
                    <td class="failure">{{ commit.failure }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
        @if (pages.loading()) {
          <mat-progress-bar mode="indeterminate" aria-label="Loading the commits" />
        }
        <div role="status">
          @if (!pages.loading() && !pages.problem() && pages.rows().length === 0) {
            <p class="empty">No changes have been committed yet.</p>
          }
        </div>
        @if (pages.more()) {
          <div class="actions">
            <button matButton type="button" [disabled]="pages.loading()" (click)="pages.load()">
              Show more
            </button>
          </div>
        }
      </gd-audit-nav>
    </div>
  `,
  styleUrls: ['../admin-page.scss', './audit.scss'],
})
export class CommitList {
  private readonly api = inject(ApiClient);

  protected readonly pages = auditPagesOf<CommitSummary>((before, take) =>
    this.api.get('/api/audit/commits', { query: { before, take } }),
  );
  protected readonly statuses = commitStatusLabels;
  protected readonly warns = warns;
  protected readonly message = problemMessage;
}
