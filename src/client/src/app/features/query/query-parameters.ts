import { Component, input, model } from '@angular/core';
import { MatCheckbox } from '@angular/material/checkbox';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import type { Schema } from '../../core/api/api-client';
import type { ParameterValues } from '../../core/query/query-url';
import type { GridColumn } from '../browse/grid/grid-columns';
import { parsedValue } from '../browse/grid/grid-edits';
import type { QueryParameterInput } from './query-datasource';

export type UsedParameter = Schema<'UsedParameterDto'>;
type ScalarKind = Schema<'ScalarKind'>;

/** The types the language names, as the API's kinds. */
const scalarKinds: Readonly<Record<string, ScalarKind>> = {
  boolean: 'boolean',
  int16: 'int16',
  int32: 'int32',
  int64: 'int64',
  decimal: 'decimal',
  single: 'single',
  double: 'double',
  string: 'string',
  binary: 'binary',
  guid: 'guid',
  date: 'date',
  time: 'time',
  datetime: 'dateTime',
  datetimeoffset: 'dateTimeOffset',
  interval: 'interval',
  json: 'json',
};

/** The kind of a type as the language writes it, without its size or `?`: `decimal` for `decimal(10,2)?`. */
export function kindOf(type: string | null): string | null {
  const kind = type?.replace(/\?$/, '').replace(/\(.*$/, '').trim();
  return kind || null;
}

/**
 * A parameter's value as sent: of the type the query takes for it (its kind, so a value needs no more than it
 * holds), read from the text as typed; NULL as null. When the query doesn't say (or takes no type), the value is
 * typed as the command line types one: `null`, `true` or `false`, a whole number, a decimal number, else text
 * (quoted with ' or " to keep it text).
 */
export function parameterInputOf(
  name: string,
  value: string | null,
  type: string | null,
): QueryParameterInput {
  if (value === null) {
    return { name, value: null };
  }
  const kind = kindOf(type);
  if (kind !== null && kind !== 'unknown') {
    // Text as typed; other values as they read, without the spaces around them.
    return {
      name,
      type: kind,
      value: ['string', 'json', 'binary'].includes(kind) ? value : value.trim(),
    };
  }
  const text = value.trim();
  const quoted = /^(['"]).*\1$/s.test(text) && text.length >= 2;
  if (quoted) {
    return { name, value: text.slice(1, -1) };
  }
  switch (text.toLowerCase()) {
    case 'null':
      return { name, value: null };
    case 'true':
      return { name, type: 'boolean', value: 'true' };
    case 'false':
      return { name, type: 'boolean', value: 'false' };
  }
  if (/^[+-]?\d+$/.test(text) && withinInt64(text)) {
    return { name, type: 'int64', value: text };
  }
  if (/^[+-]?(\d+\.?\d*|\.\d+)$/.test(text)) {
    return { name, type: 'decimal', value: text };
  }
  return { name, value: text };
}

/** Whether a value is given for a parameter: text typed, or NULL. */
export function given(values: ParameterValues, name: string): boolean {
  return Object.hasOwn(values, name) && values[name] !== '';
}

/**
 * What is wrong with a value given for a parameter, for the type the query takes for it: not a number, not a date,
 * out of range. Values of other types (or of none known) are sent as typed.
 */
export function parameterProblemOf(
  name: string,
  value: string | null | undefined,
  type: string | null,
): string | null {
  const kind = scalarKinds[kindOf(type) ?? ''];
  if (value === undefined || value === null || value === '' || !kind) {
    return null;
  }
  const column: GridColumn = {
    name: `$${name}`,
    type: { kind, nullable: true, text: kindOf(type) ?? kind },
    isKey: false,
    canUpdate: true,
    insert: 'optional',
    readOnlyReason: null,
    lineage: { kind: 'unknown', sources: [], expression: null },
    reference: null,
  };
  const parsed = parsedValue(value, column);
  return 'problem' in parsed ? parsed.problem : null;
}

/** A value as the editor holds it, from one as sent (a saved query's, a link's): text, or null for NULL. */
export function valueText(value: unknown): string | null {
  if (value === null || value === undefined) {
    return null;
  }
  return typeof value === 'string' ? value : JSON.stringify(value);
}

/** How a value of a kind is written, as a hint. */
function formatOf(kind: string | null): string | null {
  switch (kind) {
    case 'date':
      return 'yyyy-mm-dd';
    case 'datetime':
      return 'yyyy-mm-dd hh:mm:ss';
    case 'datetimeoffset':
      return 'yyyy-mm-dd hh:mm:ss+hh:mm';
    case 'time':
      return 'hh:mm:ss';
    case 'interval':
      return 'd.hh:mm:ss';
    case 'boolean':
      return 'true or false';
    default:
      return null;
  }
}

/** Values with one of them set (or, undefined, not given), as own properties whatever their names. */
export function withValue(
  values: ParameterValues,
  name: string,
  value: string | null | undefined,
): ParameterValues {
  const others = Object.entries(values).filter(([other]) => other !== name);
  return Object.fromEntries(value === undefined ? others : [...others, [name, value]]);
}

function withinInt64(text: string): boolean {
  const value = BigInt(text);
  return value >= -(2n ** 63n) && value < 2n ** 63n;
}

/**
 * The values of the parameters a query uses (`$min`), as they are named in it: each typed as text, its type (as
 * far as the query says) and how such a value is written beside it, or NULL.
 */
@Component({
  selector: 'gd-query-parameters',
  imports: [MatCheckbox, MatFormField, MatHint, MatInput, MatLabel],
  template: `
    @if (used().length > 0) {
      <div class="parameters" role="group" aria-label="Parameters">
        @for (parameter of used(); track parameter.name) {
          @let value = valueOf(parameter.name);
          <div class="parameter">
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>{{ '$' + parameter.name }}</mat-label>
              <input
                matInput
                class="code"
                autocomplete="off"
                spellcheck="false"
                [attr.data-parameter]="parameter.name"
                [attr.aria-invalid]="!!problems()[parameter.name]"
                [disabled]="value === null"
                [value]="value ?? ''"
                [placeholder]="placeholderOf(parameter)"
                (input)="typed(parameter.name, $event)"
              />
              @if (problems()[parameter.name]; as problem) {
                <mat-hint class="problem">{{ problem }}</mat-hint>
              } @else {
                <mat-hint>{{ hintOf(parameter) }}</mat-hint>
              }
            </mat-form-field>
            <mat-checkbox
              [checked]="value === null"
              [aria-label]="'$' + parameter.name + ' is NULL'"
              (change)="nulled(parameter.name, $event.checked)"
            >
              NULL
            </mat-checkbox>
          </div>
        }
      </div>
    }
  `,
  styles: `
    .parameters {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 16px;
    }

    .parameter {
      display: flex;
      align-items: center;
      gap: 4px;
    }

    .code {
      font-family: var(--gd-code-font-family);
    }

    .problem {
      color: var(--mat-sys-error);
    }
  `,
})
export class QueryParameters {
  /** The parameters the query uses, in the order it first uses them. */
  readonly used = input.required<readonly UsedParameter[]>();
  /** Their values, by name: text, or null for NULL (those not given aren't there, or are empty). */
  readonly values = model.required<ParameterValues>();
  /** What is wrong with values given, by name. */
  readonly problems = input<Readonly<Record<string, string>>>({});
  /** The text typed for parameters made NULL, to give back when they aren't. */
  private readonly beforeNull = new Map<string, string>();

  protected placeholderOf(parameter: UsedParameter): string {
    return formatOf(kindOf(parameter.type)) ?? '';
  }

  protected hintOf(parameter: UsedParameter): string {
    const kind = kindOf(parameter.type);
    return kind && kind !== 'unknown' ? kind : 'typed as written';
  }

  protected typed(name: string, event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.values.update((values) => withValue(values, name, text === '' ? undefined : text));
  }

  /** A parameter's value as typed (not one that every object has, by its name: `constructor`). */
  protected valueOf(name: string): string | null | undefined {
    const values = this.values();
    return Object.hasOwn(values, name) ? values[name] : undefined;
  }

  protected nulled(name: string, nulled: boolean): void {
    if (nulled) {
      const typed = this.valueOf(name);
      if (typed) {
        this.beforeNull.set(name, typed);
      }
      this.values.update((values) => withValue(values, name, null));
    } else {
      this.values.update((values) => withValue(values, name, this.beforeNull.get(name)));
      this.beforeNull.delete(name);
    }
  }
}
