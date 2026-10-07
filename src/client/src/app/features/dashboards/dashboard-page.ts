import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
  untracked,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAnchor, MatButton, MatIconButton } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatMenu, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../../core/api/api-client';
import { problemMessage, problemOf } from '../../core/api/problem';
import { PageTitle } from '../../core/page-titles';
import { Confirmer } from '../../core/ui/confirmer';
import { Message } from '../../core/ui/message';
import { chartPalette } from './charts/series-colors';
import { palettesOf } from './model/definition';
import { maxSelectionKeys } from './model/selection';
import { InlineHost, PublishedHost } from './state/dashboard-host';
import { DashboardStore } from './state/dashboard-store';
import { refreshOnTimer } from './state/refresh';
import { bindUrlState } from './state/url-state';
import { DashboardView } from './view/dashboard-view';

export type DashboardDto = Schema<'DashboardDto'>;

/**
 * A dashboard, as someone signed in sees it: the published copy, or (its owner's, while there is none, or on
 * asking: `?copy=working`) the working copy. Its name is the page's title, and a heading only screen readers see;
 * a bar above holds refreshing and what the user may do. Its filters' values and the slices chosen are kept in the
 * address.
 */
@Component({
  selector: 'gd-dashboard-page',
  imports: [
    DashboardView,
    DatePipe,
    MatAnchor,
    MatButton,
    MatIcon,
    MatIconButton,
    MatMenu,
    MatMenuItem,
    MatMenuTrigger,
    MatProgressBar,
    Message,
    RouterLink,
  ],
  providers: [DashboardStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <h1 class="hidden">{{ shown()?.name }}</h1>
      @if (dashboard.isLoading() && !dashboard.hasValue()) {
        <mat-progress-bar mode="indeterminate" aria-label="Reading the dashboard" />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">
            {{ problem }}
            <button gdMessageAction matButton type="button" (click)="dashboard.reload()">
              Try again
            </button>
          </gd-message>
        }
        @if (actionProblem(); as problem) {
          <gd-message kind="problem">{{ problem }}</gd-message>
        }
      </div>
      <div role="status">
        @if (addressProblems().length > 0) {
          <gd-message kind="notice">
            Some of the address was left out. {{ addressProblems().join(' ') }}
            <button gdMessageAction matButton type="button" (click)="addressProblems.set([])">
              Dismiss
            </button>
          </gd-message>
        }
      </div>
      @if (shown(); as shown) {
        <div class="bar">
          <button matButton type="button" (click)="store.refresh()" [disabled]="store.anyLoading()">
            <mat-icon>refresh</mat-icon>
            Refresh
          </button>
          @if (store.oldestRefresh(); as at) {
            <span class="aside">Read at {{ at | date: 'mediumTime' }}</span>
          }
          @if (working()) {
            <span class="badge">Your working copy</span>
            @if (shown.published) {
              <a matButton [routerLink]="[]" [queryParams]="{}">Show the published copy</a>
            }
          } @else if (shown.hasUnpublishedChanges) {
            <a matButton [routerLink]="[]" [queryParams]="{ copy: 'working' }"
              >Show your working copy</a
            >
          }
          <span class="spacer"></span>
          @if (shown.can.publish && (shown.hasUnpublishedChanges || !shown.published)) {
            <button matButton type="button" (click)="publish()">
              <mat-icon>publish</mat-icon>
              Publish
            </button>
          }
          @if (shown.can.share || shown.can.revokePublic) {
            <button matButton type="button" (click)="share()">
              <mat-icon>share</mat-icon>
              Share
            </button>
          }
          @if (shown.can.edit) {
            <a matButton="tonal" routerLink="edit">
              <mat-icon>edit</mat-icon>
              Edit
            </a>
          }
          <button matIconButton type="button" aria-label="More" [matMenuTriggerFor]="more">
            <mat-icon>more_vert</mat-icon>
          </button>
          <mat-menu #more="matMenu">
            @if (shown.can.publish) {
              <button mat-menu-item type="button" (click)="revisions()">
                <mat-icon>history</mat-icon>
                Revisions
              </button>
            }
            @if (shown.can.copy) {
              <button mat-menu-item type="button" (click)="copyIt()">
                <mat-icon>content_copy</mat-icon>
                Copy
              </button>
            }
            @if (shown.can.delete) {
              <button mat-menu-item type="button" (click)="remove()">
                <mat-icon>delete</mat-icon>
                Delete
              </button>
            }
          </mat-menu>
        </div>
        <gd-dashboard-view />
      }
    </div>
  `,
  styles: `
    .page {
      padding: 8px 16px 24px;
      box-sizing: border-box;
    }
    .bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
    }
    .spacer {
      flex: 1 1 auto;
    }
    .aside {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
    .badge {
      font: var(--mat-sys-label-medium);
      padding: 2px 8px;
      border-radius: 8px;
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
    .hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
    }
  `,
})
export class DashboardPage {
  /** The dashboard's id, from the address. */
  readonly id = input.required<string>();
  /** `working` for the owner's working copy. */
  readonly copy = input<string | undefined>(undefined);

  private readonly api = inject(ApiClient);
  private readonly title = inject(PageTitle);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly confirmer = inject(Confirmer);
  protected readonly store = inject(DashboardStore);
  /** What in the address couldn't be read, until dismissed. */
  protected readonly addressProblems = linkedSignal(bindUrlState(this.store, maxSelectionKeys));

  protected readonly dashboard = rxResource({
    params: () => Number(this.id()),
    stream: ({ params }) => this.api.get('/api/dashboards/{id}', { path: { id: params } }),
  });

  /** The dashboard read, once it is (a resource that failed has no value to read). */
  protected readonly shown = computed(() =>
    this.dashboard.hasValue() ? this.dashboard.value() : null,
  );

  protected readonly problem = computed(() => {
    const error = this.dashboard.error();
    return error ? problemMessage(problemOf(error)) : null;
  });

  /**
   * The palettes its charts are drawn with: those of the dashboard's answer, read again as it is refreshed (they
   * may have changed since: dashboards follow their palettes), when it names any.
   */
  private readonly palettesRead = rxResource({
    params: () => {
      const id = this.shown()?.id;
      const refreshes = this.store.refreshes();
      const named = palettesOf(this.store.definition()).length > 0;
      return id !== undefined && refreshes > 0 && named ? { id, refreshes } : undefined;
    },
    stream: ({ params }) =>
      this.api.get('/api/dashboards/{id}/palettes', { path: { id: params.id } }),
  });

  private readonly palettes = linkedSignal(() => this.shown()?.palettes ?? []);

  /** Whether the working copy is shown: asked for, or the only one there is. */
  protected readonly working = computed(() => {
    const shown = this.shown();
    return !!shown?.working && (this.copy() === 'working' || !shown.published);
  });

  /** What the dialogs get: the dashboard, and where its changes go (the page shows them). */
  private dialogData() {
    return {
      dashboard: this.shown()!,
      updated: (dashboard: DashboardDto) => this.dashboard.value.set(dashboard),
    };
  }

  protected async publish(): Promise<void> {
    const { PublishDialog } = await import('./dialogs/publish-dialogs');
    this.dialog.open(PublishDialog, { data: this.dialogData(), maxWidth: '95vw' });
  }

  protected async revisions(): Promise<void> {
    const { RevisionsDialog } = await import('./dialogs/publish-dialogs');
    this.dialog.open(RevisionsDialog, {
      data: this.dialogData(),
      maxWidth: '95vw',
      maxHeight: '90vh',
    });
  }

  protected async share(): Promise<void> {
    const { ShareDialog } = await import('./dialogs/share-dialog');
    this.dialog.open(ShareDialog, { data: this.dialogData(), maxWidth: '95vw', maxHeight: '90vh' });
  }

  protected async copyIt(): Promise<void> {
    const { CopyDialog } = await import('./dialogs/publish-dialogs');
    const copy = await firstValueFrom(
      this.dialog.open(CopyDialog, { data: this.dialogData() }).afterClosed(),
    );
    if (copy) {
      void this.router.navigate(['/dashboards', (copy as DashboardDto).id]);
    }
  }

  protected async remove(): Promise<void> {
    const shown = this.shown();
    if (!shown) {
      return;
    }
    const sure = await this.confirmer.confirm({
      title: `Delete ${shown.name}?`,
      message: shown.public
        ? 'Its revisions go with it, and its public link stops.'
        : 'Its revisions go with it.',
      confirm: 'Delete',
      destructive: true,
    });
    if (!sure) {
      return;
    }
    try {
      await firstValueFrom(
        this.api.delete('/api/dashboards/{id}', {
          path: { id: shown.id },
          query: { version: shown.version },
        }),
      );
      void this.router.navigate(['/dashboards']);
    } catch (error) {
      this.actionProblem.set(`Couldn't delete it: ${problemMessage(problemOf(error))}`);
    }
  }

  /** Why an action of the bar couldn't be done. */
  protected readonly actionProblem = signal<string | null>(null);

  constructor() {
    refreshOnTimer(
      this.store,
      computed(() => this.store.definition().refresh),
    );
    effect(() => {
      const shown = this.shown();
      if (!shown) {
        return;
      }
      const working = this.working();
      untracked(() => {
        this.title.detail.set(shown.name);
        if (working && shown.working) {
          this.store.definition.set(shown.working);
          this.store.host.set(new InlineHost(this.api, () => this.store.definition()));
        } else if (shown.published && shown.publishedHash) {
          this.store.definition.set(shown.published);
          this.store.host.set(new PublishedHost(this.api, shown.id, shown.publishedHash));
        }
      });
    });
    effect(() => {
      if (this.palettesRead.hasValue()) {
        const read = this.palettesRead.value();
        untracked(() => this.palettes.set(read));
      }
    });
    effect(() => {
      const palettes = this.palettes();
      untracked(() =>
        this.store.palettes.set(new Map(palettes.map((p) => [p.id, chartPalette(p)]))),
      );
    });
    inject(DestroyRef).onDestroy(() => this.title.detail.set(null));
  }
}
