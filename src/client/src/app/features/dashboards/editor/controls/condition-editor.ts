import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatSelect } from '@angular/material/select';
import type { ConditionValue, FieldRef } from '../../model/definition';
import { FieldPicker } from './field-picker';

type Op = ConditionValue['op'];
type RelativeRange = NonNullable<ConditionValue['relative']>;

const opNames: Readonly<Record<Op, string>> = {
  eq: 'is',
  ne: "isn't",
  lt: 'is below',
  le: 'is at most',
  gt: 'is above',
  ge: 'is at least',
  between: 'is between',
  contains: 'contains',
  notContains: "doesn't contain",
  startsWith: 'starts with',
  endsWith: 'ends with',
  blank: 'is blank',
  notBlank: "isn't blank",
  in: 'is one of',
  notIn: 'is none of',
  relative: 'is in the period',
};

/** Every op, in the order offered. */
export const allOps = Object.keys(opNames) as Op[];

/** The ops a filter of a kind has. */
export const filterOps: Readonly<Record<string, readonly Op[]>> = {
  values: ['in', 'notIn'],
  range: ['between', 'ge', 'gt', 'le', 'lt'],
  relative: ['relative'],
  text: ['contains', 'startsWith'],
  boolean: ['eq'],
};

/** A condition value with its op changed: what it had kept where the op takes it. */
export function withOp(value: ConditionValue, op: Op): ConditionValue {
  switch (op) {
    case 'blank':
    case 'notBlank':
      return { op };
    case 'in':
    case 'notIn':
      return {
        op,
        values:
          value.values ?? (value.value !== undefined && value.value !== null ? [value.value] : []),
      };
    case 'between':
      return { op, value: value.value ?? '', valueTo: value.valueTo ?? '' };
    case 'relative':
      return { op, relative: value.relative ?? { mode: 'last', count: 7, unit: 'day' } };
    default:
      return { op, value: value.value ?? value.values?.[0] ?? '' };
  }
}

/** A list of values as typed: `,` between them. */
function valuesOf(text: string): string[] {
  return text
    .split(',')
    .map((v) => v.trim())
    .filter((v) => v !== '');
}

/**
 * A condition's value: its op (of those allowed), and the values it takes, typed as the field's values are written
 * (`2026-01-31`, `12.5`, `true`); lists with `,` between their values; periods relative to today.
 */
@Component({
  selector: 'gd-condition-value',
  imports: [MatFormField, MatHint, MatInput, MatLabel, MatOption, MatSelect],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="value">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>{{ label() }}</mat-label>
        <mat-select [value]="value().op" (valueChange)="value.set(withOp(value(), $event))">
          @for (op of ops(); track op) {
            <mat-option [value]="op">{{ names[op] }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      @switch (shape()) {
        @case ('one') {
          @if (boolean()) {
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Value</mat-label>
              <mat-select [value]="value().value" (valueChange)="set({ value: $event })">
                <mat-option [value]="true">Yes</mat-option>
                <mat-option [value]="false">No</mat-option>
              </mat-select>
            </mat-form-field>
          } @else {
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Value</mat-label>
              <input
                matInput
                [value]="text(value().value)"
                (input)="set({ value: typed($event) })"
              />
            </mat-form-field>
          }
        }
        @case ('two') {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>From</mat-label>
            <input matInput [value]="text(value().value)" (input)="set({ value: typed($event) })" />
          </mat-form-field>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>To</mat-label>
            <input
              matInput
              [value]="text(value().valueTo)"
              (input)="set({ valueTo: typed($event) })"
            />
          </mat-form-field>
        }
        @case ('list') {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Values</mat-label>
            <input
              matInput
              [value]="(value().values ?? []).map(text).join(', ')"
              (change)="set({ values: list($event) })"
            />
            <mat-hint>With , between them</mat-hint>
          </mat-form-field>
        }
        @case ('period') {
          <div class="period">
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Period</mat-label>
              <mat-select [value]="value().relative!.mode" (valueChange)="period({ mode: $event })">
                <mat-option value="last">The last</mat-option>
                <mat-option value="this">This</mat-option>
                <mat-option value="previous">The previous</mat-option>
                <mat-option value="toDate">To date: this</mat-option>
              </mat-select>
            </mat-form-field>
            @if (value().relative!.mode === 'last' || value().relative!.mode === 'previous') {
              <mat-form-field subscriptSizing="dynamic" class="count">
                <mat-label>How many</mat-label>
                <input
                  matInput
                  type="number"
                  min="1"
                  max="1000"
                  [value]="value().relative!.count"
                  (input)="counted($event)"
                />
              </mat-form-field>
            }
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Of</mat-label>
              <mat-select [value]="value().relative!.unit" (valueChange)="period({ unit: $event })">
                <mat-option value="day">days</mat-option>
                <mat-option value="week">weeks</mat-option>
                <mat-option value="month">months</mat-option>
                <mat-option value="quarter">quarters</mat-option>
                <mat-option value="year">years</mat-option>
              </mat-select>
            </mat-form-field>
          </div>
        }
      }
    </div>
  `,
  styles: `
    .value,
    .period {
      display: grid;
      gap: 8px;
    }
  `,
})
export class ConditionValueEditor {
  protected readonly names = opNames;
  protected readonly withOp = withOp;

  readonly value = model.required<ConditionValue>();
  readonly ops = input<readonly Op[]>(allOps);
  readonly label = input('Condition');
  /** Whether the values are yes or no. */
  readonly boolean = input(false);

  protected readonly shape = computed(() => {
    switch (this.value().op) {
      case 'blank':
      case 'notBlank':
        return 'none';
      case 'between':
        return 'two';
      case 'in':
      case 'notIn':
        return 'list';
      case 'relative':
        return 'period';
      default:
        return 'one';
    }
  });

  protected text(value: unknown): string {
    return value === null || value === undefined ? '' : String(value);
  }

  protected typed(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected list(event: Event): string[] {
    return valuesOf((event.target as HTMLInputElement).value);
  }

  protected set(change: Partial<ConditionValue>): void {
    this.value.set({ ...this.value(), ...change });
  }

  protected period(change: Partial<RelativeRange>): void {
    this.set({ relative: { ...this.value().relative!, ...change } });
  }

  protected counted(event: Event): void {
    const count = Math.round(Number((event.target as HTMLInputElement).value));
    if (count >= 1 && count <= 1000) {
      this.period({ count });
    }
  }
}

/** A widget's own conditions: on its source's fields, each of them kept. */
@Component({
  selector: 'gd-condition-list',
  imports: [ConditionValueEditor, FieldPicker, MatButton, MatIcon, MatIconButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (condition of conditions(); track $index; let i = $index) {
      <fieldset class="condition">
        <legend>Condition {{ i + 1 }}</legend>
        <gd-field-picker
          [entity]="entity()"
          [field]="condition.field.column ? condition.field : null"
          (fieldChange)="change(i, { field: $event ?? { path: [], column: '' } })"
        />
        <gd-condition-value
          label="Which"
          [value]="condition.value"
          (valueChange)="change(i, { value: $event })"
        />
        <button
          matIconButton
          type="button"
          class="remove"
          [attr.aria-label]="'Remove condition ' + (i + 1)"
          (click)="remove(i)"
        >
          <mat-icon>delete</mat-icon>
        </button>
      </fieldset>
    }
    <button matButton type="button" (click)="add()">
      <mat-icon>add</mat-icon>
      Add a condition
    </button>
  `,
  styles: `
    .condition {
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
export class ConditionList {
  readonly entity = input.required<string | null>();
  readonly conditions = model.required<readonly { field: FieldRef; value: ConditionValue }[]>();

  protected add(): void {
    this.conditions.set([
      ...this.conditions(),
      { field: { path: [], column: '' }, value: { op: 'eq', value: '' } },
    ]);
  }

  protected remove(index: number): void {
    this.conditions.set(this.conditions().filter((_, i) => i !== index));
  }

  protected change(
    index: number,
    change: Partial<{ field: FieldRef; value: ConditionValue }>,
  ): void {
    this.conditions.set(this.conditions().map((c, i) => (i === index ? { ...c, ...change } : c)));
  }
}
