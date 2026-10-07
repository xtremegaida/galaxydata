import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { type Observable, firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../../../core/api/api-client';
import { type Problem, ProblemCode, problemMessage, problemOf } from '../../../core/api/problem';
import { Confirmer } from '../../../core/ui/confirmer';
import { Message } from '../../../core/ui/message';
import { changesBetween } from '../model/changes';
import { palettesOf } from '../model/definition';

export type DashboardDto = Schema<'DashboardDto'>;

/** What the dialogs of a dashboard get: it, and where to give it as it is after a change. */
export interface DashboardDialogData {
  readonly dashboard: DashboardDto;
  updated(dashboard: DashboardDto): void;
}

/** A problem as the dialogs say it: someone else's change first, refusals by their first field. */
export function refusalOf(problem: Problem, doing: string): string {
  if (problem.code === ProblemCode.concurrencyConflict) {
    return `The dashboard was changed elsewhere since it was read: close this, and open it again.`;
  }
  const first = Object.values(problem.errors ?? {})[0]?.[0];
  return `Couldn't ${doing}: ${first ?? problemMessage(problem)}`;
}

/**
 * Publishing the working copy: what changed since the last revision, a note to say why, and the new revision
 * everyone the dashboard is shared with sees (and its public link shows).
 */
@Component({
  selector: 'gd-publish-dialog',
  imports: [
    MatButton,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
    MatProgressBar,
    Message,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Publish {{ data.dashboard.name }}</h2>
    <mat-dialog-content class="content">
      <p>
        @if (data.dashboard.publishedNumber; as number) {
          Since revision {{ number }}:
        } @else {
          Not published yet:
        }
      </p>
      <ul class="changes">
        @for (change of changes(); track $index) {
          <li>{{ change }}</li>
        }
      </ul>
      @if (colored()) {
        <p class="aside">
          Its palettes' colours aren't part of a revision: changed in a palette, they show in every
          dashboard using it at once.
        </p>
      }
      <mat-form-field class="note" subscriptSizing="dynamic">
        <mat-label>Note</mat-label>
        <input #note matInput maxlength="200" [value]="text()" (input)="text.set(note.value)" />
        <mat-hint>What the revision is for (its viewers don't see it)</mat-hint>
      </mat-form-field>
      @if (busy()) {
        <mat-progress-bar mode="indeterminate" aria-label="Publishing" />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">{{ problem }}</gd-message>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close type="button">Cancel</button>
      <button matButton="filled" type="button" [disabled]="busy()" (click)="publish()">
        Publish
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .content {
      min-width: min(480px, 80vw);
    }
    .changes {
      margin: 0 0 16px;
    }
    .aside {
      margin: 0 0 16px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    .note {
      width: 100%;
    }
  `,
})
export class PublishDialog {
  protected readonly data = inject<DashboardDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<PublishDialog>);
  private readonly api = inject(ApiClient);

  protected readonly text = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  /** Whether it is drawn with palettes, which follow their own changes, not its revisions. */
  protected readonly colored = computed(() => {
    const working = this.data.dashboard.working;
    return !!working && palettesOf(working).length > 0;
  });

  protected readonly changes = computed(() =>
    changesBetween(
      this.data.dashboard.published,
      this.data.dashboard.working ?? this.data.dashboard.published!,
    ),
  );

  protected async publish(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      const published = await firstValueFrom(
        this.api.post('/api/dashboards/{id}/publish', {
          path: { id: this.data.dashboard.id },
          body: { version: this.data.dashboard.version, note: this.text().trim() || null },
        }),
      );
      this.data.updated(published);
      this.ref.close(published);
    } catch (error) {
      this.problem.set(refusalOf(problemOf(error), 'publish it'));
    } finally {
      this.busy.set(false);
    }
  }
}

/**
 * The revisions published, newest first: the one viewers see marked; any restored into the working copy (to be
 * published again), and the working copy's changes discarded (it is the published copy again).
 */
@Component({
  selector: 'gd-revisions-dialog',
  imports: [
    DatePipe,
    MatButton,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatProgressBar,
    Message,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Revisions of {{ dashboard().name }}</h2>
    <mat-dialog-content class="content">
      @if (revisions.isLoading() || busy()) {
        <mat-progress-bar mode="indeterminate" aria-label="Reading the revisions" />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">{{ problem }}</gd-message>
        }
      </div>
      @if (dashboard().hasUnpublishedChanges && dashboard().published) {
        <gd-message kind="notice">
          The working copy has changes not published.
          <button gdMessageAction matButton type="button" [disabled]="busy()" (click)="discard()">
            Discard them
          </button>
        </gd-message>
      }
      <ul class="revisions">
        @for (revision of revisions.hasValue() ? revisions.value() : []; track revision.number) {
          <li>
            <span class="number">{{ revision.number }}</span>
            <span class="what">
              {{ revision.publishedAt | date: 'medium' }}, by {{ revision.publishedBy }}
              @if (revision.isPublished) {
                <span class="badge">Published</span>
              }
              @if (revision.note) {
                <span class="note">{{ revision.note }}</span>
              }
            </span>
            <button matButton type="button" [disabled]="busy()" (click)="restore(revision.number)">
              Restore
            </button>
          </li>
        } @empty {
          @if (revisions.hasValue()) {
            <li class="none">Nothing has been published yet.</li>
          }
        }
      </ul>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton="filled" mat-dialog-close type="button">Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .content {
      min-width: min(560px, 80vw);
    }
    .revisions {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    li {
      display: grid;
      grid-template-columns: 2.5em 1fr auto;
      gap: 8px;
      align-items: center;
      padding: 6px 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }
    .number {
      font: var(--mat-sys-title-medium);
    }
    .note {
      display: block;
      color: var(--mat-sys-on-surface-variant);
    }
    .badge {
      margin-inline-start: 8px;
      padding: 0 6px;
      border-radius: 6px;
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
      font: var(--mat-sys-label-small);
    }
    .none {
      display: block;
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class RevisionsDialog {
  private readonly data = inject<DashboardDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(ApiClient);
  private readonly confirmer = inject(Confirmer);

  protected readonly dashboard = signal(this.data.dashboard);
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);
  protected readonly revisions = rxResource({
    stream: () =>
      this.api.get('/api/dashboards/{id}/revisions', { path: { id: this.data.dashboard.id } }),
  });

  protected async restore(number: number): Promise<void> {
    const sure =
      !this.dashboard().hasUnpublishedChanges ||
      (await this.confirmer.confirm({
        title: `Restore revision ${number}?`,
        message: 'The working copy becomes it: its changes not published are lost.',
        confirm: 'Restore',
        destructive: true,
      }));
    if (sure) {
      await this.run('restore it', () =>
        this.api.post('/api/dashboards/{id}/revisions/{number}/restore', {
          path: { id: this.dashboard().id, number },
          body: { version: this.dashboard().version },
        }),
      );
    }
  }

  protected async discard(): Promise<void> {
    const sure = await this.confirmer.confirm({
      title: 'Discard the changes?',
      message: 'The working copy becomes the published copy again.',
      confirm: 'Discard',
      destructive: true,
    });
    if (sure) {
      await this.run('discard them', () =>
        this.api.post('/api/dashboards/{id}/discard', {
          path: { id: this.dashboard().id },
          body: { version: this.dashboard().version },
        }),
      );
    }
  }

  private async run(doing: string, request: () => Observable<DashboardDto>): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      const dashboard = await firstValueFrom(request());
      this.dashboard.set(dashboard);
      this.data.updated(dashboard);
    } catch (error) {
      this.problem.set(refusalOf(problemOf(error), doing));
    } finally {
      this.busy.set(false);
    }
  }
}

/** A copy of the dashboard, the user's own and private: of its published copy (the owner's, of the working one). */
@Component({
  selector: 'gd-copy-dialog',
  imports: [
    MatButton,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatFormField,
    MatInput,
    MatLabel,
    Message,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Copy {{ data.dashboard.name }}</h2>
    <mat-dialog-content>
      <mat-form-field class="name" subscriptSizing="dynamic">
        <mat-label>Name of the copy</mat-label>
        <input #name matInput required [value]="text()" (input)="text.set(name.value)" />
      </mat-form-field>
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">{{ problem }}</gd-message>
        }
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close type="button">Cancel</button>
      <button
        matButton="filled"
        type="button"
        [disabled]="busy() || !text().trim()"
        (click)="copy()"
      >
        Copy
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .name {
      width: min(420px, 80vw);
    }
  `,
})
export class CopyDialog {
  protected readonly data = inject<DashboardDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<CopyDialog>);
  private readonly api = inject(ApiClient);
  protected readonly text = signal(`${this.data.dashboard.name} (copy)`);
  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected async copy(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      const copy = await firstValueFrom(
        this.api.post('/api/dashboards/{id}/copy', {
          path: { id: this.data.dashboard.id },
          body: { name: this.text().trim() },
        }),
      );
      this.ref.close(copy);
    } catch (error) {
      const problem = problemOf(error);
      this.problem.set(
        problem.code === 'dashboard-name-taken'
          ? 'You have a dashboard of this name already.'
          : refusalOf(problem, 'copy it'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
