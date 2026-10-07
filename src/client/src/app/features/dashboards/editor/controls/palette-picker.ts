import { ChangeDetectionStrategy, Component, computed, inject, input, model } from '@angular/core';
import { MatAnchor } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatSelect, MatSelectTrigger } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { PaletteLibrary } from '../../palettes/palette-library';

/** A palette chosen among them all (by name, with whose it is), or none; the palettes' pages a link away. */
@Component({
  selector: 'gd-palette-picker',
  imports: [
    MatAnchor,
    MatFormField,
    MatHint,
    MatIcon,
    MatLabel,
    MatOption,
    MatSelect,
    MatSelectTrigger,
    RouterLink,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field subscriptSizing="dynamic" class="picker">
      <mat-label>{{ label() }}</mat-label>
      <mat-select
        [value]="value() ?? unchosen"
        (valueChange)="value.set($event === unchosen ? null : $event)"
        (openedChange)="opened($event)"
      >
        <mat-select-trigger>
          @if (chosen(); as chosen) {
            <span class="swatches" aria-hidden="true">
              @for (color of chosen.colors; track $index) {
                <span class="swatch" [style.background]="color.light"></span>
              }
            </span>
            {{ chosen.name }}
          } @else if (value() !== null) {
            A palette that is gone
          } @else {
            {{ none() }}
          }
        </mat-select-trigger>
        <mat-option [value]="unchosen">{{ none() }}</mat-option>
        @for (palette of palettes(); track palette.id) {
          <mat-option [value]="palette.id">
            <span class="swatches" aria-hidden="true">
              @for (color of palette.colors; track $index) {
                <span class="swatch" [style.background]="color.light"></span>
              }
            </span>
            {{ palette.name }} <span class="owner">{{ palette.owner }}</span>
          </mat-option>
        }
        @if (gone()) {
          <mat-option [value]="value()" disabled>A palette that is gone</mat-option>
        }
      </mat-select>
      @if (gone()) {
        <mat-hint class="problem" role="alert">It is gone: the next colours are used</mat-hint>
      } @else if (hint()) {
        <mat-hint>{{ hint() }}</mat-hint>
      }
    </mat-form-field>
    <a matButton routerLink="/dashboards/palettes" target="_blank" class="manage">
      <mat-icon>palette</mat-icon>
      Palettes
    </a>
  `,
  styles: `
    :host {
      display: flex;
      align-items: flex-start;
      gap: 8px;
    }
    .picker {
      flex: 1 1 auto;
      min-width: 12rem;
    }
    .swatches {
      display: inline-flex;
      gap: 1px;
      margin-inline-end: 6px;
      vertical-align: middle;
    }
    .swatch {
      width: 8px;
      height: 14px;
      border-radius: 2px;
    }
    .owner {
      margin-inline-start: 6px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    .problem {
      color: var(--mat-sys-error);
    }
    .manage {
      margin-top: 4px;
    }
  `,
})
export class PalettePicker {
  private readonly library = inject(PaletteLibrary);

  readonly label = input('Palette');
  /** What none means here: the dashboard's palette, or the built-in colours. */
  readonly none = input('None: the built-in colours');
  readonly hint = input<string | null>(null);
  readonly value = model<number | null>(null);

  /** None chosen, as the select holds it: Material takes null for nothing chosen, and would show nothing. */
  protected readonly unchosen = 0;

  protected readonly palettes = computed(() => this.library.summaries() ?? []);
  protected readonly chosen = computed(
    () => this.palettes().find((p) => p.id === this.value()) ?? null,
  );
  /** Named, but not among them (deleted): once they are listed. */
  protected readonly gone = computed(
    () => this.value() !== null && this.library.summaries() !== null && !this.chosen(),
  );

  constructor() {
    if (this.library.summaries() === null) {
      void this.library.list().catch(() => undefined);
    }
  }

  /** Opened, they are listed again: others may have made some since. */
  protected opened(open: boolean): void {
    if (open) {
      void this.library.list().catch(() => undefined);
    }
  }
}
