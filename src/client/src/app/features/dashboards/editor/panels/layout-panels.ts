import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatRadioButton, MatRadioGroup } from '@angular/material/radio';
import { configOf } from '../../model/definition';
import {
  customize,
  designedOf,
  hideAt,
  moveWidget,
  resetBreakpoint,
  resizeWidget,
  setBreakpoints,
  setLayoutSizes,
  setRefresh,
} from '../../model/definition-ops';
import type { Breakpoint } from '../../model/definition';
import { effectiveLayout, minColumns } from '../../layout/grid-layout';
import { WIDGET_KINDS, kindOf } from '../../model/widget-registry';
import { EditorStore, type IssuePlace } from '../editor-store';
import { PalettePicker } from '../controls/palette-picker';

/** The least seconds between refreshes (the server's `Dashboards:MinRefreshSeconds`). */
export const minRefreshSeconds = 30;

const panelStyles = `
  .panel {
    display: grid;
    gap: 12px;
  }
  h3 {
    margin: 8px 0 0;
    font: var(--mat-sys-title-small);
  }
  .row {
    display: grid;
    grid-template-columns: 1fr 1fr 1fr auto;
    gap: 8px;
    align-items: start;
  }
  .pair {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 8px;
  }
  .aside {
    margin: 0;
    font: var(--mat-sys-body-small);
    color: var(--mat-sys-on-surface-variant);
  }
`;

/**
 * The grid: its breakpoints (from 0, ascending, the widest the one designed; 1 to 24 columns each), its rows'
 * height and gaps; at a narrower breakpoint, whether it follows the designed layout or is laid out by hand (and
 * the widgets hidden there); the chosen widget's cell, typed.
 */
@Component({
  selector: 'gd-layout-panel',
  imports: [MatButton, MatFormField, MatHint, MatIcon, MatIconButton, MatInput, MatLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="panel">
      <h3>At {{ store.breakpoint().label }}</h3>
      @if (store.designed()) {
        <p class="aside">
          The layout designed: narrower widths are derived from it unless laid out by hand.
        </p>
      } @else if (store.laidOut()) {
        <p class="aside">Laid out by hand: changes here are this width's alone.</p>
        @for (id of hidden(); track id) {
          <div class="hidden-widget">
            <span>{{ store.nameOf(id) }} is hidden here</span>
            <button matButton type="button" (click)="show(id)">Show it</button>
          </div>
        }
        <button matButton type="button" (click)="reset()">
          <mat-icon>restart_alt</mat-icon>
          Derive it from {{ designed().label }} again
        </button>
      } @else {
        <p class="aside">
          Follows the {{ designed().label }} layout ({{ store.breakpoint().columns }} columns).
        </p>
        <button matButton="tonal" type="button" (click)="customizeShown()">
          <mat-icon>dashboard_customize</mat-icon>
          Lay it out by hand
        </button>
      }
      @if (cell(); as cell) {
        <h3>{{ store.nameOf(store.selected()!) }}</h3>
        <div class="pair">
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Column</mat-label>
            <input
              matInput
              type="number"
              min="1"
              [disabled]="!store.laidOut()"
              [value]="cell.x + 1"
              (change)="placed($event, 'x')"
            />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Row</mat-label>
            <input
              matInput
              type="number"
              min="1"
              [disabled]="!store.laidOut()"
              [value]="cell.y + 1"
              (change)="placed($event, 'y')"
            />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Width, in columns</mat-label>
            <input
              matInput
              type="number"
              min="1"
              [disabled]="!store.laidOut()"
              [value]="cell.w"
              (change)="sized($event, 'w')"
            />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Height, in rows</mat-label>
            <input
              matInput
              type="number"
              min="1"
              [disabled]="!store.laidOut()"
              [value]="cell.h"
              (change)="sized($event, 'h')"
            />
          </mat-form-field>
        </div>
      }
      <h3>Breakpoints</h3>
      @for (breakpoint of breakpoints(); track $index; let i = $index) {
        <div class="row">
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Name</mat-label>
            <input
              matInput
              [value]="breakpoint.label"
              (change)="edited(i, { label: text($event) })"
            />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>From, px</mat-label>
            <input
              matInput
              type="number"
              min="0"
              [disabled]="i === 0"
              [value]="breakpoint.minWidth"
              (change)="edited(i, { minWidth: number($event) })"
            />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Columns</mat-label>
            <input
              matInput
              type="number"
              min="1"
              max="24"
              [value]="breakpoint.columns"
              (change)="edited(i, { columns: number($event) })"
            />
          </mat-form-field>
          <button
            matIconButton
            type="button"
            [disabled]="breakpoints().length === 1 || i === 0"
            [attr.aria-label]="'Remove ' + breakpoint.label"
            (click)="removeBreakpoint(i)"
          >
            <mat-icon>delete</mat-icon>
          </button>
        </div>
      }
      <button matButton type="button" (click)="addBreakpoint()">
        <mat-icon>add</mat-icon>
        Add a breakpoint
      </button>
      @if (problem(); as problem) {
        <p class="aside" role="alert">{{ problem }}</p>
      }
      <div class="pair">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Row height, px</mat-label>
          <input
            matInput
            type="number"
            min="16"
            max="400"
            [value]="store.draft().layout.rowHeight"
            (change)="sizes($event, 'rowHeight')"
          />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Gap, px</mat-label>
          <input
            matInput
            type="number"
            min="0"
            max="64"
            [value]="store.draft().layout.gap"
            (change)="sizes($event, 'gap')"
          />
          <mat-hint>Between widgets</mat-hint>
        </mat-form-field>
      </div>
    </div>
  `,
  styles: `
    ${panelStyles}
    .hidden-widget {
      display: flex;
      justify-content: space-between;
      align-items: center;
    }
  `,
})
export class LayoutPanel {
  protected readonly store = inject(EditorStore);
  private readonly kinds = inject(WIDGET_KINDS);

  protected readonly designed = computed(() => designedOf(this.store.draft()));
  protected readonly breakpoints = computed(() => this.store.draft().layout.breakpoints);
  protected readonly hidden = computed(
    () => this.store.draft().layout.overrides[this.store.breakpoint().id]?.hidden ?? [],
  );

  /** The chosen widget's cell at the breakpoint shown. */
  protected readonly cell = computed(() => {
    const id = this.store.selected();
    const definition = this.store.draft();
    if (!id) {
      return null;
    }
    return (
      effectiveLayout(
        definition.layout,
        this.store.breakpoint().id,
        definition.widgets.map((w) => w.id),
      ).cells[id] ?? null
    );
  });

  /** What is wrong with the breakpoints typed, if anything: they're kept only when right. */
  protected readonly problem = signal<string | null>(null);

  protected text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected number(event: Event): number {
    return Math.round(Number((event.target as HTMLInputElement).value));
  }

  protected customizeShown(): void {
    const breakpoint = this.store.breakpoint();
    const definition = this.store.draft();
    const mins = Object.fromEntries(
      definition.widgets.map((w) => [
        w.id,
        {
          w: minColumns(
            kindOf(this.kinds, configOf(w))?.minWidthPx ?? 120,
            breakpoint,
            definition.layout.gap,
          ),
          h: 1,
        },
      ]),
    );
    this.store.apply(`Laid ${breakpoint.label} out by hand`, (d) =>
      customize(d, breakpoint.id, mins),
    );
  }

  protected reset(): void {
    const breakpoint = this.store.breakpoint();
    this.store.apply(`Derived ${breakpoint.label} again`, (d) => resetBreakpoint(d, breakpoint.id));
  }

  /** Shown again: it is placed where the layout has room for it after the widget before it. */
  protected show(id: string): void {
    const breakpoint = this.store.breakpoint();
    this.store.apply(`Showed ${this.store.nameOf(id)} at ${breakpoint.label}`, (d) =>
      hideAt(d, breakpoint.id, id, false),
    );
  }

  protected placed(event: Event, axis: 'x' | 'y'): void {
    const cell = this.cell();
    const id = this.store.selected();
    if (!cell || !id) {
      return;
    }
    const value = Math.max(0, this.number(event) - 1);
    const to = { x: axis === 'x' ? value : cell.x, y: axis === 'y' ? value : cell.y };
    this.store.apply(`Moved ${this.store.nameOf(id)}`, (d) =>
      moveWidget(d, this.store.breakpoint().id, id, to),
    );
  }

  protected sized(event: Event, axis: 'w' | 'h'): void {
    const cell = this.cell();
    const id = this.store.selected();
    if (!cell || !id) {
      return;
    }
    const value = Math.max(1, this.number(event));
    const size = { w: axis === 'w' ? value : cell.w, h: axis === 'h' ? value : cell.h };
    this.store.apply(`Resized ${this.store.nameOf(id)}`, (d) =>
      resizeWidget(d, this.store.breakpoint().id, id, size),
    );
  }

  protected edited(index: number, change: Partial<Breakpoint>): void {
    const next = this.breakpoints().map((b, i) => (i === index ? { ...b, ...change } : b));
    this.replace(next);
  }

  protected addBreakpoint(): void {
    const last = this.breakpoints().at(-1)!;
    this.replace([
      ...this.breakpoints(),
      {
        id: `w${last.minWidth + 400}`,
        label: 'Wider',
        minWidth: last.minWidth + 400,
        columns: Math.min(24, last.columns * 2),
      },
    ]);
  }

  protected removeBreakpoint(index: number): void {
    this.replace(this.breakpoints().filter((_, i) => i !== index));
  }

  protected sizes(event: Event, which: 'rowHeight' | 'gap'): void {
    const value = this.number(event);
    const [min, max] = which === 'rowHeight' ? [16, 400] : [0, 64];
    if (value >= min && value <= max) {
      this.store.apply(
        'Changed the grid',
        (d) => setLayoutSizes(d, { [which]: value }),
        `grid:${which}`,
      );
    }
  }

  /** Breakpoints kept when they are right: from 0, ascending, 1 to 24 columns each; said otherwise. */
  private replace(breakpoints: readonly Breakpoint[]): void {
    const wrong =
      breakpoints.length === 0
        ? 'Keep a breakpoint.'
        : breakpoints[0].minWidth !== 0
          ? 'The first breakpoint is from 0.'
          : breakpoints.some((b, i) => i > 0 && b.minWidth <= breakpoints[i - 1].minWidth)
            ? 'Each breakpoint is from a width wider than the one before.'
            : breakpoints.some((b) => b.columns < 1 || b.columns > 24)
              ? 'Breakpoints have 1 to 24 columns.'
              : breakpoints.some((b) => !b.label.trim())
                ? 'Name every breakpoint.'
                : null;
    this.problem.set(wrong);
    if (!wrong) {
      this.store.apply('Changed the breakpoints', (d) => setBreakpoints(d, breakpoints));
    }
  }
}

/**
 * The dashboard's own settings: when its rows are read again (by hand only, or every so many seconds while it is
 * shown), and the palette its charts are drawn with (each may have its own instead).
 */
@Component({
  selector: 'gd-refresh-panel',
  imports: [
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
    MatRadioButton,
    MatRadioGroup,
    PalettePicker,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="panel">
      <mat-radio-group
        class="modes"
        aria-label="Refresh"
        [value]="store.draft().refresh.mode"
        (change)="moded($event.value)"
      >
        <mat-radio-button value="manual">When viewers refresh it</mat-radio-button>
        <mat-radio-button value="interval">On a timer, too</mat-radio-button>
      </mat-radio-group>
      @if (store.draft().refresh.mode === 'interval') {
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Every, in seconds</mat-label>
          <input
            matInput
            type="number"
            [min]="min"
            [value]="store.draft().refresh.seconds"
            (change)="timed($event)"
          />
          <mat-hint>{{ min }} at least; not while the page is hidden</mat-hint>
        </mat-form-field>
      }
      <h3>Colours</h3>
      <gd-palette-picker
        label="Its charts' palette"
        hint="A chart may have its own instead (its settings' Colours)"
        [value]="store.draft().palette ?? null"
        (valueChange)="paletted($event)"
      />
    </div>
  `,
  styles: `
    ${panelStyles}
    .modes {
      display: grid;
    }
  `,
})
export class RefreshPanel {
  protected readonly store = inject(EditorStore);
  protected readonly min = minRefreshSeconds;

  protected moded(mode: 'manual' | 'interval'): void {
    this.store.apply('Changed the refresh', (d) =>
      setRefresh(
        d,
        mode === 'manual' ? { mode, seconds: null } : { mode, seconds: d.refresh.seconds ?? 300 },
      ),
    );
  }

  protected paletted(palette: number | null): void {
    this.store.apply(palette === null ? 'Took the palette away' : 'Chose a palette', (d) => {
      const next = { ...d };
      if (palette === null) {
        delete next.palette;
      } else {
        next.palette = palette;
      }
      return next;
    });
  }

  protected timed(event: Event): void {
    const seconds = Math.round(Number((event.target as HTMLInputElement).value));
    if (seconds >= this.min) {
      this.store.apply(
        'Changed the refresh',
        (d) => setRefresh(d, { mode: 'interval', seconds }),
        'refresh',
      );
    }
  }
}

/**
 * What is wrong (or may be): the editor's own, the server's for the dashboard (as it last read or saved it) and for
 * each widget (as its preview last said), and a save's refusals by field; each going to where it is.
 */
@Component({
  selector: 'gd-issues-panel',
  imports: [MatButton, MatIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="issues">
      @for (issue of issues(); track $index) {
        <li [class.error]="issue.severity === 'error'">
          <mat-icon aria-hidden="true">{{
            issue.severity === 'error' ? 'error' : 'warning'
          }}</mat-icon>
          <span class="message">
            <span class="hidden">{{ issue.severity === 'error' ? 'Error: ' : 'Warning: ' }}</span>
            {{ issue.message }}
          </span>
          @if (issue.place) {
            <button matButton type="button" (click)="go.emit(issue.place)">Go to</button>
          }
        </li>
      } @empty {
        <li class="none">Nothing is wrong that the editor knows of.</li>
      }
    </ul>
  `,
  styles: `
    .issues {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      gap: 8px;
    }
    li {
      display: grid;
      grid-template-columns: auto 1fr auto;
      gap: 8px;
      align-items: start;
      font: var(--mat-sys-body-medium);
    }
    li mat-icon {
      color: var(--mat-sys-tertiary);
    }
    li.error mat-icon {
      color: var(--mat-sys-error);
    }
    .none {
      display: block;
      color: var(--mat-sys-on-surface-variant);
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
export class IssuesPanel {
  private readonly store = inject(EditorStore);
  /** Where an issue is, to go there. */
  readonly go = output<IssuePlace>();

  protected readonly issues = this.store.issues;
}
