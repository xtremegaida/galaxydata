import { LiveAnnouncer } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { ApiClient } from '../../../../core/api/api-client';
import { ProblemCode, problemMessage, problemOf } from '../../../../core/api/problem';
import { galaxyColors } from '../../charts/chart-theme';
import {
  type ColorSource,
  type EntityColor,
  type PaletteColor,
  type PaletteDefinition,
  hexOf,
  normalizeLabel,
} from '../../charts/series-colors';
import { configOf, isChart } from '../../model/definition';
import { ColorField } from '../../palettes/color-field';
import { type PaletteDto, PaletteLibrary } from '../../palettes/palette-library';
import { newPalette } from '../../palettes/bases';
import { DashboardStore } from '../../state/dashboard-store';
import { EditorStore } from '../editor-store';

/** What choosing a label's colour is about: the label, the colour it has, the palette's colours, and where it goes. */
export interface SetColorData {
  readonly label: string;
  /** The colour it has, light and dark. */
  readonly color: string;
  readonly dark: string | null;
  readonly colors: readonly PaletteColor[];
  /** Where the colour is kept, said as the dialog asks. */
  readonly where: string;
  readonly note: string | null;
}

/** A label's colour, chosen among its palette's or another (and one for dark backgrounds). */
@Component({
  selector: 'gd-set-color-dialog',
  imports: [
    ColorField,
    MatButton,
    MatCheckbox,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>The colour of {{ data.label }}</h2>
    <mat-dialog-content>
      @if (data.note) {
        <p class="note" role="status">{{ data.note }}</p>
      }
      <div class="picks" role="group" aria-label="The palette's colours">
        @for (color of data.colors; track $index; let i = $index) {
          <button
            type="button"
            class="pick"
            [class.chosen]="color.light === light()"
            [style.background]="color.light"
            [attr.aria-label]="'Colour ' + (i + 1)"
            [attr.aria-pressed]="color.light === light()"
            (click)="light.set(color.light); dark.set(color.dark)"
          ></button>
        }
      </div>
      <gd-color-field label="Its colour" [value]="light()" (valueChange)="lit($event)" />
      <mat-checkbox [checked]="dark() !== null" (change)="dark.set($event.checked ? light() : null)"
        >Another on dark backgrounds</mat-checkbox
      >
      @if (dark(); as shade) {
        <gd-color-field label="On dark" [value]="shade" (valueChange)="dark.set($event)" />
      }
      <p class="aside">{{ data.where }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton type="button" mat-dialog-close>Cancel</button>
      <button matButton="filled" type="button" (click)="done()">Set colour</button>
    </mat-dialog-actions>
  `,
  styles: `
    mat-dialog-content {
      display: grid;
      gap: 12px;
    }
    .picks {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .pick {
      width: 28px;
      height: 28px;
      padding: 0;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 50%;
      cursor: pointer;
    }
    .pick.chosen {
      outline: 2px solid var(--mat-sys-primary);
      outline-offset: 2px;
    }
    .note {
      color: var(--mat-sys-error);
    }
    .aside {
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
  `,
})
export class SetColorDialog {
  protected readonly data = inject<SetColorData>(MAT_DIALOG_DATA);
  private readonly ref = inject(MatDialogRef<SetColorDialog, PaletteColor>);
  protected readonly light = signal(this.data.color);
  protected readonly dark = signal<string | null>(this.data.dark);

  /** A colour of its own: the same on dark backgrounds, till another is chosen for them. */
  protected lit(light: string): void {
    if (this.dark() === this.data.dark && light !== this.light()) {
      this.dark.set(null);
    }
    this.light.set(light);
  }

  protected done(): void {
    this.ref.close({ light: this.light(), dark: this.dark() });
  }
}

const sources: Record<ColorSource, string> = {
  override: 'its own',
  label: 'by label',
  order: 'by order',
  moved: 'moved apart from another',
  neutral: 'grey: the colours ran out',
  default: 'the built-in colours',
};

/** A definition's overrides with a label's colour: the override of what it matches replaced, or one more. */
export function withOverride(
  definition: PaletteDefinition,
  label: string | null,
  color: PaletteColor,
): PaletteDefinition {
  const key = (l: string | null) => (l === null ? null : normalizeLabel(l, definition.matching));
  const at = definition.overrides.findIndex((o) => key(o.label) === key(label));
  const override = { label, color };
  return {
    ...definition,
    overrides:
      at < 0
        ? [...definition.overrides, override]
        : definition.overrides.map((o, i) => (i === at ? override : o)),
  };
}

/**
 * A chart's colours as its preview shows them, and where each comes from, each with Set colour…, which keeps the
 * label's colour in the palette the chart is drawn with (saved there, not a step of the dashboard's undo: every
 * dashboard using it follows). A chart with no palette makes one for the dashboard; another's palette is copied.
 */
@Component({
  selector: 'gd-chart-colors',
  imports: [MatButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h3>Its colours</h3>
    <p class="aside">{{ from() }}</p>
    @if (problem(); as problem) {
      <p class="problem" role="alert">{{ problem }}</p>
    }
    <ul class="colors" aria-label="Its colours">
      @for (entry of entries(); track entry.entity) {
        <li>
          <span class="swatch" aria-hidden="true" [style.background]="entry.color"></span>
          <span class="what">
            <span class="shown">{{ entry.shown }}<span class="hidden">, </span></span>
            <span class="aside">{{ sourceOf(entry.source) }}</span>
          </span>
          <button
            matButton
            type="button"
            [disabled]="busy()"
            [attr.aria-label]="'Set the colour of ' + entry.shown"
            (click)="setColor(entry)"
          >
            Set colour…
          </button>
        </li>
      } @empty {
        <li class="aside">Its preview shows nothing to colour yet.</li>
      }
    </ul>
  `,
  styles: `
    .colors {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      gap: 2px;
    }
    li {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) auto;
      align-items: center;
      column-gap: 8px;
    }
    /* Its label, and below it where its colour comes from. */
    .what {
      display: grid;
      min-width: 0;
    }
    .shown {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .swatch {
      flex: none;
      width: 14px;
      height: 14px;
      border-radius: 50%;
      box-shadow: 0 0 0 1px var(--mat-sys-outline-variant);
    }
    .aside {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    .problem {
      color: var(--mat-sys-error);
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
export class ChartColors {
  readonly widget = input.required<string>();

  private readonly api = inject(ApiClient);
  private readonly dialog = inject(MatDialog);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly dashboard = inject(DashboardStore);
  private readonly store = inject(EditorStore);
  private readonly library = inject(PaletteLibrary);

  protected readonly busy = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly entries = computed(() => this.dashboard.colors().get(this.widget()) ?? []);

  /** The palette the chart is drawn with, and whose choice it is: its own, else the dashboard's (each, when it is there). */
  private readonly drawnWith = computed(() => {
    const widget = this.store.draft().widgets.find((w) => w.id === this.widget());
    const config = widget ? configOf(widget) : null;
    const own = config && isChart(config) ? (config.palette ?? null) : null;
    const shared = this.store.draft().palette ?? null;
    const read = this.library.read();
    if (own !== null && read.get(own)) {
      return { palette: read.get(own)!, scope: 'chart' as const };
    }
    if (shared !== null && read.get(shared)) {
      return { palette: read.get(shared)!, scope: 'dashboard' as const };
    }
    return null;
  });

  protected readonly from = computed(() => {
    const drawn = this.drawnWith();
    if (!drawn) {
      return 'No palette: the built-in colours. Setting a colour makes a palette for the dashboard.';
    }
    const { palette, scope } = drawn;
    const whose = scope === 'chart' ? "The chart's palette" : "The dashboard's palette";
    return palette.canEdit
      ? `${whose}, ${palette.name}: colours set here are saved in it, for every dashboard that uses it.`
      : `${whose}, ${palette.name}, is ${palette.owner}'s: setting a colour copies it.`;
  });

  protected sourceOf(source: ColorSource): string {
    return sources[source];
  }

  protected async setColor(entry: EntityColor): Promise<void> {
    this.problem.set(null);
    this.busy.set(true);
    try {
      let note: string | null = null;
      for (let attempt = 0; attempt < 3; attempt++) {
        const drawn = this.drawnWith();
        // As it is now: it may have changed since it was read.
        const palette = drawn
          ? await firstValueFrom(
              this.api.get('/api/palettes/{id}', { path: { id: drawn.palette.id } }),
            )
          : null;
        if (palette) {
          this.library.put(palette);
        }
        const color = await this.choose(entry, palette, note);
        if (!color) {
          return;
        }
        try {
          await this.keep(entry, color, palette, drawn?.scope ?? 'dashboard');
          this.announcer.announce(`${entry.shown} has its colour`);
          return;
        } catch (error) {
          const problem = problemOf(error);
          if (problem.code !== ProblemCode.concurrencyConflict) {
            throw error;
          }
          note = 'The palette was changed elsewhere meanwhile: here it is as it is now.';
        }
      }
    } catch (error) {
      this.problem.set(`Couldn't set the colour: ${problemMessage(problemOf(error))}`);
    } finally {
      this.busy.set(false);
    }
  }

  private async choose(
    entry: EntityColor,
    palette: PaletteDto | null,
    note: string | null,
  ): Promise<PaletteColor | undefined> {
    const where = !palette
      ? "It makes a palette for the dashboard, of the built-in colours, with this label's."
      : palette.canEdit
        ? `Saved in ${palette.name}, which ${palette.usedBy === 1 ? '1 dashboard uses' : `${palette.usedBy} dashboards use`}.`
        : `${palette.name} is ${palette.owner}'s: it is copied as yours, with this label's colour.`;
    const data: SetColorData = {
      label: entry.shown,
      color: entry.pair?.light ?? hexOf(entry.color),
      dark: entry.pair?.dark ?? null,
      colors: palette?.definition.colors ?? galaxyColors,
      where,
      note,
    };
    return firstValueFrom(
      this.dialog.open(SetColorDialog, { data, maxWidth: '95vw' }).afterClosed(),
    ) as Promise<PaletteColor | undefined>;
  }

  /** Keeps the colour: in the palette, in a copy of another's, or in a new palette of the dashboard's. */
  private async keep(
    entry: EntityColor,
    color: PaletteColor,
    palette: PaletteDto | null,
    scope: 'chart' | 'dashboard',
  ): Promise<void> {
    if (palette?.canEdit) {
      const saved = await firstValueFrom(
        this.api.put('/api/palettes/{id}', {
          path: { id: palette.id },
          body: {
            name: palette.name,
            description: palette.description,
            definition: withOverride(palette.definition, entry.label, color),
            version: palette.version,
          },
        }),
      );
      this.library.put(saved);
      return;
    }
    // As the chart was drawn: today's colours by order, grey after; or a copy of another's palette.
    const definition = palette
      ? withOverride(palette.definition, entry.label, color)
      : withOverride(
          { ...newPalette(galaxyColors), assign: 'order', distinct: false, whenOut: 'neutral' },
          entry.label,
          color,
        );
    const base = palette
      ? `${palette.name} (copy)`
      : `${this.store.name().trim() || 'Dashboard'} colours`;
    const made = await this.made(base, definition);
    this.library.put(made);
    const widget = this.widget();
    if (scope === 'chart' && palette) {
      this.store.apply('Copied its palette', (d) => ({
        ...d,
        widgets: d.widgets.map((w) =>
          w.id === widget ? { ...w, config: { ...w.config, palette: made.id } } : w,
        ),
      }));
    } else {
      this.store.apply(palette ? 'Copied its palette' : 'Made a palette', (d) => ({
        ...d,
        palette: made.id,
      }));
    }
  }

  /** A new palette of the user's, its name made free of theirs (2, 3, … after it) if need be. */
  private async made(name: string, definition: PaletteDefinition): Promise<PaletteDto> {
    for (let n = 1; ; n++) {
      try {
        return await firstValueFrom(
          this.api.post('/api/palettes', {
            body: { name: n === 1 ? name : `${name} ${n}`, description: null, definition },
          }),
        );
      } catch (error) {
        if (problemOf(error).code !== 'palette-name-taken' || n >= 20) {
          throw error;
        }
      }
    }
  }
}
