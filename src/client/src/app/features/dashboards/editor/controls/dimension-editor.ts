import { ChangeDetectionStrategy, Component, computed, inject, input, model } from '@angular/core';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { MatSelect } from '@angular/material/select';
import type { Bucket, Dimension } from '../../model/definition';
import { EntityCatalog, isDated } from '../entity-catalog';
import { type ChosenField, FieldPicker, humanize } from './field-picker';

const periods: readonly { bucket: Bucket; label: string; time?: true }[] = [
  { bucket: 'day', label: 'Day' },
  { bucket: 'week', label: 'Week (from Monday)' },
  { bucket: 'month', label: 'Month' },
  { bucket: 'quarter', label: 'Quarter' },
  { bucket: 'year', label: 'Year' },
  { bucket: 'quarterOfYear', label: 'Quarter of the year (Q1–Q4)' },
  { bucket: 'monthOfYear', label: 'Month of the year' },
  { bucket: 'dayOfWeek', label: 'Day of the week' },
  { bucket: 'hourOfDay', label: 'Hour of the day', time: true },
];

/**
 * What a chart's slices (or a table's groups) are: a field of the source, by a period when it is a date (a
 * date-time always: its instants are too many to group), and its label (the column's, unless changed).
 */
@Component({
  selector: 'gd-dimension-editor',
  imports: [FieldPicker, MatFormField, MatHint, MatInput, MatLabel, MatOption, MatSelect],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="dimension">
      <gd-field-picker
        class="pick"
        use="dimension"
        [entity]="entity()"
        [label]="label()"
        [field]="dimension().field.column ? dimension().field : null"
        (chosen)="chose($event)"
      />
      @if (dated()) {
        <mat-form-field class="period" subscriptSizing="dynamic">
          <mat-label>By</mat-label>
          <mat-select [value]="dimension().bucket" (valueChange)="set({ bucket: $event })">
            @if (!timed()) {
              <mat-option [value]="null">Each value</mat-option>
            }
            @for (period of offered(); track period.bucket) {
              <mat-option [value]="period.bucket">{{ period.label }}</mat-option>
            }
          </mat-select>
          @if (timed()) {
            <mat-hint>Times are grouped by a period</mat-hint>
          }
        </mat-form-field>
      }
      <mat-form-field class="name" subscriptSizing="dynamic">
        <mat-label>Label</mat-label>
        <input matInput [value]="dimension().label" (input)="named($event)" autocomplete="off" />
      </mat-form-field>
    </div>
  `,
  styles: `
    .dimension {
      display: grid;
      grid-template-columns: 1fr;
      gap: 8px;
    }
  `,
})
export class DimensionEditor {
  private readonly catalog = inject(EntityCatalog);

  readonly entity = input.required<string | null>();
  readonly label = input('Category');
  readonly dimension = model.required<Dimension>();

  private readonly column = computed(() =>
    this.catalog.columnOf(this.entity(), this.dimension().field),
  );
  protected readonly dated = computed(() => {
    const column = this.column();
    return !!column && isDated(column.type);
  });
  protected readonly timed = computed(() => {
    const kind = this.column()?.type.kind;
    return kind === 'dateTime' || kind === 'dateTimeOffset';
  });
  protected readonly offered = computed(() => periods.filter((p) => !p.time || this.timed()));

  protected set(change: Partial<Dimension>): void {
    this.dimension.set({ ...this.dimension(), ...change });
  }

  protected chose(chosen: ChosenField): void {
    const before = this.dimension();
    const timed = chosen.type.kind === 'dateTime' || chosen.type.kind === 'dateTimeOffset';
    this.set({
      field: chosen.field,
      // A label typed stays; one that was the column's follows the column.
      label: !before.label || before.label === this.labelBefore() ? chosen.label : before.label,
      bucket: !isDated(chosen.type) ? null : (before.bucket ?? (timed ? 'month' : null)),
    });
  }

  protected named(event: Event): void {
    this.set({ label: (event.target as HTMLInputElement).value });
  }

  /** The label the field had from its column, to tell a label typed from one given. */
  private labelBefore(): string | null {
    const column = this.column();
    return column ? column.label || humanize(column.name) : null;
  }
}
