import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatAnchor, MatIconButton } from '@angular/material/button';
import { MatFormField, MatLabel, MatPrefix } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatMenu, MatMenuContent, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { Confirmer } from '../../../core/ui/confirmer';
import { Message } from '../../../core/ui/message';
import { PaletteLibrary, type PaletteSummary } from './palette-library';

/** Every palette, for anyone who reads data: whose it is, its colours, how it gives them, and how many dashboards use it. */
@Component({
  selector: 'gd-palette-list',
  imports: [
    MatAnchor,
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
        <a matIconButton routerLink="/dashboards" aria-label="Dashboards">
          <mat-icon>arrow_back</mat-icon>
        </a>
        <h1>Palettes</h1>
        <mat-form-field subscriptSizing="dynamic">
          <mat-icon matPrefix>search</mat-icon>
          <mat-label>Find a palette</mat-label>
          <input matInput #findInput [value]="find()" (input)="find.set(findInput.value)" />
        </mat-form-field>
        <a matButton="filled" routerLink="new">
          <mat-icon>add</mat-icon>
          New palette
        </a>
      </header>
      <p class="aside">
        Colours for charts' slices and series, which dashboards and their charts choose. A label
        keeps its colour in every chart a palette colours by label.
      </p>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (problem(); as problem) {
        <gd-message kind="problem">{{ problem }}</gd-message>
      }
      <ul class="palettes">
        @for (palette of shown(); track palette.id) {
          <li class="palette">
            <span class="swatches" aria-hidden="true">
              @for (color of palette.colors; track $index) {
                <span class="swatch" [style.background]="color.light"></span>
              }
            </span>
            <a class="name" [routerLink]="[palette.id]">{{ palette.name }}</a>
            <span class="about">
              {{ palette.owner }} · {{ palette.assign === 'label' ? 'by label' : 'by order' }} ·
              {{ palette.overrides }} {{ palette.overrides === 1 ? 'label' : 'labels' }} of their
              own · {{ used(palette) }}
            </span>
            <button
              matIconButton
              type="button"
              [matMenuTriggerFor]="actions"
              [attr.aria-label]="'Actions of ' + palette.name"
            >
              <mat-icon>more_vert</mat-icon>
            </button>
            <mat-menu #actions="matMenu">
              <ng-template matMenuContent>
                <a mat-menu-item [routerLink]="['new']" [queryParams]="{ copy: palette.id }">
                  <mat-icon>content_copy</mat-icon>
                  Copy
                </a>
                @if (palette.canEdit) {
                  <button mat-menu-item type="button" (click)="remove(palette)">
                    <mat-icon>delete</mat-icon>
                    Delete
                  </button>
                }
              </ng-template>
            </mat-menu>
          </li>
        } @empty {
          @if (!loading()) {
            <li class="aside">
              {{
                find()
                  ? 'No palette has this name.'
                  : 'No palettes yet: make one from a base, then give labels their colours.'
              }}
            </li>
          }
        }
      </ul>
    </div>
  `,
  styles: `
    .page {
      display: grid;
      gap: 12px;
      max-width: 1040px;
      margin: 0 auto;
      padding: 16px 24px;
    }
    .header {
      display: flex;
      align-items: center;
      gap: 12px;
      flex-wrap: wrap;
    }
    h1 {
      margin: 0;
      flex: 1 1 auto;
      font: var(--mat-sys-headline-small);
    }
    .aside {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-medium);
    }
    .palettes {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .palette {
      display: grid;
      grid-template-columns: 10rem minmax(8rem, 1fr) minmax(0, 2fr) auto;
      align-items: center;
      gap: 12px;
      padding: 8px 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }
    .swatches {
      display: flex;
      flex-wrap: wrap;
      gap: 2px;
    }
    .swatch {
      width: 14px;
      height: 14px;
      border-radius: 50%;
      box-shadow: 0 0 0 1px var(--mat-sys-outline-variant);
    }
    .name {
      font: var(--mat-sys-title-small);
      color: var(--mat-sys-primary);
    }
    .about {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    @media (max-width: 720px) {
      .palette {
        grid-template-columns: 1fr auto;
      }
      .swatches,
      .about {
        grid-column: 1;
      }
    }
  `,
})
export class PaletteList {
  private readonly api = inject(ApiClient);
  private readonly confirmer = inject(Confirmer);
  private readonly library = inject(PaletteLibrary);

  protected readonly find = signal('');
  protected readonly loading = signal(true);
  protected readonly problem = signal<string | null>(null);

  protected readonly shown = computed(() => {
    const text = this.find().trim().toLowerCase();
    return (this.library.summaries() ?? []).filter(
      (p) => !text || p.name.toLowerCase().includes(text) || p.owner.toLowerCase().includes(text),
    );
  });

  constructor() {
    this.library.list().then(
      () => this.loading.set(false),
      (error: unknown) => {
        this.loading.set(false);
        this.problem.set(`Couldn't list the palettes: ${problemMessage(problemOf(error))}`);
      },
    );
  }

  protected used(palette: PaletteSummary): string {
    return palette.usedBy === 0
      ? 'no dashboard uses it'
      : `used by ${palette.usedBy} ${palette.usedBy === 1 ? 'dashboard' : 'dashboards'}`;
  }

  protected async remove(palette: PaletteSummary): Promise<void> {
    const sure = await this.confirmer.confirm({
      title: `Delete ${palette.name}?`,
      message:
        palette.usedBy > 0
          ? `${palette.usedBy} ${palette.usedBy === 1 ? 'dashboard uses' : 'dashboards use'} it: their charts get their default colours.`
          : 'No dashboard uses it.',
      confirm: 'Delete',
      destructive: true,
    });
    if (!sure) {
      return;
    }
    try {
      await firstValueFrom(
        this.api.delete('/api/palettes/{id}', {
          path: { id: palette.id },
          query: { version: palette.version },
        }),
      );
      this.library.removed(palette.id);
    } catch (error) {
      this.problem.set(`Couldn't delete it: ${problemMessage(problemOf(error))}`);
    }
  }
}
