import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAnchor, MatButton, MatIconButton } from '@angular/material/button';
import { MatMenu, MatMenuContent, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { MatFormField, MatLabel, MatPrefix } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { type Observable, firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { AuthStore } from '../../../core/auth/auth-store';
import { Confirmer } from '../../../core/ui/confirmer';
import { Message } from '../../../core/ui/message';

type Summary = Schema<'DashboardSummaryDto'>;

const sharing: Record<Summary['sharing'], string> = {
  private: 'Private',
  chosen: 'Shared',
  everyone: 'Shared with everyone',
};

/** The dashboards the user sees: their own, those shared with them, and (for administrators) those shared, public or orphaned. */
@Component({
  selector: 'gd-dashboard-list',
  imports: [
    DatePipe,
    MatAnchor,
    MatButton,
    MatFormField,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatMenu,
    MatMenuContent,
    MatMenuItem,
    MatMenuTrigger,
    MatPrefix,
    MatProgressBar,
    Message,
    RouterLink,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <header class="header">
        <h1>Dashboards</h1>
        <mat-form-field subscriptSizing="dynamic">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Find a dashboard</mat-label>
          <input matInput #findInput [value]="find()" (input)="find.set(findInput.value)" />
        </mat-form-field>
        <a matButton="filled" routerLink="new">
          <mat-icon>add</mat-icon>
          New dashboard
        </a>
      </header>
      @if (dashboards.isLoading()) {
        <mat-progress-bar mode="indeterminate" aria-label="Reading the dashboards" />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">
            {{ problem }}
            <button gdMessageAction matButton type="button" (click)="dashboards.reload()">
              Try again
            </button>
          </gd-message>
        }
      </div>
      @if (dashboards.hasValue()) {
        @for (group of groups(); track group.label) {
          @if (group.items.length > 0) {
            <section class="group">
              <h2>{{ group.label }}</h2>
              <ul class="items">
                @for (item of group.items; track item.id) {
                  <li class="item">
                    <div class="head">
                      <a class="name" [routerLink]="[item.id]">{{ item.name }}</a>
                      <button
                        matIconButton
                        type="button"
                        [attr.aria-label]="'Actions of ' + item.name"
                        [matMenuTriggerFor]="actions"
                        [matMenuTriggerData]="{ item }"
                      >
                        <mat-icon>more_vert</mat-icon>
                      </button>
                    </div>
                    @if (item.description) {
                      <span class="description">{{ item.description }}</span>
                    }
                    <span class="facts">
                      @if (!item.isMine) {
                        <span>{{ item.owner }}'s</span>
                      }
                      <span>{{ sharingOf(item) }}</span>
                      @if (item.isPublic) {
                        <span class="badge">Public</span>
                      }
                      @if (item.publishedAt) {
                        <span>Published {{ item.publishedAt | date: 'mediumDate' }}</span>
                      } @else {
                        <span>Not published</span>
                      }
                      @if (item.hasUnpublishedChanges) {
                        <span class="badge">Unpublished changes</span>
                      }
                    </span>
                  </li>
                }
              </ul>
            </section>
          }
        }
        <mat-menu #actions="matMenu">
          <ng-template matMenuContent let-item="item">
            @if (item.isMine) {
              <a mat-menu-item [routerLink]="[item.id, 'edit']">
                <mat-icon>edit</mat-icon>
                Edit
              </a>
            }
            @if (item.isMine || item.publishedAt) {
              <button mat-menu-item type="button" (click)="copy(item)">
                <mat-icon>content_copy</mat-icon>
                Copy
              </button>
            }
            @if (admin() && !item.isMine && item.sharing !== 'private') {
              <button mat-menu-item type="button" (click)="makePrivate(item)">
                <mat-icon>lock</mat-icon>
                Make private
              </button>
            }
            @if (item.isPublic && (item.isMine || admin())) {
              <button mat-menu-item type="button" (click)="revoke(item)">
                <mat-icon>link_off</mat-icon>
                Stop its public link
              </button>
            }
            @if (item.isMine || admin()) {
              <button mat-menu-item type="button" (click)="remove(item)">
                <mat-icon>delete</mat-icon>
                Delete
              </button>
            }
          </ng-template>
        </mat-menu>
        <div role="status">
          @if (done(); as done) {
            <p class="done">{{ done }}</p>
          }
          @if (rows().length === 0) {
            <p class="empty">
              {{
                find()
                  ? 'No dashboard\\'s name has "' + find() + '".'
                  : 'There are no dashboards yet.'
              }}
            </p>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .page {
      max-width: 1100px;
      padding: 16px 24px 32px;
      box-sizing: border-box;
    }
    .header {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px 16px;
      margin-bottom: 16px;
    }
    .header h1 {
      flex: 1 1 auto;
      margin: 0;
      font: var(--mat-sys-headline-small);
    }
    .group h2 {
      font: var(--mat-sys-title-medium);
      margin: 16px 0 8px;
    }
    .items {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .item {
      display: flex;
      flex-direction: column;
      gap: 2px;
      padding: 8px 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
    }
    .name {
      font: var(--mat-sys-title-small);
      color: var(--mat-sys-primary);
    }
    .done {
      color: var(--mat-sys-on-surface-variant);
    }
    .description {
      color: var(--mat-sys-on-surface-variant);
    }
    .facts {
      display: flex;
      flex-wrap: wrap;
      gap: 12px;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
    .badge {
      padding: 0 6px;
      border-radius: 6px;
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
    .empty {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class DashboardList {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthStore);
  private readonly confirmer = inject(Confirmer);
  private readonly router = inject(Router);

  /** Administrators also see others' shared, public and orphaned dashboards, and may narrow or delete them. */
  protected readonly admin = computed(() => this.auth.permissions().canAdmin);
  /** What was last done, said. */
  protected readonly done = signal<string | null>(null);

  protected readonly dashboards = rxResource({ stream: () => this.api.get('/api/dashboards') });
  protected readonly find = signal('');

  protected readonly problem = computed(() => {
    const error = this.dashboards.error();
    return error ? problemMessage(problemOf(error)) : null;
  });

  protected readonly rows = computed(() => {
    const all = this.dashboards.hasValue() ? this.dashboards.value() : [];
    const words = this.find().trim().toLowerCase();
    return words
      ? all.filter(
          (d) =>
            d.name.toLowerCase().includes(words) ||
            (d.description ?? '').toLowerCase().includes(words),
        )
      : all;
  });

  protected readonly groups = computed(() => [
    { label: 'Yours', items: this.rows().filter((d) => d.isMine) },
    { label: 'Shared with you', items: this.rows().filter((d) => !d.isMine) },
  ]);

  protected sharingOf(item: Summary): string {
    return sharing[item.sharing];
  }

  /** A copy of one's own: of the published copy (of the working one, when it is one's own). */
  protected async copy(item: Summary): Promise<void> {
    try {
      const copy = await firstValueFrom(
        this.api.post('/api/dashboards/{id}/copy', {
          path: { id: item.id },
          body: { name: await this.freeName(`${item.name} (copy)`) },
        }),
      );
      void this.router.navigate(['/dashboards', copy.id]);
    } catch (error) {
      this.done.set(`Couldn't copy ${item.name}: ${problemMessage(problemOf(error))}`);
    }
  }

  protected async makePrivate(item: Summary): Promise<void> {
    const sure = await this.confirmer.confirm({
      title: `Make ${item.name} private?`,
      message: `${item.owner}'s viewers no longer see it; its owner may share it again.`,
      confirm: 'Make private',
      destructive: true,
    });
    if (sure) {
      await this.act(item, 'made private', () =>
        this.api.put('/api/dashboards/{id}/sharing', {
          path: { id: item.id },
          body: { everyone: false, users: [], version: item.version },
        }),
      );
    }
  }

  protected async revoke(item: Summary): Promise<void> {
    const sure = await this.confirmer.confirm({
      title: `Stop ${item.name}'s public link?`,
      message: 'It stops at once, wherever it is framed.',
      confirm: 'Stop it',
      destructive: true,
    });
    if (sure) {
      await this.act(item, 'public link stopped', () =>
        this.api.put('/api/dashboards/{id}/public', {
          path: { id: item.id },
          body: { enabled: false, origins: null, version: item.version },
        }),
      );
    }
  }

  protected async remove(item: Summary): Promise<void> {
    const sure = await this.confirmer.confirm({
      title: `Delete ${item.name}?`,
      message: item.isPublic
        ? 'Its revisions go with it, and its public link stops.'
        : 'Its revisions go with it.',
      confirm: 'Delete',
      destructive: true,
    });
    if (sure) {
      await this.act(item, 'deleted', () =>
        this.api.delete('/api/dashboards/{id}', {
          path: { id: item.id },
          query: { version: item.version },
        }),
      );
    }
  }

  private async act(
    item: Summary,
    done: string,
    request: () => Observable<unknown>,
  ): Promise<void> {
    try {
      await firstValueFrom(request());
      this.done.set(`${item.name}: ${done}`);
      this.dashboards.reload();
    } catch (error) {
      this.done.set(`Couldn't do it to ${item.name}: ${problemMessage(problemOf(error))}`);
    }
  }

  /** A name none of the user's dashboards has: the one given, or it numbered. */
  private async freeName(name: string): Promise<string> {
    const mine = new Set(
      (this.dashboards.hasValue() ? (this.dashboards.value() ?? []) : [])
        .filter((d) => d.isMine)
        .map((d) => d.name.toLowerCase()),
    );
    let candidate = name;
    for (let i = 2; mine.has(candidate.toLowerCase()); i++) {
      candidate = `${name} ${i}`;
    }
    return candidate;
  }
}
