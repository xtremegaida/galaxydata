import { ChangeDetectionStrategy, Component, computed, inject, input, model } from '@angular/core';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatSelect } from '@angular/material/select';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import type { Measure, MeasureTerm, WidgetCondition } from '../../model/definition';
import { rowCount } from '../../model/widget-defaults';
import { EntityCatalog, type EntityColumn, type FieldUse, fieldAllowed } from '../entity-catalog';
import { ConditionList } from './condition-editor';
import { type ChosenField, FieldPicker, humanize } from './field-picker';
import { NumberFormatEditor, keptFormat } from './number-format';

type Aggregate = Measure['aggregate'];

/** How a source says a column's values are summed up, as a measure's aggregate. */
const hinted: Readonly<Record<NonNullable<EntityColumn['aggregate']>, Aggregate>> = {
  sum: 'sum',
  avg: 'avg',
  min: 'min',
  max: 'max',
  countDistinct: 'countDistinct',
};

/**
 * The measures a source's entity suggests: one for each of its columns whose source says how its values are summed
 * up (a count of events kept for each visit, added up), as it says, when the column is of a type that aggregate
 * takes (the greatest of a yes-or-no isn't one).
 */
export function suggestedMeasures(columns: readonly EntityColumn[]): Measure[] {
  return columns
    .filter((c) => {
      const use = c.aggregate
        ? aggregates.find((a) => a.aggregate === hinted[c.aggregate!])?.use
        : null;
      return !c.hidden && !!use && fieldAllowed(c.type, use);
    })
    .map((c) => {
      const aggregate = hinted[c.aggregate!];
      return {
        aggregate,
        field: { path: [], column: c.name },
        label: measureLabel(aggregate, c.label || humanize(c.name), c.type.kind === 'boolean'),
        format: null,
      };
    });
}

const aggregates: readonly { aggregate: Aggregate; label: string; use: FieldUse | null }[] = [
  { aggregate: 'count', label: 'Count of rows', use: null },
  { aggregate: 'countValues', label: 'Count of values', use: 'any' },
  { aggregate: 'countDistinct', label: 'Count of distinct values', use: 'any' },
  // Within about 1% on ClickHouse, in a few kilobytes, where an exact count holds every value; exact elsewhere.
  { aggregate: 'approxCountDistinct', label: 'Count of distinct values (approximate)', use: 'any' },
  { aggregate: 'sum', label: 'Sum', use: 'number' },
  // Of true/false values, the share that are true.
  { aggregate: 'avg', label: 'Average (of yes/no: the share yes)', use: 'average' },
  { aggregate: 'min', label: 'Least', use: 'ordered' },
  { aggregate: 'max', label: 'Greatest', use: 'ordered' },
];

function useOf(aggregate: Aggregate): FieldUse | null {
  return aggregates.find((a) => a.aggregate === aggregate)?.use ?? null;
}

/** A measure's label as made from what it is: "Rows", "Sum of total", "Distinct customer", "Bounced (share)". */
export function measureLabel(
  aggregate: Aggregate,
  fieldLabel: string | null,
  boolean = false,
): string {
  if (aggregate === 'count' || !fieldLabel) {
    return 'Rows';
  }
  const of = fieldLabel.toLowerCase();
  switch (aggregate) {
    case 'countValues':
      return `Count of ${of}`;
    case 'countDistinct':
      return `Distinct ${of}`;
    case 'approxCountDistinct':
      return `Distinct ${of} (approx.)`;
    case 'sum':
      return `Sum of ${of}`;
    case 'avg':
      return boolean ? `${fieldLabel} (share)` : `Average ${of}`;
    case 'min':
      return `Least ${of}`;
    case 'max':
      return `Greatest ${of}`;
  }
}

/**
 * What a chart or table measures: one or more aggregates (counts of rows, of values, of distinct values; sums and
 * averages of numbers, the share of true/false values that are true; the least and greatest of what is ordered),
 * each of a field but rows' counts, of the rows its conditions keep, and divided by another aggregate when it is a
 * ratio; labelled (from what it is, unless typed), and shown as numbers, percentages or durations. A measure that
 * counts rows offers those its source suggests in its place.
 */
@Component({
  selector: 'gd-measure-list',
  imports: [
    ConditionList,
    FieldPicker,
    MatButton,
    MatFormField,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
    MatSlideToggle,
    NumberFormatEditor,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (measure of measures(); track $index; let i = $index) {
      <fieldset class="measure">
        <legend>{{ single() ? 'Measure' : 'Measure ' + (i + 1) }}</legend>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Aggregate</mat-label>
          <mat-select [value]="measure.aggregate" (valueChange)="aggregated(i, $event)">
            @for (choice of aggregates; track choice.aggregate) {
              <mat-option [value]="choice.aggregate">{{ choice.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        @if (measure.aggregate === 'count' && suggested().length > 0) {
          <div class="suggested" role="group" aria-label="Suggested measures">
            @for (suggestion of suggested(); track suggestion.field!.column) {
              <button matButton type="button" (click)="suggest(i, suggestion)">
                {{ suggestion.label }}
              </button>
            }
          </div>
        }
        @if (useOf(measure.aggregate); as use) {
          <gd-field-picker
            label="Of"
            [entity]="entity()"
            [use]="use"
            [field]="measure.field"
            (chosen)="chose(i, $event)"
          />
        }
        <gd-condition-list
          legend="Row condition"
          add="Only rows where…"
          [entity]="entity()"
          [conditions]="measure.where ?? []"
          (conditionsChange)="change(i, { where: kept($event) })"
        />
        <mat-slide-toggle [checked]="!!measure.per" (change)="divided(i, $event.checked)">
          Divided by another aggregate (a rate or ratio)
        </mat-slide-toggle>
        @if (measure.per; as per) {
          <fieldset class="per">
            <legend>Divided by</legend>
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Aggregate</mat-label>
              <mat-select [value]="per.aggregate" (valueChange)="perAggregated(i, $event)">
                @for (choice of aggregates; track choice.aggregate) {
                  <mat-option [value]="choice.aggregate">{{ choice.label }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            @if (useOf(per.aggregate); as use) {
              <gd-field-picker
                label="Of"
                [entity]="entity()"
                [use]="use"
                [field]="per.field"
                (chosen)="changePer(i, { field: $event.field })"
              />
            }
            <gd-condition-list
              legend="Row condition"
              add="Only rows where…"
              [entity]="entity()"
              [conditions]="per.where ?? []"
              (conditionsChange)="changePer(i, { where: kept($event) })"
            />
          </fieldset>
        }
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Label</mat-label>
          <input matInput [value]="measure.label" (input)="named(i, $event)" autocomplete="off" />
        </mat-form-field>
        <gd-number-format
          [format]="measure.format"
          (formatChange)="change(i, { format: $event })"
        />
        @if (measures().length > 1) {
          <button
            matIconButton
            type="button"
            class="remove"
            [attr.aria-label]="'Remove ' + measure.label"
            (click)="remove(i)"
          >
            <mat-icon>delete</mat-icon>
          </button>
        }
      </fieldset>
    }
    @if (!single() && measures().length < max) {
      <button matButton type="button" (click)="add()">
        <mat-icon>add</mat-icon>
        Add a measure
      </button>
    }
  `,
  styles: `
    .measure,
    .per {
      display: grid;
      gap: 8px;
      position: relative;
      margin: 0 0 12px;
      padding: 8px 12px 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: var(--mat-sys-corner-small);
    }
    .per {
      margin: 0;
    }
    legend {
      font: var(--mat-sys-label-large);
      padding: 0 4px;
    }
    .suggested {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }
    .remove {
      position: absolute;
      top: -8px;
      right: 0;
    }
  `,
})
export class MeasureList {
  private readonly catalog = inject(EntityCatalog);
  protected readonly aggregates = aggregates;
  protected readonly useOf = useOf;
  protected readonly max = 10;

  readonly entity = input.required<string | null>();
  /** Whether there is one measure only (a pie's; a bar chart's by a series). */
  readonly single = input(false);
  readonly measures = model.required<readonly Measure[]>();

  private readonly labelsGiven = computed(() =>
    this.measures().map((m) => {
      const column = this.catalog.columnOf(this.entity(), m.field);
      return measureLabel(
        m.aggregate,
        column ? column.label || humanize(column.name) : null,
        column?.type.kind === 'boolean',
      );
    }),
  );

  /** The measures the source's entity suggests (its own columns', not those it leads to). */
  protected readonly suggested = computed(() => {
    const name = this.entity();
    const entity = name ? this.catalog.entity(name)() : null;
    return entity ? suggestedMeasures(entity.columns) : [];
  });

  protected aggregated(index: number, aggregate: Aggregate): void {
    const measure = this.measures()[index];
    const field = useOf(aggregate) ? measure.field : null;
    const column = this.catalog.columnOf(this.entity(), field);
    const boolean = column?.type.kind === 'boolean';
    this.change(index, {
      aggregate,
      field,
      label: this.given(index)
        ? measureLabel(aggregate, column ? column.label || humanize(column.name) : null, boolean)
        : measure.label,
      format: this.shareFormat(aggregate, boolean, measure),
    });
  }

  protected chose(index: number, chosen: ChosenField): void {
    const measure = this.measures()[index];
    const boolean = chosen.type.kind === 'boolean';
    this.change(index, {
      field: chosen.field,
      label: this.given(index)
        ? measureLabel(measure.aggregate, chosen.label, boolean)
        : measure.label,
      format: this.shareFormat(measure.aggregate, boolean, measure),
    });
  }

  /** A suggested measure in place of one, its label too unless one was typed. */
  protected suggest(index: number, suggestion: Measure): void {
    this.change(index, {
      aggregate: suggestion.aggregate,
      field: suggestion.field,
      label: this.given(index) ? suggestion.label : this.measures()[index].label,
    });
  }

  protected named(index: number, event: Event): void {
    this.change(index, { label: (event.target as HTMLInputElement).value });
  }

  /** Divided by the group's rows to begin with (a share of them), or by nothing again. */
  protected divided(index: number, on: boolean): void {
    this.change(index, { per: on ? { aggregate: 'count', field: null } : null });
  }

  protected perAggregated(index: number, aggregate: Aggregate): void {
    const per = this.measures()[index].per!;
    this.changePer(index, { aggregate, field: useOf(aggregate) ? per.field : null });
  }

  protected changePer(index: number, change: Partial<MeasureTerm>): void {
    this.change(index, { per: { ...this.measures()[index].per!, ...change } });
  }

  /** Conditions as kept: none as none, as the server writes them. */
  protected kept(conditions: readonly WidgetCondition[]): WidgetCondition[] | null {
    return conditions.length > 0 ? [...conditions] : null;
  }

  protected add(): void {
    this.measures.set([...this.measures(), rowCount()]);
  }

  protected remove(index: number): void {
    this.measures.set(this.measures().filter((_, i) => i !== index));
  }

  /** A share of true values shows as a percentage, unless its format was chosen. */
  private shareFormat(aggregate: Aggregate, boolean: boolean, measure: Measure): Measure['format'] {
    return aggregate === 'avg' && boolean && !measure.format
      ? keptFormat({ decimals: null, prefix: null, suffix: null, compact: false, style: 'percent' })
      : measure.format;
  }

  /** Whether a measure's label is the one it was given (not typed): it follows what the measure is. */
  private given(index: number): boolean {
    const label = this.measures()[index].label;
    return !label || label === this.labelsGiven()[index] || label === 'Rows';
  }

  protected change(index: number, change: Partial<Measure>): void {
    this.measures.set(this.measures().map((m, i) => (i === index ? { ...m, ...change } : m)));
  }
}
