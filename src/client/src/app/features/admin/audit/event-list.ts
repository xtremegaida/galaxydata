import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatProgressBar } from '@angular/material/progress-bar';
import { ApiClient } from '../../../core/api/api-client';
import { problemMessage } from '../../../core/api/problem';
import { Message } from '../../../core/ui/message';
import { type AdminEvent, AuditNav, auditPagesOf, detailLines } from './audit';

/** What administrators (and the application, as it started) did, newest first: who, what, to what, and how. */
@Component({
  selector: 'gd-event-list',
  imports: [AuditNav, DatePipe, MatButton, MatProgressBar, Message],
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
            <table class="audit" aria-label="What administrators did">
              <thead>
                <tr>
                  <th scope="col">When</th>
                  <th scope="col">Who</th>
                  <th scope="col">What</th>
                  <th scope="col">To what</th>
                  <th scope="col">Details</th>
                </tr>
              </thead>
              <tbody>
                @for (event of pages.rows(); track event.id) {
                  <tr>
                    <td tabindex="-1" [attr.data-entry]="event.id">
                      {{ event.at | date: 'medium' }}
                    </td>
                    <td>{{ event.actor }}</td>
                    <td>
                      <code class="action">{{ event.action }}</code>
                    </td>
                    <td>{{ event.target }}</td>
                    <td>
                      <ul class="details">
                        @for (line of details(event); track $index) {
                          <li>
                            @if (line.name) {
                              <code>{{ line.name }}</code
                              >:
                            }
                            {{ line.value }}
                          </li>
                        }
                      </ul>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
        @if (pages.loading()) {
          <mat-progress-bar mode="indeterminate" aria-label="Loading what administrators did" />
        }
        <div role="status">
          @if (!pages.loading() && !pages.problem() && pages.rows().length === 0) {
            <p class="empty">Administrators haven't done anything yet.</p>
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
export class EventList {
  private readonly api = inject(ApiClient);

  protected readonly pages = auditPagesOf<AdminEvent>((before, take) =>
    this.api.get('/api/audit/admin-events', { query: { before, take } }),
  );
  protected readonly message = problemMessage;
  private readonly lines = new Map<number, ReturnType<typeof detailLines>>();

  protected details(event: AdminEvent): ReturnType<typeof detailLines> {
    let lines = this.lines.get(event.id);
    if (!lines) {
      lines = detailLines(event.details);
      this.lines.set(event.id, lines);
    }
    return lines;
  }
}
