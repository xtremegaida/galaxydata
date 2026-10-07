import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  model,
  output,
  viewChild,
} from '@angular/core';
import {
  MatAutocomplete,
  MatAutocompleteTrigger,
  type MatAutocompleteSelectedEvent,
} from '@angular/material/autocomplete';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import type { FieldRef } from '../../model/definition';
import {
  type Entity,
  EntityCatalog,
  type FieldUse,
  type TypeDto,
  fieldAllowed,
} from '../entity-catalog';

/** A field chosen: where it is, its column's label (or name) and type. */
export interface ChosenField {
  readonly field: FieldRef;
  readonly label: string;
  readonly type: TypeDto;
}

interface Choice {
  /** The text the field gets: `customer.city`, or `customer.` for a navigation to go on from. */
  readonly text: string;
  readonly shown: string;
  readonly detail: string;
  readonly navigation: boolean;
}

/** A field as text: its navigations, then its column, `.` between them. */
export function fieldText(field: FieldRef | null | undefined): string {
  return field && field.column ? [...field.path, field.column].join('.') : '';
}

/**
 * A field of a source's entity: one of its columns, or a column of an entity it leads to through navigations to one
 * row (`customer.country`), typed and suggested step by step (choosing a navigation goes on into its entity). Only
 * columns of the types `use` allows are suggested; hidden ones and navigations to many aren't.
 */
@Component({
  selector: 'gd-field-picker',
  imports: [
    MatAutocomplete,
    MatAutocompleteTrigger,
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
    MatOption,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field class="field" subscriptSizing="dynamic">
      <mat-label>{{ label() }}</mat-label>
      <input
        matInput
        [value]="text()"
        [matAutocomplete]="choices"
        (input)="typed($event)"
        (change)="settled()"
        autocomplete="off"
        spellcheck="false"
      />
      <mat-autocomplete #choices="matAutocomplete" (optionSelected)="picked($event)">
        @for (choice of options(); track choice.text) {
          <mat-option [value]="choice.text">
            {{ choice.shown }} <span class="detail">{{ choice.detail }}</span>
          </mat-option>
        }
      </mat-autocomplete>
      @if (problem(); as problem) {
        <!-- A hint, not an error: Material shows errors of form controls only. -->
        <mat-hint class="problem" role="alert">{{ problem }}</mat-hint>
      } @else if (hint()) {
        <mat-hint>{{ hint() }}</mat-hint>
      }
    </mat-form-field>
  `,
  styles: `
    .field {
      width: 100%;
    }
    .problem {
      color: var(--mat-sys-error);
    }
    .detail {
      margin-inline-start: 8px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
  `,
})
export class FieldPicker {
  private readonly catalog = inject(EntityCatalog);
  private readonly trigger = viewChild.required(MatAutocompleteTrigger);

  /** The source's entity, as queries name it. */
  readonly entity = input.required<string | null>();
  readonly label = input('Field');
  readonly use = input<FieldUse>('any');
  readonly hint = input<string | null>(null);
  readonly field = model<FieldRef | null>(null);
  /** A field chosen (or typed, when it is one), with its label and type. */
  readonly chosen = output<ChosenField>();

  /** The text typed, the field's own till something else is typed. */
  protected readonly text = linkedSignal(() => fieldText(this.field()));

  /** The entity the text's navigations lead to, and what is typed after them. */
  private readonly reached = computed(() => {
    const parts = this.text().split('.');
    let entity = this.entity() ? this.catalog.entity(this.entity()!)() : null;
    for (const step of parts.slice(0, -1)) {
      const navigation = entity?.navigations.find(
        (n) =>
          n.name.toLowerCase() === step.toLowerCase() && n.multiplicity !== 'many' && !n.hidden,
      );
      entity = navigation ? this.catalog.entity(navigation.target)() : null;
    }
    return { entity: entity ?? null, prefix: parts.slice(0, -1), last: parts.at(-1) ?? '' };
  });

  protected readonly options = computed<Choice[]>(() => {
    const { entity, prefix, last } = this.reached();
    if (!entity) {
      return [];
    }
    const typed = last.toLowerCase();
    const before = prefix.length > 0 ? prefix.join('.') + '.' : '';
    const columns = entity.columns
      .filter(
        (c) =>
          !c.hidden && fieldAllowed(c.type, this.use()) && c.name.toLowerCase().includes(typed),
      )
      .map((c) => ({
        text: before + c.name,
        shown: before + c.name,
        detail: [c.label, c.type.text].filter(Boolean).join(' · '),
        navigation: false,
      }));
    const navigations = entity.navigations
      .filter((n) => n.multiplicity !== 'many' && !n.hidden && n.name.toLowerCase().includes(typed))
      .map((n) => ({
        text: `${before}${n.name}.`,
        shown: `${before}${n.name}.`,
        detail: `to ${n.target}`,
        navigation: true,
      }));
    return [...columns, ...navigations];
  });

  protected readonly problem = computed(() => {
    const text = this.text();
    if (!text || text === fieldText(this.field()) || this.reached().entity === null) {
      return null;
    }
    return this.columnOf(text)
      ? null
      : 'Not a column this source has (or leads to by its navigations to one)';
  });

  protected typed(event: Event): void {
    this.text.set((event.target as HTMLInputElement).value);
  }

  protected picked(event: MatAutocompleteSelectedEvent): void {
    const text = String(event.option.value);
    this.text.set(text);
    if (text.endsWith('.')) {
      // A navigation: its entity's fields are suggested next (the panel closed as it was chosen).
      setTimeout(() => this.trigger().openPanel());
    } else {
      this.settled();
    }
  }

  /** What is typed is the field, when it is one. */
  protected settled(): void {
    const text = this.text().trim();
    if (!text) {
      this.field.set(null);
      return;
    }
    const found = this.columnOf(text);
    if (found) {
      this.field.set(found.field);
      this.chosen.emit(found);
    }
  }

  private columnOf(text: string): ChosenField | null {
    const parts = text.split('.');
    let entity: Entity | null | undefined = this.entity()
      ? this.catalog.entity(this.entity()!)()
      : null;
    const path: string[] = [];
    for (const step of parts.slice(0, -1)) {
      const navigation = entity?.navigations.find(
        (n) =>
          n.name.toLowerCase() === step.toLowerCase() && n.multiplicity !== 'many' && !n.hidden,
      );
      if (!navigation) {
        return null;
      }
      path.push(navigation.name);
      entity = this.catalog.entity(navigation.target)();
    }
    const name = parts.at(-1)!.toLowerCase();
    const column = entity?.columns.find((c) => c.name.toLowerCase() === name && !c.hidden);
    return column && fieldAllowed(column.type, this.use())
      ? {
          field: { path, column: column.name },
          label: column.label || humanize(column.name),
          type: column.type,
        }
      : null;
  }
}

/** A column's name as a label: `order_date` → "Order date". */
export function humanize(name: string): string {
  const words = name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_\s]+/g, ' ')
    .trim()
    .toLowerCase();
  return words ? words[0].toUpperCase() + words.slice(1) : name;
}
