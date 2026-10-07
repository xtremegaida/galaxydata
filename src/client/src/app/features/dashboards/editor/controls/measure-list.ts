import { ChangeDetectionStrategy, Component, computed, inject, input, model } from '@angular/core';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatSelect } from '@angular/material/select';
import type { Measure } from '../../model/definition';
import { rowCount } from '../../model/widget-defaults';
import { EntityCatalog, type FieldUse } from '../entity-catalog';
import { type ChosenField, FieldPicker, humanize } from './field-picker';

type Aggregate = Measure['aggregate'];

const aggregates: readonly { aggregate: Aggregate; label: string; use: FieldUse | null }[] = [
  { aggregate: 'count', label: 'Count of rows', use: null },
  { aggregate: 'countValues', label: 'Count of values', use: 'any' },
  { aggregate: 'countDistinct', label: 'Count of distinct values', use: 'any' },
  { aggregate: 'sum', label: 'Sum', use: 'number' },
  { aggregate: 'avg', label: 'Average', use: 'number' },
  { aggregate: 'min', label: 'Least', use: 'ordered' },
  { aggregate: 'max', label: 'Greatest', use: 'ordered' },
];

/** A measure's label as made from what it is: "Rows", "Sum of total", "Distinct customer". */
export function measureLabel(aggregate: Aggregate, fieldLabel: string | null): string {
  if (aggregate === 'count' || !fieldLabel) {
    return 'Rows';
  }
  const of = fieldLabel.toLowerCase();
  switch (aggregate) {
    case 'countValues':
      return `Count of ${of}`;
    case 'countDistinct':
      return `Distinct ${of}`;
    case 'sum':
      return `Sum of ${of}`;
    case 'avg':
      return `Average ${of}`;
    case 'min':
      return `Least ${of}`;
    case 'max':
      return `Greatest ${of}`;
  }
}

/**
 * What a chart or table measures: one or more aggregates (counts of rows, of values, of distinct values; sums and
 * averages of numbers; the least and greatest of what is ordered), each of a field but rows' counts, and labelled
 * (from what it is, unless typed).
 */
@Component({
  selector: 'gd-measure-list',
  imports: [
    FieldPicker,
    MatButton,
    MatFormField,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
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
        @if (useOf(measure.aggregate); as use) {
          <gd-field-picker
            label="Of"
            [entity]="entity()"
            [use]="use"
            [field]="measure.field"
            (chosen)="chose(i, $event)"
          />
        }
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Label</mat-label>
          <input matInput [value]="measure.label" (input)="named(i, $event)" autocomplete="off" />
        </mat-form-field>
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
    .measure {
      display: grid;
      gap: 8px;
      position: relative;
      margin: 0 0 12px;
      padding: 8px 12px 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: var(--mat-sys-corner-small);
    }
    legend {
      font: var(--mat-sys-label-large);
      padding: 0 4px;
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
  protected readonly max = 10;

  readonly entity = input.required<string | null>();
  /** Whether there is one measure only (a pie's; a bar chart's by a series). */
  readonly single = input(false);
  readonly measures = model.required<readonly Measure[]>();

  private readonly labelsGiven = computed(() =>
    this.measures().map((m) => {
      const column = this.catalog.columnOf(this.entity(), m.field);
      return measureLabel(m.aggregate, column ? column.label || humanize(column.name) : null);
    }),
  );

  protected useOf(aggregate: Aggregate): FieldUse | null {
    return aggregates.find((a) => a.aggregate === aggregate)?.use ?? null;
  }

  protected aggregated(index: number, aggregate: Aggregate): void {
    const measure = this.measures()[index];
    const field = this.useOf(aggregate) ? measure.field : null;
    const column = this.catalog.columnOf(this.entity(), field);
    this.change(index, {
      aggregate,
      field,
      label: this.given(index)
        ? measureLabel(aggregate, column ? column.label || humanize(column.name) : null)
        : measure.label,
    });
  }

  protected chose(index: number, chosen: ChosenField): void {
    const measure = this.measures()[index];
    this.change(index, {
      field: chosen.field,
      label: this.given(index) ? measureLabel(measure.aggregate, chosen.label) : measure.label,
    });
  }

  protected named(index: number, event: Event): void {
    this.change(index, { label: (event.target as HTMLInputElement).value });
  }

  protected add(): void {
    this.measures.set([...this.measures(), rowCount()]);
  }

  protected remove(index: number): void {
    this.measures.set(this.measures().filter((_, i) => i !== index));
  }

  /** Whether a measure's label is the one it was given (not typed): it follows what the measure is. */
  private given(index: number): boolean {
    const label = this.measures()[index].label;
    return !label || label === this.labelsGiven()[index] || label === 'Rows';
  }

  private change(index: number, change: Partial<Measure>): void {
    this.measures.set(this.measures().map((m, i) => (i === index ? { ...m, ...change } : m)));
  }
}
