import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAnchor, MatButton, MatIconButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatRadioButton, MatRadioGroup } from '@angular/material/radio';
import { MatOption, MatSelect } from '@angular/material/select';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { MatTooltip } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, firstValueFrom } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { type Problem, ProblemCode, problemMessage, problemOf } from '../../../core/api/problem';
import { PageTitle } from '../../../core/page-titles';
import { Confirmer } from '../../../core/ui/confirmer';
import { Message } from '../../../core/ui/message';
import { type HasUnsavedChanges, warnBeforeUnload } from '../../../core/ui/unsaved-changes';
import { fallbackTheme } from '../charts/chart-theme';
import { colorWarnings } from '../charts/color-checks';
import {
  type ColorSource,
  type LabelMatching,
  type PaletteColor,
  type PaletteDefinition,
  PaletteMemory,
  chartPalette,
  normalizeLabel,
} from '../charts/series-colors';
import { EntityCatalog } from '../editor/entity-catalog';
import { newPalette, paletteBases } from './bases';
import { ColorField } from './color-field';
import { DataLabels, type FoundLabel } from './data-labels';
import { type PaletteDto, PaletteLibrary } from './palette-library';

const sources: Record<ColorSource, string> = {
  override: 'its own',
  label: 'by label',
  order: 'by order',
  moved: 'moved, kept apart',
  neutral: 'grey',
  default: '',
};

const matchings: { key: keyof LabelMatching; label: string }[] = [
  { key: 'ignoreCase', label: 'Case (Open is open)' },
  { key: 'ignoreWhitespace', label: 'Spaces (Cape Town is CapeTown)' },
  { key: 'ignoreBrackets', label: 'Text in brackets (Cape Town (CPT) is Cape Town)' },
  { key: 'ignoreAccents', label: 'Accents (Zürich is Zurich)' },
];

/**
 * A palette, made or changed: its colours (from a base, when new), how labels get them, how labels are matched,
 * and the colours of labels of its own (overrides), found in the data or written. A preview shows the labels it
 * colours in both schemes, and checks say which colours are hard to see or to tell apart. Saved at the version
 * read (Ctrl+S); every dashboard using it follows at once.
 */
@Component({
  selector: 'gd-palette-editor',
  imports: [
    ColorField,
    DataLabels,
    MatAnchor,
    MatButton,
    MatCheckbox,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatOption,
    MatProgressBar,
    MatRadioButton,
    MatRadioGroup,
    MatSelect,
    MatSlideToggle,
    MatTooltip,
    Message,
    RouterLink,
  ],
  providers: [EntityCatalog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'keys($event)' },
  template: `
    <div class="page">
      <header class="header">
        <a matIconButton routerLink="/dashboards/palettes" aria-label="Palettes">
          <mat-icon>arrow_back</mat-icon>
        </a>
        <mat-form-field class="name" subscriptSizing="dynamic">
          <mat-label>Name</mat-label>
          <input
            #nameInput
            matInput
            required
            [value]="name()"
            [disabled]="!canEdit()"
            (input)="name.set(nameInput.value)"
          />
          @if (nameProblem(); as problem) {
            <mat-hint class="problem" role="alert">{{ problem }}</mat-hint>
          }
        </mat-form-field>
        <button
          matButton="filled"
          type="button"
          [disabled]="!canEdit() || saving() || !dirty()"
          (click)="save()"
        >
          <mat-icon>save</mat-icon>
          Save
        </button>
        @if (saved(); as saved) {
          @if (saved.canEdit) {
            <button matIconButton type="button" aria-label="Delete the palette" (click)="remove()">
              <mat-icon>delete</mat-icon>
            </button>
          }
        }
      </header>
      @if (read.isLoading() || saving()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (loadProblem(); as problem) {
        <gd-message kind="problem">{{ problem }}</gd-message>
      }
      @if (saved(); as saved) {
        @if (!saved.canEdit) {
          <gd-message kind="notice">
            {{ saved.name }} is {{ saved.owner }}'s: copy it to change its colours.
            <a
              matButton
              [routerLink]="['/dashboards/palettes/new']"
              [queryParams]="{ copy: saved.id }"
              >Copy it</a
            >
          </gd-message>
        }
        <p class="aside">
          {{ usedBy(saved) }}
        </p>
      }
      @if (saveProblem(); as problem) {
        <gd-message kind="problem">
          {{ problem.text }}
          @if (problem.conflict) {
            <button matButton type="button" (click)="readAgain()">Read it again</button>
            <button matButton type="button" (click)="saveCopy()">Save as a copy</button>
          }
        </gd-message>
      }

      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Description</mat-label>
        <textarea
          #about
          matInput
          rows="2"
          [value]="description()"
          [disabled]="!canEdit()"
          (input)="description.set(about.value)"
        ></textarea>
      </mat-form-field>

      @if (!saved()) {
        <section aria-labelledby="gd-palette-base">
          <h2 id="gd-palette-base">Start from</h2>
          <div class="bases" role="radiogroup" aria-labelledby="gd-palette-base">
            @for (base of bases; track base.id) {
              <button
                type="button"
                class="base"
                role="radio"
                [attr.aria-checked]="base.id === baseId()"
                (click)="startFrom(base.id)"
              >
                <span class="swatches" aria-hidden="true">
                  @for (color of base.colors; track $index) {
                    <span class="swatch" [style.background]="color.light"></span>
                  }
                </span>
                <span class="base-name">{{ base.name }}</span>
                <span class="aside">{{ base.description }}</span>
              </button>
            }
          </div>
        </section>
      }

      <section aria-labelledby="gd-palette-colors">
        <h2 id="gd-palette-colors">Colours</h2>
        <ol class="colors">
          @for (color of definition().colors; track $index; let i = $index) {
            <li class="color">
              <gd-color-field
                [label]="'Colour ' + (i + 1)"
                [value]="color.light"
                [disabled]="!canEdit()"
                [refused]="refusal('colors[' + i + '].light')"
                (valueChange)="setColor(i, { light: $event })"
              />
              <mat-checkbox
                [checked]="color.dark !== null"
                [disabled]="!canEdit()"
                (change)="setColor(i, { dark: $event.checked ? color.light : null })"
                >Another on dark backgrounds</mat-checkbox
              >
              @if (color.dark !== null) {
                <gd-color-field
                  [label]="'Colour ' + (i + 1) + ' on dark'"
                  [value]="color.dark"
                  [disabled]="!canEdit()"
                  [refused]="refusal('colors[' + i + '].dark')"
                  (valueChange)="setColor(i, { dark: $event })"
                />
              }
              <span class="moves">
                <button
                  matIconButton
                  type="button"
                  [disabled]="!canEdit() || i === 0"
                  [attr.aria-label]="'Move colour ' + (i + 1) + ' up'"
                  (click)="moveColor(i, -1)"
                >
                  <mat-icon>arrow_upward</mat-icon>
                </button>
                <button
                  matIconButton
                  type="button"
                  [disabled]="!canEdit() || i === definition().colors.length - 1"
                  [attr.aria-label]="'Move colour ' + (i + 1) + ' down'"
                  (click)="moveColor(i, 1)"
                >
                  <mat-icon>arrow_downward</mat-icon>
                </button>
                <button
                  matIconButton
                  type="button"
                  [disabled]="!canEdit() || definition().colors.length === 1"
                  [attr.aria-label]="'Remove colour ' + (i + 1)"
                  (click)="removeColor(i)"
                >
                  <mat-icon>delete</mat-icon>
                </button>
              </span>
            </li>
          }
        </ol>
        <button matButton type="button" [disabled]="!canEdit()" (click)="addColor()">
          <mat-icon>add</mat-icon>
          Add a colour
        </button>
        @if (reshuffled()) {
          <p class="aside" role="status">
            Moving or removing colours changes the colours labels get by label (adding them at the
            end changes few).
          </p>
        }
      </section>

      <section aria-labelledby="gd-palette-assign">
        <h2 id="gd-palette-assign">How labels get them</h2>
        <mat-radio-group
          class="choices"
          aria-labelledby="gd-palette-assign"
          [value]="definition().assign"
          [disabled]="!canEdit()"
          (change)="set({ assign: $event.value })"
        >
          <mat-radio-button value="label"
            >By label: a label has its colour in every chart</mat-radio-button
          >
          <mat-radio-button value="order"
            >By order: in the order a chart shows its labels</mat-radio-button
          >
        </mat-radio-group>
        <mat-slide-toggle
          [checked]="definition().distinct"
          [disabled]="!canEdit()"
          (change)="set({ distinct: $event.checked })"
          >Keep labels' colours apart within a chart</mat-slide-toggle
        >
        <mat-form-field subscriptSizing="dynamic" class="out">
          <mat-label>When colours run out</mat-label>
          <mat-select
            [value]="definition().whenOut"
            [disabled]="!canEdit()"
            (valueChange)="set({ whenOut: $event })"
          >
            <mat-option value="repeat">Use them again</mat-option>
            <mat-option value="neutral">Grey, as "Other" is</mat-option>
          </mat-select>
        </mat-form-field>
      </section>

      <section aria-labelledby="gd-palette-matching">
        <h2 id="gd-palette-matching">Labels match, whatever their</h2>
        <div class="choices">
          @for (option of matchings; track option.key) {
            <mat-checkbox
              [checked]="definition().matching[option.key]"
              [disabled]="!canEdit()"
              (change)="match(option.key, $event.checked)"
              >{{ option.label }}</mat-checkbox
            >
          }
        </div>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Try a label</mat-label>
          <input #trial matInput [value]="tried()" (input)="tried.set(trial.value)" />
          @if (tried()) {
            <mat-hint>Matched as “{{ triedAs() }}”</mat-hint>
          }
        </mat-form-field>
      </section>

      <section aria-labelledby="gd-palette-overrides">
        <h2 id="gd-palette-overrides">Labels of their own colour</h2>
        @for (item of definition().overrides; track $index; let i = $index) {
          <div class="override">
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Label</mat-label>
              <input
                matInput
                class="label"
                [value]="item.label ?? ''"
                [disabled]="!canEdit() || item.label === null"
                (input)="setOverride(i, { label: $any($event.target).value })"
              />
              @if (overrideProblems()[i]; as problem) {
                <mat-hint class="problem" role="alert">{{ problem }}</mat-hint>
              }
            </mat-form-field>
            <mat-checkbox
              [checked]="item.label === null"
              [disabled]="!canEdit()"
              (change)="setOverride(i, { label: $event.checked ? null : '' })"
              >No value</mat-checkbox
            >
            <span class="picks" role="group" [attr.aria-label]="picksOf(item.label)">
              @for (color of definition().colors; track $index; let c = $index) {
                <button
                  type="button"
                  class="pick"
                  [style.background]="color.light"
                  [disabled]="!canEdit()"
                  [matTooltip]="'Colour ' + (c + 1)"
                  [attr.aria-label]="'Colour ' + (c + 1)"
                  (click)="setOverride(i, { color: { light: color.light, dark: color.dark } })"
                ></button>
              }
            </span>
            <gd-color-field
              label="Its colour"
              [value]="item.color.light"
              [disabled]="!canEdit()"
              [refused]="refusal('overrides[' + i + '].color.light')"
              (valueChange)="setOverride(i, { color: { light: $event, dark: item.color.dark } })"
            />
            <mat-checkbox
              [checked]="item.color.dark !== null"
              [disabled]="!canEdit()"
              (change)="
                setOverride(i, {
                  color: {
                    light: item.color.light,
                    dark: $event.checked ? item.color.light : null,
                  },
                })
              "
              >Another on dark</mat-checkbox
            >
            @if (item.color.dark !== null) {
              <gd-color-field
                label="On dark"
                [value]="item.color.dark"
                [disabled]="!canEdit()"
                (valueChange)="setOverride(i, { color: { light: item.color.light, dark: $event } })"
              />
            }
            <button
              matIconButton
              type="button"
              [disabled]="!canEdit()"
              [attr.aria-label]="'Remove the colour of ' + labelOf(item.label)"
              (click)="removeOverride(i)"
            >
              <mat-icon>delete</mat-icon>
            </button>
          </div>
        } @empty {
          <p class="aside">
            None yet: labels get the palette's colours. Give a label one of its own here, from the
            data below, or from a chart's colours in the dashboard editor.
          </p>
        }
        <button matButton type="button" [disabled]="!canEdit()" (click)="addOverride(null)">
          <mat-icon>add</mat-icon>
          Add a label
        </button>
        <h3>Labels in the data</h3>
        <gd-data-labels [disabled]="!canEdit()" (add)="addFound($event)" />
      </section>

      <section aria-labelledby="gd-palette-preview">
        <h2 id="gd-palette-preview">Preview</h2>
        <ul class="preview">
          @for (item of preview(); track $index) {
            <li>
              <span class="swatch" aria-hidden="true" [style.background]="item.light"></span>
              <span class="swatch" aria-hidden="true" [style.background]="item.dark"></span>
              {{ item.shown }}
              @if (item.source) {
                <span class="aside">{{ item.source }}</span>
              }
            </li>
          } @empty {
            <li class="aside">Labels with colours of their own, and those found, show here.</li>
          }
        </ul>
      </section>

      <section aria-labelledby="gd-palette-checks">
        <h2 id="gd-palette-checks">Checks</h2>
        <ul class="checks">
          @for (warning of warnings(); track $index) {
            <li><mat-icon aria-hidden="true">warning</mat-icon>{{ warning.message }}</li>
          } @empty {
            <li>
              <mat-icon aria-hidden="true">check_circle</mat-icon>The colours stand out, and are
              told apart, light and dark.
            </li>
          }
        </ul>
      </section>
    </div>
  `,
  styles: `
    .page {
      display: grid;
      gap: 16px;
      max-width: 1040px;
      margin: 0 auto;
      padding: 16px 24px 48px;
    }
    .header {
      display: flex;
      align-items: center;
      gap: 12px;
    }
    .name {
      flex: 1 1 auto;
    }
    h2 {
      margin: 8px 0;
      font: var(--mat-sys-title-medium);
    }
    h3 {
      margin: 16px 0 8px;
      font: var(--mat-sys-title-small);
    }
    .aside {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    .problem {
      color: var(--mat-sys-error);
    }
    .bases {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(15rem, 1fr));
      gap: 8px;
    }
    .base {
      display: grid;
      gap: 4px;
      padding: 12px;
      text-align: start;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 12px;
      background: var(--mat-sys-surface-container-low);
      color: inherit;
      font: var(--mat-sys-body-medium);
      cursor: pointer;
    }
    .base[aria-checked='true'] {
      border-color: var(--mat-sys-primary);
      box-shadow: inset 0 0 0 1px var(--mat-sys-primary);
    }
    .base-name {
      font: var(--mat-sys-title-small);
    }
    .swatches {
      display: flex;
      gap: 2px;
    }
    .swatch {
      display: inline-block;
      width: 14px;
      height: 14px;
      border-radius: 50%;
      box-shadow: 0 0 0 1px var(--mat-sys-outline-variant);
    }
    .colors {
      margin: 0;
      padding: 0;
      list-style: none;
      display: grid;
      gap: 8px;
    }
    .color,
    .override {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px 16px;
    }
    .override {
      padding: 8px 0;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }
    /* Labels as wide in every row, so their colours line up. */
    .override > mat-form-field {
      width: 14rem;
    }
    .moves {
      display: inline-flex;
    }
    .choices {
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .out {
      max-width: 16rem;
    }
    .picks {
      display: inline-flex;
      flex-wrap: wrap;
      gap: 4px;
      max-width: 12rem;
    }
    .pick {
      width: 24px;
      height: 24px;
      padding: 0;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 50%;
      cursor: pointer;
    }
    .preview,
    .checks {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-wrap: wrap;
      gap: 8px 16px;
    }
    .preview li {
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .checks {
      flex-direction: column;
    }
    .checks li {
      display: flex;
      gap: 8px;
      align-items: flex-start;
    }
  `,
})
export class PaletteEditor implements HasUnsavedChanges {
  /** The palette's id, from the address; none for a new one. */
  readonly id = input<string | undefined>(undefined);
  /** A palette a new one is copied from (`?copy=`). */
  readonly copy = input<string | undefined>(undefined);

  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly title = inject(PageTitle);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly confirmer = inject(Confirmer);
  private readonly document = inject(DOCUMENT);
  private readonly library = inject(PaletteLibrary);
  protected readonly nameField = viewChild<ElementRef<HTMLInputElement>>('nameInput');

  protected readonly bases = paletteBases;
  protected readonly matchings = matchings;

  /** The palette as it was last saved (or read); none while it is new. */
  protected readonly saved = signal<PaletteDto | null>(null);
  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly definition = signal<PaletteDefinition>(newPalette(paletteBases[0].colors));
  protected readonly baseId = signal<string | null>(paletteBases[0].id);
  /** What was saved (or read, or started from): there are changes when what is shown isn't it. */
  private readonly baseline = signal('');
  protected readonly saving = signal(false);
  protected readonly nameProblem = signal<string | null>(null);
  protected readonly saveProblem = signal<{ text: string; conflict: boolean } | null>(null);
  /** What the server refused, by field. */
  private readonly refusals = signal<Readonly<Record<string, readonly string[]>>>({});
  /** Labels found in the data, to preview. */
  private readonly found = signal<readonly FoundLabel[]>([]);
  protected readonly tried = signal('');

  protected readonly read = rxResource({
    params: () => {
      const id = this.id() ?? this.copy();
      return id ? Number(id) : undefined;
    },
    stream: ({ params }) =>
      params ? this.api.get('/api/palettes/{id}', { path: { id: params } }) : EMPTY,
  });

  protected readonly loadProblem = computed(() => {
    const error = this.read.error();
    return error ? `Couldn't read the palette: ${problemMessage(problemOf(error))}` : null;
  });

  protected readonly canEdit = computed(() => this.saved()?.canEdit ?? true);

  private readonly shown = computed(() =>
    JSON.stringify([this.name().trim(), this.description().trim(), this.definition()]),
  );

  protected readonly dirty = computed(() => this.shown() !== this.baseline());

  /** Whether colours were moved or removed since saved, which (by label) changes labels' colours. */
  protected readonly reshuffled = computed(() => {
    const before = this.saved()?.definition.colors ?? [];
    const now = this.definition().colors;
    return (
      this.definition().assign === 'label' &&
      before.length > 0 &&
      !before.every((c, i) => now[i]?.light === c.light)
    );
  });

  protected readonly triedAs = computed(() =>
    normalizeLabel(this.tried(), this.definition().matching),
  );

  /** What is wrong with each override as written: nothing left to match, or what another matches already. */
  protected readonly overrideProblems = computed(() => {
    const matching = this.definition().matching;
    const seen = new Map<string, number>();
    return this.definition().overrides.map((item, i) => {
      const refused = this.refusal(`overrides[${i}].label`);
      if (refused) {
        return refused;
      }
      if (item.label !== null && item.label.trim() === '') {
        return 'Write the label (or tick No value)';
      }
      const key = item.label === null ? '\u0000' : normalizeLabel(item.label, matching);
      if (key === '') {
        return 'Nothing of it is left to match, as labels are matched';
      }
      const earlier = seen.get(key);
      if (earlier !== undefined) {
        return `It matches what label ${earlier + 1} matches`;
      }
      seen.set(key, i);
      return null;
    });
  });

  protected readonly warnings = computed(() =>
    colorWarnings(this.definition().colors, this.definition().assign),
  );

  /** The labels it colours (its own, and those found in the data), light and dark, and why. */
  protected readonly preview = computed(() => {
    const definition = this.definition();
    const view = chartPalette({ id: 0, hash: 'preview', definition });
    const labels = new Map<string, FoundLabel>();
    for (const item of definition.overrides) {
      labels.set(JSON.stringify(item.label), {
        label: item.label,
        shown: this.labelOf(item.label),
        rows: 0,
      });
    }
    for (const item of this.found()) {
      labels.set(JSON.stringify(item.label), item);
    }
    const entities = [...labels.entries()].map(([entity, item]) => ({
      entity,
      label: item.label,
      shown: item.shown,
    }));
    const light = new PaletteMemory(view).assign(entities, fallbackTheme(false), null);
    const dark = new PaletteMemory(view).assign(entities, fallbackTheme(true), null);
    return light.map((item, i) => ({
      shown: item.shown,
      light: item.color,
      dark: dark[i].color,
      source: sources[item.source],
    }));
  });

  constructor() {
    effect(() => {
      const id = this.id();
      const read = this.read.hasValue() ? this.read.value() : undefined;
      untracked(() => {
        if (read && id) {
          this.load(read);
        } else if (read && !id) {
          // A copy: its colours and ways, a new palette of the user's.
          this.saved.set(null);
          this.name.set(`${read.name} (copy)`);
          this.description.set(read.description ?? '');
          this.definition.set(read.definition);
          this.baseId.set(null);
          this.baseline.set('');
        } else if (!id && !this.copy()) {
          this.baseline.set(this.shown());
        }
      });
    });
    effect(() => {
      // Unnamed, the page's own title says it is new ("New palette").
      const name = this.name().trim();
      const unsaved = name ? `${name} (not saved)` : 'Not saved';
      this.title.detail.set(this.dirty() ? unsaved : name || null);
    });
    warnBeforeUnload(() => this.hasUnsavedChanges());
    inject(DestroyRef).onDestroy(() => this.title.detail.set(null));
  }

  hasUnsavedChanges(): boolean {
    return this.dirty() && !this.saving() && this.canEdit();
  }

  protected usedBy(palette: PaletteDto): string {
    if (palette.usedBy === 0) {
      return 'No dashboard uses it yet.';
    }
    const named = palette.dashboards.map((d) => d.name);
    const seen =
      named.length > 0 ? ` (${named.slice(0, 5).join(', ')}${named.length > 5 ? ', …' : ''})` : '';
    return `Used by ${palette.usedBy} ${palette.usedBy === 1 ? 'dashboard' : 'dashboards'}${seen}: changes show in them at once.`;
  }

  /** The name of the palette's colours offered for a label. */
  protected picksOf(label: string | null): string {
    return `The palette's colours for ${this.labelOf(label)}`;
  }

  protected labelOf(label: string | null): string {
    return label === null ? '(no value)' : label || '(a label)';
  }

  protected refusal(path: string): string | null {
    return this.refusals()[`definition.${path}`]?.[0] ?? null;
  }

  protected startFrom(id: string): void {
    const base = paletteBases.find((b) => b.id === id);
    if (base) {
      this.baseId.set(id);
      this.set({ colors: base.colors.map((c) => ({ ...c })) });
    }
  }

  protected set(change: Partial<PaletteDefinition>): void {
    this.definition.set({ ...this.definition(), ...change });
    // What the server refused was of what was sent; changed, it says it again (or not) at the next save.
    this.refusals.set({});
  }

  protected match(key: keyof LabelMatching, on: boolean): void {
    this.set({ matching: { ...this.definition().matching, [key]: on } });
  }

  protected setColor(index: number, change: Partial<PaletteColor>): void {
    this.set({
      colors: this.definition().colors.map((c, i) => (i === index ? { ...c, ...change } : c)),
    });
  }

  protected addColor(): void {
    const colors = this.definition().colors;
    const next = paletteBases[0].colors[colors.length % paletteBases[0].colors.length];
    this.set({ colors: [...colors, { ...next }] });
  }

  protected moveColor(index: number, by: number): void {
    const colors = [...this.definition().colors];
    const [moved] = colors.splice(index, 1);
    colors.splice(index + by, 0, moved);
    this.set({ colors });
    this.announcer.announce(`Colour ${index + 1} is colour ${index + by + 1} now`);
  }

  protected removeColor(index: number): void {
    this.set({ colors: this.definition().colors.filter((_, i) => i !== index) });
  }

  protected setOverride(
    index: number,
    change: Partial<PaletteDefinition['overrides'][number]>,
  ): void {
    this.set({
      overrides: this.definition().overrides.map((o, i) => (i === index ? { ...o, ...change } : o)),
    });
  }

  protected addOverride(label: string | null): void {
    this.set({
      overrides: [
        ...this.definition().overrides,
        // Its own colour, the same on dark backgrounds till another is chosen.
        { label: label ?? '', color: { light: this.definition().colors[0].light, dark: null } },
      ],
    });
  }

  protected removeOverride(index: number): void {
    this.set({ overrides: this.definition().overrides.filter((_, i) => i !== index) });
  }

  /** Labels found in the data, each with the colour it gets now (to change), unless it has one of its own already. */
  protected addFound(found: readonly FoundLabel[]): void {
    const definition = this.definition();
    const view = chartPalette({ id: 0, hash: 'found', definition });
    const matching = definition.matching;
    const have = new Set(
      definition.overrides.map((o) =>
        o.label === null ? '\u0000' : normalizeLabel(o.label, matching),
      ),
    );
    const fresh = found.filter(
      (f) => !have.has(f.label === null ? '\u0000' : normalizeLabel(f.label, matching)),
    );
    const theme = fallbackTheme(false);
    const colored = new PaletteMemory(view).assign(
      fresh.map((f) => ({ entity: JSON.stringify(f.label), label: f.label, shown: f.shown })),
      theme,
      null,
    );
    const darkColored = new PaletteMemory(view).assign(
      fresh.map((f) => ({ entity: JSON.stringify(f.label), label: f.label, shown: f.shown })),
      fallbackTheme(true),
      null,
    );
    this.set({
      overrides: [
        ...definition.overrides,
        ...fresh.map((f, i) => ({
          label: f.label,
          color: {
            light: colored[i].color,
            dark: darkColored[i].color === colored[i].color ? null : darkColored[i].color,
          },
        })),
      ],
    });
    this.found.set([...this.found(), ...found]);
    this.announcer.announce(
      `${fresh.length} ${fresh.length === 1 ? 'label' : 'labels'} added with the colours they had`,
    );
  }

  /** Ctrl+S saves. */
  protected keys(event: KeyboardEvent): void {
    const mac = /Mac|iPhone|iPad/.test(this.document.defaultView?.navigator.platform ?? '');
    if ((mac ? event.metaKey : event.ctrlKey) && !event.altKey && event.key.toLowerCase() === 's') {
      event.preventDefault();
      void this.save();
    }
  }

  protected async save(): Promise<void> {
    if (this.saving() || !this.canEdit()) {
      return;
    }
    const name = this.name().trim();
    if (!name) {
      this.nameProblem.set('Name the palette');
      this.nameField()?.nativeElement.focus();
      return;
    }
    const saved = this.saved();
    const body = {
      name,
      description: this.description().trim() || null,
      definition: this.definition(),
    };
    this.saving.set(true);
    this.nameProblem.set(null);
    this.saveProblem.set(null);
    this.refusals.set({});
    try {
      const palette = saved
        ? await firstValueFrom(
            this.api.put('/api/palettes/{id}', {
              path: { id: saved.id },
              body: { ...body, version: saved.version },
            }),
          )
        : await firstValueFrom(this.api.post('/api/palettes', { body }));
      this.load(palette);
      this.library.put(palette);
      this.announcer.announce(`${name} is saved`);
      if (!saved) {
        void this.router.navigate(['/dashboards/palettes', palette.id], { replaceUrl: true });
      }
    } catch (error) {
      this.refused(problemOf(error));
    } finally {
      this.saving.set(false);
    }
  }

  protected async readAgain(): Promise<void> {
    const id = this.saved()?.id;
    if (id === undefined) {
      return;
    }
    try {
      const read = await firstValueFrom(this.api.get('/api/palettes/{id}', { path: { id } }));
      // What saving goes over is what is there now; the changes shown stay.
      this.saved.set(read);
      this.library.put(read);
      this.saveProblem.set(null);
      this.announcer.announce('Read again: saving now saves over it');
    } catch (error) {
      this.saveProblem.set({
        text: `Couldn't read it again: ${problemMessage(problemOf(error))}`,
        conflict: true,
      });
    }
  }

  protected async saveCopy(): Promise<void> {
    this.saving.set(true);
    try {
      const copy = await firstValueFrom(
        this.api.post('/api/palettes', {
          body: {
            name: `${this.name().trim() || 'Palette'} (copy)`,
            description: this.description().trim() || null,
            definition: this.definition(),
          },
        }),
      );
      this.load(copy);
      this.library.put(copy);
      void this.router.navigate(['/dashboards/palettes', copy.id], { replaceUrl: true });
    } catch (error) {
      this.refused(problemOf(error));
    } finally {
      this.saving.set(false);
    }
  }

  protected async remove(): Promise<void> {
    const saved = this.saved();
    if (!saved) {
      return;
    }
    const sure = await this.confirmer.confirm({
      title: `Delete ${saved.name}?`,
      message:
        saved.usedBy > 0
          ? `${saved.usedBy} ${saved.usedBy === 1 ? 'dashboard uses' : 'dashboards use'} it: their charts get their default colours.`
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
          path: { id: saved.id },
          query: { version: saved.version },
        }),
      );
      this.library.removed(saved.id);
      this.baseline.set(this.shown());
      void this.router.navigate(['/dashboards/palettes']);
    } catch (error) {
      this.refused(problemOf(error));
    }
  }

  private load(palette: PaletteDto): void {
    this.saved.set(palette);
    this.name.set(palette.name);
    this.description.set(palette.description ?? '');
    this.definition.set(palette.definition);
    this.baseId.set(null);
    this.baseline.set(this.shown());
  }

  private refused(problem: Problem): void {
    if (problem.code === 'palette-name-taken') {
      this.nameProblem.set('You have a palette of this name already');
      this.nameField()?.nativeElement.focus();
      return;
    }
    if (problem.code === ProblemCode.concurrencyConflict) {
      this.saveProblem.set({
        text: 'The palette was changed elsewhere since it was read: read it again (your changes stay) to save over it, or save yours as a copy.',
        conflict: true,
      });
      return;
    }
    const errors = problem.errors ?? {};
    if (Object.keys(errors).length > 0) {
      this.refusals.set(errors);
      if (errors['name']) {
        this.nameProblem.set(errors['name'][0] ?? 'Name the palette');
      }
      const placed = (field: string) =>
        field === 'name' ||
        /^definition\.(colors|overrides)\[\d+\]\.(light|dark|label|color)/.test(field);
      const rest = Object.entries(errors)
        .filter(([field]) => !placed(field))
        .map(([, messages]) => messages[0]);
      this.saveProblem.set({
        text: `Couldn't save: ${rest.length > 0 ? rest.join(' ') : 'some of it isn’t right (each says what).'}`,
        conflict: false,
      });
      return;
    }
    this.saveProblem.set({ text: `Couldn't save: ${problemMessage(problem)}`, conflict: false });
  }
}
