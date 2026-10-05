import { DatePipe } from '@angular/common';
import { Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiClient } from '../../../core/api/api-client';
import { type Problem, problemMessage, problemOf } from '../../../core/api/problem';
import { Message } from '../../../core/ui/message';
import { commitStatusLabels, scriptStatusLabels, warns } from './audit';

/** A commit of changes, as the audit has it: who, when, what came of it, and each script it ran, as text. */
@Component({
  selector: 'gd-commit-page',
  imports: [DatePipe, MatAnchor, MatButton, MatIcon, MatProgressBar, Message, RouterLink],
  template: `
    <div class="admin-page">
      <a class="back" matButton routerLink="/admin/audit/commits">
        <mat-icon>arrow_back</mat-icon>
        Commits
      </a>
      @if (commit.isLoading()) {
        <mat-progress-bar mode="indeterminate" aria-label="Loading the commit" />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">
            {{ message(problem) }}
            @if (problem.status !== 404) {
              <button gdMessageAction matButton type="button" (click)="commit.reload()">
                Try again
              </button>
            }
          </gd-message>
        }
      </div>
      @if (shown(); as commit) {
        <header class="page-header">
          <h1>Commit {{ commit.id }}</h1>
          <div class="subtitle">
            <ul class="badges" aria-label="Outcome">
              <li class="badge" [class.warn]="warns(commit.status)">
                {{ statuses[commit.status] }}
              </li>
              @if (commit.edited) {
                <li class="badge">Edited</li>
              }
              @if (commit.anyStatement) {
                <li class="badge warn">Any statement allowed</li>
              }
            </ul>
          </div>
        </header>
        <section class="section" aria-labelledby="gd-commit-facts">
          <h2 id="gd-commit-facts">What came of it</h2>
          <dl class="facts">
            <dt>By</dt>
            <dd>{{ commit.user }}</dd>
            <dt>Started</dt>
            <dd>{{ commit.startedAt | date: 'medium' }}</dd>
            <dt>Finished</dt>
            <dd>
              @if (commit.finishedAt) {
                {{ commit.finishedAt | date: 'medium' }} ({{ took() }})
              } @else {
                Not known to have finished
              }
            </dd>
            <dt>Changes</dt>
            <dd>{{ commit.changes }}</dd>
            @if (commit.failure) {
              <dt>Why it failed</dt>
              <dd class="error">
                {{ commit.failure }}
                @if (commit.failureKind) {
                  ({{ commit.failureKind }})
                }
              </dd>
            }
            <dt>Catalog version</dt>
            <dd>
              <code>{{ commit.catalogVersion }}</code>
            </dd>
          </dl>
          @if (commit.status === 'unknown') {
            <gd-message kind="warning">
              The application stopped as the commit ran, or what came of it couldn't be written:
              whether the changes were written must be checked in the databases.
            </gd-message>
          }
        </section>
        <section class="section" aria-labelledby="gd-commit-scripts">
          <h2 id="gd-commit-scripts">Scripts</h2>
          @for (script of commit.scripts; track $index) {
            <section class="script" [attr.aria-labelledby]="'gd-commit-script-' + $index">
              <h3 [id]="'gd-commit-script-' + $index">
                {{ script.source }} · {{ script.dialect }}
              </h3>
              <dl class="facts">
                <dt>Outcome</dt>
                <dd [class.error]="warns(script.status)">{{ scripts[script.status] }}</dd>
                <dt>Statements</dt>
                <dd>{{ script.statements }}{{ script.edited ? ', as edited' : '' }}</dd>
                <dt>Rows changed</dt>
                <dd>{{ script.rowsChanged ?? 'Not counted' }}</dd>
                @if (script.error) {
                  <dt>Error</dt>
                  <dd class="error">{{ script.error }}</dd>
                }
              </dl>
              <pre
                class="text"
                tabindex="0"
                [attr.aria-label]="'The script run on ' + script.source"
                >{{ script.text }}</pre>
            </section>
          }
        </section>
      }
    </div>
  `,
  styleUrls: ['../admin-page.scss', './audit.scss'],
})
export class CommitPage {
  private readonly api = inject(ApiClient);

  /** The commit's id, from the address. */
  readonly id = input.required<string>();

  /** The commit's number; none when the address's isn't one. */
  private readonly number = computed(() =>
    /^\d{1,15}$/.test(this.id()) ? Number(this.id()) : null,
  );
  protected readonly commit = rxResource({
    params: () => {
      const id = this.number();
      return id === null ? undefined : { id };
    },
    stream: ({ params }) => this.api.get('/api/audit/commits/{id}', { path: { id: params.id } }),
  });
  protected readonly shown = computed(() => (this.commit.hasValue() ? this.commit.value() : null));
  protected readonly problem = computed<Problem | null>(() => {
    if (this.number() === null) {
      return {
        status: 404,
        code: 'not-found',
        title: 'There is no such commit',
        detail: `There is no commit ${this.id()} in the audit`,
      };
    }
    const error = this.commit.error();
    return error ? problemOf(error) : null;
  });
  protected readonly took = computed(() => {
    const commit = this.shown();
    if (!commit?.finishedAt) {
      return '';
    }
    const ms = Date.parse(commit.finishedAt) - Date.parse(commit.startedAt);
    return ms < 1000 ? `${Math.max(ms, 0)} ms` : `${(ms / 1000).toFixed(1)} s`;
  });
  protected readonly statuses = commitStatusLabels;
  protected readonly scripts = scriptStatusLabels;
  protected readonly warns = warns;
  protected readonly message = problemMessage;
}
