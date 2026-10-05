import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiClient, type Schema } from '../../core/api/api-client';
import { problemMessage, problemOf } from '../../core/api/problem';
import { Message } from '../../core/ui/message';

export type SavedQuerySummary = Schema<'SavedQuerySummaryDto'>;

/** What the dialog knows of the page: the saved query it shows, if any. */
export interface OpenQueryData {
  readonly current: number | null;
}

/**
 * The saved queries the user may open: theirs, and those shared (with their owners' names), found by name,
 * description or owner. Choosing one opens it (its address), closing the dialog.
 */
@Component({
  selector: 'gd-open-query-dialog',
  imports: [
    DatePipe,
    MatButton,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatFormField,
    MatInput,
    MatLabel,
    MatProgressBar,
    Message,
    RouterLink,
  ],
  template: `
    <h2 mat-dialog-title>Open a saved query</h2>
    <mat-dialog-content>
      <mat-form-field class="find" subscriptSizing="dynamic">
        <mat-label>Find</mat-label>
        <input matInput type="search" autocomplete="off" [value]="find()" (input)="typed($event)" />
      </mat-form-field>
      <div role="alert">
        @if (queries.error(); as error) {
          <gd-message kind="problem">
            Couldn't read the saved queries: {{ message(error) }}
            <button gdMessageAction matButton type="button" (click)="queries.reload()">
              Try again
            </button>
          </gd-message>
        }
      </div>
      @if (queries.isLoading()) {
        <mat-progress-bar mode="indeterminate" aria-label="Loading the saved queries" />
      } @else if (queries.hasValue()) {
        @for (group of groups(); track group.label) {
          <section [attr.aria-labelledby]="group.id">
            <h3 [id]="group.id">{{ group.label }}</h3>
            <ul class="queries">
              @for (query of group.queries; track query.id) {
                <li>
                  <a
                    class="query"
                    [routerLink]="['/query', query.id]"
                    [attr.aria-current]="query.id === data.current ? 'page' : null"
                    mat-dialog-close
                  >
                    <span class="name">{{ query.name }}</span>
                    @if (query.isShared && query.isMine) {
                      <span class="badge">shared</span>
                    }
                  </a>
                  <p class="about">
                    @if (query.description) {
                      {{ query.description }} ·
                    }
                    @if (!query.isMine) {
                      {{ query.owner || 'no one (their owner is gone)' }}'s ·
                    }
                    changed {{ query.updatedAt | date: 'medium' }}
                  </p>
                </li>
              }
            </ul>
          </section>
        } @empty {
          <p class="aside">
            {{ find().trim() ? 'No saved query matches.' : 'There are no saved queries yet.' }}
          </p>
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton type="button" mat-dialog-close>Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .find {
      display: block;
      margin-bottom: 8px;
    }

    h3 {
      margin: 12px 0 4px;
      font: var(--mat-sys-title-small);
    }

    .queries {
      margin: 0;
      padding: 0;
      list-style: none;
    }

    li {
      padding: 4px 0;
    }

    .query {
      color: var(--mat-sys-primary);
      font: var(--mat-sys-body-large);
    }

    .query[aria-current] {
      font-weight: 500;
    }

    .badge {
      margin-inline-start: 8px;
      padding: 1px 6px;
      border-radius: var(--mat-sys-corner-small);
      background: var(--mat-sys-surface-container-highest);
      color: var(--mat-sys-on-surface);
      font: var(--mat-sys-label-small);
    }

    .about,
    .aside {
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
  `,
})
export class OpenQueryDialog {
  private readonly api = inject(ApiClient);
  protected readonly data = inject<OpenQueryData>(MAT_DIALOG_DATA);

  protected readonly find = signal('');
  protected readonly queries = rxResource({
    stream: () => this.api.get('/api/saved-queries'),
  });
  protected readonly groups = computed(() => {
    const text = this.find().trim().toLowerCase();
    const found = (this.queries.hasValue() ? this.queries.value() : []).filter(
      (query) =>
        !text ||
        [query.name, query.description ?? '', query.owner].some((part) =>
          part.toLowerCase().includes(text),
        ),
    );
    return [
      { id: 'gd-open-mine', label: 'Yours', queries: found.filter((query) => query.isMine) },
      // Shared, and (for administrators) those whose owner is gone.
      {
        id: 'gd-open-others',
        label: "Other people's",
        queries: found.filter((query) => !query.isMine),
      },
    ].filter((group) => group.queries.length > 0);
  });

  protected typed(event: Event): void {
    this.find.set((event.target as HTMLInputElement).value);
  }

  protected message(error: unknown): string {
    return problemMessage(problemOf(error));
  }
}
