import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatSelect } from '@angular/material/select';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import type {
  BarConfig,
  Dimension,
  LineConfig,
  Measure,
  PieConfig,
  TableConfig,
  TextConfig,
} from '../../model/definition';
import { blankDimension } from '../../model/widget-defaults';
import { DimensionEditor } from '../controls/dimension-editor';
import { FieldPicker, humanize } from '../controls/field-picker';
import { MeasureList } from '../controls/measure-list';

type Sort = BarConfig['sort'];

/** The sorts a chart or grouped table offers: by each measure, either way; by its category, either way. */
function sortsOf(
  measures: readonly Measure[],
  dimensions: readonly Dimension[],
): { sort: Sort; label: string }[] {
  return [
    ...measures.flatMap((m, index) => [
      {
        sort: { by: 'measure' as const, index, descending: true },
        label: `${m.label || 'Measure'}, largest first`,
      },
      {
        sort: { by: 'measure' as const, index, descending: false },
        label: `${m.label || 'Measure'}, smallest first`,
      },
    ]),
    ...dimensions.flatMap((d, index) => [
      {
        sort: { by: 'dimension' as const, index, descending: false },
        label: `${d.label || 'Category'}, in order`,
      },
      {
        sort: { by: 'dimension' as const, index, descending: true },
        label: `${d.label || 'Category'}, in reverse`,
      },
    ]),
  ];
}

function sortKey(sort: Sort | null): string {
  return sort ? `${sort.by}:${sort.index}:${sort.descending}` : '';
}

const settingsStyles = `
  :host {
    display: grid;
    gap: 12px;
  }
  .row {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 8px;
  }
  h3 {
    margin: 8px 0 0;
    font: var(--mat-sys-title-small);
  }
`;

/** A text widget's Markdown: headings (`#` is the page's second level), notes, lists, links. */
@Component({
  selector: 'gd-text-settings',
  imports: [MatFormField, MatHint, MatInput, MatLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field subscriptSizing="dynamic">
      <mat-label>Text, in Markdown</mat-label>
      <textarea
        matInput
        class="markdown"
        rows="12"
        [value]="config().markdown"
        (input)="config.set({ ...config(), markdown: typed($event) })"
        spellcheck="true"
      ></textarea>
      <mat-hint># Heading, **bold**, [a link](https://…), - a list</mat-hint>
    </mat-form-field>
  `,
  styles: `
    ${settingsStyles}
    .markdown {
      font-family: var(--gd-code-font-family);
    }
  `,
})
export class TextSettings {
  readonly entity = input<string | null>(null);
  readonly config = model.required<TextConfig>();

  protected typed(event: Event): string {
    return (event.target as HTMLTextAreaElement).value;
  }
}

/** The parts every chart has: a legend's place, labels on its marks, and its axes' titles (not a pie's). */
@Component({
  selector: 'gd-chart-display',
  imports: [MatFormField, MatInput, MatLabel, MatOption, MatSelect, MatSlideToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h3>Display</h3>
    <div class="row">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Legend</mat-label>
        <mat-select [value]="legend()" (valueChange)="legend.set($event)">
          <mat-option value="bottom">Below</mat-option>
          <mat-option value="top">Above</mat-option>
          <mat-option value="right">Right</mat-option>
          <mat-option value="left">Left</mat-option>
          <mat-option value="none">None</mat-option>
        </mat-select>
      </mat-form-field>
      <mat-slide-toggle [checked]="labels()" (change)="labels.set($event.checked)"
        >Labels</mat-slide-toggle
      >
    </div>
    @if (axes()) {
      <div class="row">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Category axis title</mat-label>
          <input matInput [value]="xTitle() ?? ''" (input)="xTitle.set(text($event))" />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Value axis title</mat-label>
          <input matInput [value]="yTitle() ?? ''" (input)="yTitle.set(text($event))" />
        </mat-form-field>
      </div>
    }
  `,
  styles: settingsStyles,
})
export class ChartDisplay {
  readonly legend = model.required<BarConfig['legend']>();
  readonly labels = model.required<boolean>();
  readonly xTitle = model<string | null>(null);
  readonly yTitle = model<string | null>(null);
  readonly axes = input(true);

  protected text(event: Event): string | null {
    return (event.target as HTMLInputElement).value || null;
  }
}

/** The series a bar or line chart may be split by: a second dimension, or none. */
@Component({
  selector: 'gd-series-editor',
  imports: [DimensionEditor, MatSlideToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-slide-toggle
      [checked]="series() !== null"
      (change)="series.set($event.checked ? blank() : null)"
    >
      Split by a series (one measure then)
    </mat-slide-toggle>
    @if (series(); as series) {
      <gd-dimension-editor
        label="Series"
        [entity]="entity()"
        [dimension]="series"
        (dimensionChange)="this.series.set($event)"
      />
    }
  `,
  styles: settingsStyles,
})
export class SeriesEditor {
  readonly entity = input.required<string | null>();
  readonly series = model.required<Dimension | null>();
  protected readonly blank = blankDimension;
}

/** A bar chart: its categories, series, measures, orientation, stacking, sort, how many bars, and display. */
@Component({
  selector: 'gd-bar-settings',
  imports: [
    ChartDisplay,
    DimensionEditor,
    MatFormField,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
    MeasureList,
    SeriesEditor,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <gd-dimension-editor
      [entity]="entity()"
      [dimension]="config().dimension"
      (dimensionChange)="set({ dimension: $event })"
    />
    <gd-series-editor
      [entity]="entity()"
      [series]="config().series"
      (seriesChange)="serieschanged($event)"
    />
    <gd-measure-list
      [entity]="entity()"
      [single]="config().series !== null"
      [measures]="config().measures"
      (measuresChange)="set({ measures: [...$event] })"
    />
    <div class="row">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Bars</mat-label>
        <mat-select [value]="config().orientation" (valueChange)="set({ orientation: $event })">
          <mat-option value="vertical">Upright</mat-option>
          <mat-option value="horizontal">Across</mat-option>
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Stacked</mat-label>
        <mat-select [value]="config().stack" (valueChange)="set({ stack: $event })">
          <mat-option value="none">Side by side</mat-option>
          <mat-option value="stacked">Stacked</mat-option>
          <mat-option value="percent">Stacked to 100%</mat-option>
        </mat-select>
      </mat-form-field>
    </div>
    <div class="row">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Sorted by</mat-label>
        <mat-select [value]="sortKey(config().sort)" (valueChange)="sorted($event)">
          @for (choice of sorts(); track sortKey(choice.sort)) {
            <mat-option [value]="sortKey(choice.sort)">{{ choice.label }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Categories, at most</mat-label>
        <input
          matInput
          type="number"
          min="1"
          max="1000"
          [value]="config().limit"
          (change)="limited($event)"
        />
      </mat-form-field>
    </div>
    <gd-chart-display
      [legend]="config().legend"
      (legendChange)="set({ legend: $event })"
      [labels]="config().labels"
      (labelsChange)="set({ labels: $event })"
      [xTitle]="config().xTitle"
      (xTitleChange)="set({ xTitle: $event })"
      [yTitle]="config().yTitle"
      (yTitleChange)="set({ yTitle: $event })"
    />
  `,
  styles: settingsStyles,
})
export class BarSettings {
  readonly entity = input.required<string | null>();
  readonly config = model.required<BarConfig>();
  protected readonly sortKey = sortKey;
  protected readonly sorts = computed(() =>
    sortsOf(this.config().measures, [this.config().dimension]),
  );

  protected set(change: Partial<BarConfig>): void {
    this.config.set({ ...this.config(), ...change });
  }

  protected serieschanged(series: Dimension | null): void {
    const config = this.config();
    this.set({
      series,
      measures: series ? config.measures.slice(0, 1) : config.measures,
      sort: series && config.sort.by === 'measure' ? { ...config.sort, index: 0 } : config.sort,
    });
  }

  protected sorted(key: string): void {
    const found = this.sorts().find((s) => sortKey(s.sort) === key);
    if (found) {
      this.set({ sort: found.sort });
    }
  }

  protected limited(event: Event): void {
    const limit = Math.round(Number((event.target as HTMLInputElement).value));
    if (limit >= 1 && limit <= 1000) {
      this.set({ limit });
    }
  }
}

/** A line chart: its x (a period by default), series, measures, area, gaps, how many points, and display. */
@Component({
  selector: 'gd-line-settings',
  imports: [
    ChartDisplay,
    DimensionEditor,
    MatFormField,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
    MatSlideToggle,
    MeasureList,
    SeriesEditor,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <gd-dimension-editor
      label="Along"
      [entity]="entity()"
      [dimension]="config().dimension"
      (dimensionChange)="set({ dimension: $event })"
    />
    <gd-series-editor
      [entity]="entity()"
      [series]="config().series"
      (seriesChange)="
        set({
          series: $event,
          measures: $event ? config().measures.slice(0, 1) : config().measures,
        })
      "
    />
    <gd-measure-list
      [entity]="entity()"
      [single]="config().series !== null"
      [measures]="config().measures"
      (measuresChange)="set({ measures: [...$event] })"
    />
    <div class="row">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Periods without rows</mat-label>
        <mat-select [value]="config().gaps" (valueChange)="set({ gaps: $event })">
          <mat-option value="break">A gap</mat-option>
          <mat-option value="zero">Zero</mat-option>
          <mat-option value="connect">Joined across</mat-option>
        </mat-select>
      </mat-form-field>
      <mat-slide-toggle [checked]="config().area" (change)="set({ area: $event.checked })">
        Filled below
      </mat-slide-toggle>
    </div>
    <mat-form-field subscriptSizing="dynamic">
      <mat-label>Points, at most</mat-label>
      <input
        matInput
        type="number"
        min="1"
        max="10000"
        [value]="config().limit"
        (change)="limited($event)"
      />
    </mat-form-field>
    <gd-chart-display
      [legend]="config().legend"
      (legendChange)="set({ legend: $event })"
      [labels]="config().labels"
      (labelsChange)="set({ labels: $event })"
      [xTitle]="config().xTitle"
      (xTitleChange)="set({ xTitle: $event })"
      [yTitle]="config().yTitle"
      (yTitleChange)="set({ yTitle: $event })"
    />
  `,
  styles: settingsStyles,
})
export class LineSettings {
  readonly entity = input.required<string | null>();
  readonly config = model.required<LineConfig>();

  protected set(change: Partial<LineConfig>): void {
    this.config.set({ ...this.config(), ...change });
  }

  protected limited(event: Event): void {
    const limit = Math.round(Number((event.target as HTMLInputElement).value));
    if (limit >= 1 && limit <= 10000) {
      this.set({ limit });
    }
  }
}

/** A pie: its slices, its one measure, how many slices (the rest as "Other"), a hole, and display. */
@Component({
  selector: 'gd-pie-settings',
  imports: [
    ChartDisplay,
    DimensionEditor,
    MatFormField,
    MatInput,
    MatLabel,
    MatSlideToggle,
    MeasureList,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <gd-dimension-editor
      label="Slices"
      [entity]="entity()"
      [dimension]="config().dimension"
      (dimensionChange)="set({ dimension: $event })"
    />
    <gd-measure-list
      [entity]="entity()"
      [single]="true"
      [measures]="[config().measure]"
      (measuresChange)="set({ measure: $event[0] })"
    />
    <div class="row">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Slices, at most</mat-label>
        <input
          matInput
          type="number"
          min="1"
          max="50"
          [value]="config().limit"
          (change)="limited($event)"
        />
      </mat-form-field>
      <mat-slide-toggle [checked]="config().other" (change)="set({ other: $event.checked })">
        The rest as Other
      </mat-slide-toggle>
    </div>
    <mat-slide-toggle [checked]="config().donut" (change)="set({ donut: $event.checked })">
      A hole in the middle
    </mat-slide-toggle>
    <gd-chart-display
      [axes]="false"
      [legend]="config().legend"
      (legendChange)="set({ legend: $event })"
      [labels]="config().labels"
      (labelsChange)="set({ labels: $event })"
    />
  `,
  styles: settingsStyles,
})
export class PieSettings {
  readonly entity = input.required<string | null>();
  readonly config = model.required<PieConfig>();

  protected set(change: Partial<PieConfig>): void {
    this.config.set({ ...this.config(), ...change });
  }

  protected limited(event: Event): void {
    const limit = Math.round(Number((event.target as HTMLInputElement).value));
    if (limit >= 1 && limit <= 50) {
      this.set({ limit });
    }
  }
}

/** A table: grouped (dimensions and measures) or as its rows are (columns), its sort, and a page's rows. */
@Component({
  selector: 'gd-table-settings',
  imports: [
    DimensionEditor,
    FieldPicker,
    MatButton,
    MatFormField,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
    MeasureList,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field subscriptSizing="dynamic">
      <mat-label>Rows</mat-label>
      <mat-select [value]="config().mode" (valueChange)="moded($event)">
        <mat-option value="grouped">Grouped, with measures</mat-option>
        <mat-option value="raw">As they are</mat-option>
      </mat-select>
    </mat-form-field>
    @if (config().mode === 'grouped') {
      @for (dimension of config().dimensions; track $index; let i = $index) {
        <div class="item">
          <gd-dimension-editor
            [label]="'Group ' + (i + 1)"
            [entity]="entity()"
            [dimension]="dimension"
            (dimensionChange)="setDimension(i, $event)"
          />
          @if (config().dimensions.length > 1) {
            <button
              matIconButton
              type="button"
              [attr.aria-label]="'Remove group ' + (i + 1)"
              (click)="removeDimension(i)"
            >
              <mat-icon>delete</mat-icon>
            </button>
          }
        </div>
      }
      <button
        matButton
        type="button"
        (click)="set({ dimensions: [...config().dimensions, blank()] })"
      >
        <mat-icon>add</mat-icon>
        Add a group
      </button>
      <gd-measure-list
        [entity]="entity()"
        [measures]="config().measures"
        (measuresChange)="set({ measures: [...$event] })"
      />
    } @else {
      @for (column of config().columns; track $index; let i = $index) {
        <div class="item">
          <div class="column">
            <gd-field-picker
              [label]="'Column ' + (i + 1)"
              [entity]="entity()"
              [field]="column.field.column ? column.field : null"
              (chosen)="setColumn(i, { field: $event.field, label: column.label || $event.label })"
            />
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Its label</mat-label>
              <input
                matInput
                [value]="column.label"
                (input)="setColumn(i, { label: text($event) })"
              />
            </mat-form-field>
          </div>
          <button
            matIconButton
            type="button"
            [attr.aria-label]="'Remove column ' + (i + 1)"
            (click)="removeColumn(i)"
          >
            <mat-icon>delete</mat-icon>
          </button>
        </div>
      }
      <button
        matButton
        type="button"
        (click)="
          set({
            columns: [
              ...config().columns,
              { field: { path: [], column: '' }, label: '', format: null },
            ],
          })
        "
      >
        <mat-icon>add</mat-icon>
        Add a column
      </button>
    }
    <div class="row">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Sorted by</mat-label>
        <mat-select [value]="sortKey(config().sort)" (valueChange)="sorted($event)">
          @for (choice of sorts(); track sortKey(choice.sort)) {
            <mat-option [value]="sortKey(choice.sort)">{{ choice.label }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Rows a page</mat-label>
        <input
          matInput
          type="number"
          min="1"
          max="1000"
          [value]="config().pageSize"
          (change)="paged($event)"
        />
      </mat-form-field>
    </div>
  `,
  styles: `
    ${settingsStyles}
    .item {
      display: grid;
      grid-template-columns: 1fr auto;
      align-items: start;
      gap: 4px;
    }
    .column {
      display: grid;
      gap: 8px;
    }
  `,
})
export class TableSettings {
  readonly entity = input.required<string | null>();
  readonly config = model.required<TableConfig>();
  protected readonly sortKey = sortKey;
  protected readonly blank = blankDimension;

  protected readonly sorts = computed(() => {
    const config = this.config();
    if (config.mode === 'grouped') {
      return sortsOf(config.measures, config.dimensions);
    }
    return config.columns.flatMap((c, index) => [
      {
        sort: { by: 'column' as const, index, descending: false },
        label: `${c.label || 'Column'}, in order`,
      },
      {
        sort: { by: 'column' as const, index, descending: true },
        label: `${c.label || 'Column'}, in reverse`,
      },
    ]);
  });

  protected set(change: Partial<TableConfig>): void {
    this.config.set({ ...this.config(), ...change });
  }

  protected text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  /** Grouped and raw tables have their own parts: switching keeps what fits the other. */
  protected moded(mode: TableConfig['mode']): void {
    const config = this.config();
    if (mode === 'raw') {
      const columns =
        config.columns.length > 0
          ? config.columns
          : config.dimensions
              .filter((d) => d.field.column)
              .map((d) => ({
                field: d.field,
                label: d.label || humanize(d.field.column),
                format: null,
              }));
      this.set({
        mode,
        columns,
        dimensions: [],
        measures: [],
        sort: { by: 'column', index: 0, descending: false },
      });
    } else {
      this.set({
        mode,
        dimensions: config.dimensions.length > 0 ? config.dimensions : [blankDimension()],
        measures:
          config.measures.length > 0
            ? config.measures
            : [{ aggregate: 'count', field: null, label: 'Rows', format: null }],
        sort: { by: 'measure', index: 0, descending: true },
      });
    }
  }

  protected setDimension(index: number, dimension: Dimension): void {
    this.set({ dimensions: this.config().dimensions.map((d, i) => (i === index ? dimension : d)) });
  }

  protected removeDimension(index: number): void {
    this.set({ dimensions: this.config().dimensions.filter((_, i) => i !== index) });
  }

  protected setColumn(index: number, change: Partial<TableConfig['columns'][number]>): void {
    this.set({
      columns: this.config().columns.map((c, i) => (i === index ? { ...c, ...change } : c)),
    });
  }

  protected removeColumn(index: number): void {
    this.set({ columns: this.config().columns.filter((_, i) => i !== index) });
  }

  protected sorted(key: string): void {
    const found = this.sorts().find((s) => sortKey(s.sort) === key);
    if (found) {
      this.set({ sort: found.sort });
    }
  }

  protected paged(event: Event): void {
    const pageSize = Math.round(Number((event.target as HTMLInputElement).value));
    if (pageSize >= 1 && pageSize <= 1000) {
      this.set({ pageSize });
    }
  }
}
