import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Directive,
  LOCALE_ID,
  computed,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import {
  MatAutocomplete,
  MatAutocompleteTrigger,
  type MatAutocompleteSelectedEvent,
} from '@angular/material/autocomplete';
import { MatChipGrid, MatChipInput, MatChipRemove, MatChipRow } from '@angular/material/chips';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatSelect } from '@angular/material/select';
import { EMPTY, switchMap, timer } from 'rxjs';
import { sameJson } from '../../../core/api/same-json';
import { filterValueOf, relativeText, valueText } from '../model/describe';
import type { ConditionValue, DashboardState, Filter } from '../model/definition';
import { DashboardStore } from '../state/dashboard-store';
import { DASHBOARD_WAITS } from '../state/waits';

type RelativeRange = NonNullable<ConditionValue['relative']>;

const numeric = new Set(['int16', 'int32', 'int64', 'decimal', 'single', 'double']);
const dated = new Set(['date', 'dateTime', 'dateTimeOffset']);

/** What a filter's control shares: its value as it holds, and setting it (the dashboard's own again when equal to it). */
@Directive()
abstract class FilterControl {
  readonly filter = input.required<Filter>();
  protected readonly store = inject(DashboardStore);
  protected readonly locale = inject(LOCALE_ID);

  protected readonly value = computed(() => filterValueOf(this.filter(), this.store.filters()));

  protected set(value: ConditionValue | null): void {
    const filter = this.filter();
    this.store.setFilter(filter.id, sameJson(value, filter.value) ? undefined : value);
  }

  /**
   * The state the server takes the filter's values under: the other filters. Not what is chosen in the widgets: a
   * filter of the field a chart chose from would offer only what it chose.
   */
  protected stateForValues(): DashboardState {
    return { filters: this.store.filters() as DashboardState['filters'], selections: {} };
  }
}

/**
 * A filter of values: those chosen as chips, more found as typing pauses (the values there are, most rows first,
 * under the other filters and what is chosen), asked for once the field is first used.
 */
@Component({
  selector: 'gd-values-filter',
  imports: [
    MatAutocomplete,
    MatAutocompleteTrigger,
    MatChipGrid,
    MatChipInput,
    MatChipRemove,
    MatChipRow,
    MatFormField,
    MatIcon,
    MatLabel,
    MatOption,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field class="field" subscriptSizing="dynamic">
      <mat-label>{{ label() }}</mat-label>
      <mat-chip-grid #grid [attr.aria-label]="filter().label + ', values kept'">
        @for (value of chosen(); track $index) {
          <mat-chip-row (removed)="remove($index)">
            {{ text(value) }}
            <button matChipRemove type="button" [attr.aria-label]="'Remove ' + text(value)">
              <mat-icon>cancel</mat-icon>
            </button>
          </mat-chip-row>
        }
        <input
          #field
          [matChipInputFor]="grid"
          [matAutocomplete]="choices"
          [placeholder]="chosen().length === 0 ? 'Any' : ''"
          [value]="search()"
          (input)="typed($event)"
          (focus)="asked.set(true)"
          autocomplete="off"
          spellcheck="false"
        />
      </mat-chip-grid>
      <mat-autocomplete #choices="matAutocomplete" (optionSelected)="picked($event, field)">
        @for (option of options(); track $index) {
          <mat-option [value]="option.value">
            {{ text(option.value) }}
            <span class="rows"
              >{{ option.rows
              }}<span class="hidden"> {{ option.rows === 1 ? 'row' : 'rows' }}</span></span
            >
          </mat-option>
        }
        @if (values.isLoading()) {
          <mat-option disabled>Reading the values…</mat-option>
        } @else if (values.error()) {
          <mat-option disabled>Couldn't read the values</mat-option>
        } @else if (asked() && options().length === 0) {
          <mat-option disabled>No more values</mat-option>
        }
      </mat-autocomplete>
    </mat-form-field>
  `,
  styles: `
    .field {
      min-width: 200px;
    }
    .hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
    }
    .rows {
      margin-inline-start: 8px;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
  `,
})
export class ValuesFilter extends FilterControl {
  private readonly waits = inject(DASHBOARD_WAITS);

  protected readonly search = signal('');
  /** Whether the values have been asked for: the field was used. */
  protected readonly asked = signal(false);

  protected readonly chosen = computed(() => this.value()?.values ?? []);
  protected readonly excluding = computed(() => this.value()?.op === 'notIn');
  protected readonly label = computed(
    () => this.filter().label + (this.excluding() ? ' (not)' : ''),
  );

  protected readonly values = rxResource({
    params: () =>
      this.asked() && this.store.host()
        ? { search: this.search().trim(), state: this.stateForValues() }
        : undefined,
    stream: ({ params }) => {
      const host = this.store.host();
      return host
        ? timer(params.search ? this.waits.filterTyping : 0).pipe(
            switchMap(() => host.values(this.filter().id, params.state, params.search || null)),
          )
        : EMPTY;
    },
  });

  /** The values found, but those chosen already. */
  protected readonly options = computed(() => {
    const found = this.values.hasValue() ? (this.values.value()?.values ?? []) : [];
    const chosen = this.chosen();
    return found.filter((option) => !chosen.some((c) => sameJson(c, option.value)));
  });

  protected text(value: unknown): string {
    return valueText(value, this.locale);
  }

  protected typed(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
  }

  protected picked(event: MatAutocompleteSelectedEvent, field: HTMLInputElement): void {
    const value: unknown = event.option.value;
    field.value = '';
    this.search.set('');
    const values = this.filter().multiple ? [...this.chosen(), value] : [value];
    this.set({ op: this.excluding() ? 'notIn' : 'in', values });
  }

  protected remove(index: number): void {
    const values = this.chosen().filter((_, i) => i !== index);
    this.set(values.length === 0 ? null : { op: this.excluding() ? 'notIn' : 'in', values });
  }
}

/** A filter of a range: from, up to, or between two values, typed as the field's type takes them (dates by day). */
@Component({
  selector: 'gd-range-filter',
  imports: [MatFormField, MatInput, MatLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset class="range">
      <legend class="hidden">{{ filter().label }}</legend>
      <mat-form-field class="field" subscriptSizing="dynamic">
        <mat-label>{{ filter().label }} from</mat-label>
        <input
          matInput
          [type]="inputType()"
          [attr.inputmode]="inputType() === 'number' ? 'decimal' : null"
          [value]="from()"
          (change)="changed('from', $event)"
          autocomplete="off"
        />
      </mat-form-field>
      <mat-form-field class="field" subscriptSizing="dynamic">
        <mat-label>to</mat-label>
        <input
          matInput
          [type]="inputType()"
          [attr.inputmode]="inputType() === 'number' ? 'decimal' : null"
          [value]="to()"
          (change)="changed('to', $event)"
          autocomplete="off"
        />
      </mat-form-field>
    </fieldset>
  `,
  styles: `
    .range {
      display: flex;
      gap: 4px;
      margin: 0;
      padding: 0;
      border: 0;
    }
    .field {
      width: 150px;
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
export class RangeFilter extends FilterControl {
  /** The field's type, from its values (asked for once): which input it takes. */
  private readonly type = rxResource({
    params: () => this.store.host() ?? undefined,
    stream: ({ params: host }) =>
      host.values(
        this.filter().id,
        untracked(() => this.stateForValues()),
        null,
      ),
  });

  protected readonly inputType = computed(() => {
    const kind = this.type.hasValue() ? this.type.value()?.type.kind : undefined;
    return kind && dated.has(kind) ? 'date' : kind && numeric.has(kind) ? 'number' : 'text';
  });

  protected readonly from = computed(() => {
    const value = this.value();
    return value && value.op !== 'le' && value.op !== 'lt' ? this.plain(value.value) : '';
  });

  protected readonly to = computed(() => {
    const value = this.value();
    if (!value) {
      return '';
    }
    return value.op === 'between'
      ? this.plain(value.valueTo)
      : value.op === 'le' || value.op === 'lt'
        ? this.plain(value.value)
        : '';
  });

  protected changed(side: 'from' | 'to', event: Event): void {
    const typed = (event.target as HTMLInputElement).value.trim();
    const from = side === 'from' ? typed : this.from();
    const to = side === 'to' ? typed : this.to();
    this.set(
      from && to
        ? { op: 'between', value: from, valueTo: to }
        : from
          ? { op: 'ge', value: from }
          : to
            ? { op: 'le', value: to }
            : null,
    );
  }

  /** A bound as its input shows it: a date-time's day. */
  private plain(value: unknown): string {
    if (value === null || value === undefined) {
      return '';
    }
    const text = String(value);
    return this.inputType() === 'date' ? text.slice(0, 10) : text;
  }
}

const presets: readonly RelativeRange[] = [
  { mode: 'this', count: 1, unit: 'day' },
  { mode: 'last', count: 7, unit: 'day' },
  { mode: 'last', count: 30, unit: 'day' },
  { mode: 'this', count: 1, unit: 'week' },
  { mode: 'this', count: 1, unit: 'month' },
  { mode: 'previous', count: 1, unit: 'month' },
  { mode: 'last', count: 3, unit: 'month' },
  { mode: 'last', count: 12, unit: 'month' },
  { mode: 'this', count: 1, unit: 'quarter' },
  { mode: 'previous', count: 1, unit: 'quarter' },
  { mode: 'this', count: 1, unit: 'year' },
  { mode: 'previous', count: 1, unit: 'year' },
  { mode: 'toDate', count: 1, unit: 'year' },
];

/** A filter of a period relative to today: chosen from the usual ones (and the dashboard's, if it is another). */
@Component({
  selector: 'gd-relative-filter',
  imports: [MatFormField, MatLabel, MatOption, MatSelect],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field class="field" subscriptSizing="dynamic">
      <mat-label>{{ filter().label }}</mat-label>
      <mat-select [value]="chosen()" (valueChange)="picked($event)">
        <mat-option [value]="-1">Any time</mat-option>
        @for (range of ranges(); track $index) {
          <mat-option [value]="$index">{{ say(range) }}</mat-option>
        }
      </mat-select>
    </mat-form-field>
  `,
  styles: `
    .field {
      width: 180px;
    }
  `,
})
export class RelativeFilter extends FilterControl {
  /** The periods offered: the usual ones, and the one that holds when it isn't one of them. */
  protected readonly ranges = computed(() => {
    const now = this.value()?.relative;
    return now && !presets.some((p) => sameJson(p, now)) ? [...presets, now] : presets;
  });

  protected readonly chosen = computed(() => {
    const now = this.value()?.relative;
    return now ? this.ranges().findIndex((r) => sameJson(r, now)) : -1;
  });

  protected say(range: RelativeRange): string {
    const text = relativeText(range);
    return text[0].toUpperCase() + text.slice(1);
  }

  protected picked(index: number): void {
    const range = this.ranges()[index];
    this.set(range ? { op: 'relative', relative: range } : null);
  }
}

/** A filter of text: what a value contains (or starts with), applied as typing pauses. */
@Component({
  selector: 'gd-text-filter',
  imports: [MatFormField, MatInput, MatLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field class="field" subscriptSizing="dynamic">
      <mat-label>{{ filter().label }}</mat-label>
      <input
        matInput
        type="search"
        [value]="text()"
        (input)="typed($event)"
        (change)="apply()"
        autocomplete="off"
        spellcheck="false"
      />
    </mat-form-field>
  `,
  styles: `
    .field {
      width: 180px;
    }
  `,
})
export class TextFilter extends FilterControl {
  private readonly waits = inject(DASHBOARD_WAITS);
  private pending: ReturnType<typeof setTimeout> | null = null;
  private typedText: string | null = null;

  protected readonly text = computed(() => {
    const value = this.value()?.value;
    return value === null || value === undefined ? '' : String(value);
  });

  constructor() {
    super();
    inject(DestroyRef).onDestroy(() => this.cancel());
  }

  protected typed(event: Event): void {
    this.typedText = (event.target as HTMLInputElement).value;
    this.cancel();
    this.pending = setTimeout(() => this.apply(), this.waits.filterTyping);
  }

  protected apply(): void {
    this.cancel();
    if (this.typedText === null) {
      return;
    }
    const text = this.typedText.trim();
    this.typedText = null;
    const op = this.filter().value?.op === 'startsWith' ? 'startsWith' : 'contains';
    this.set(text ? { op, value: text } : null);
  }

  private cancel(): void {
    if (this.pending !== null) {
      clearTimeout(this.pending);
      this.pending = null;
    }
  }
}

/** A filter of yes or no. */
@Component({
  selector: 'gd-boolean-filter',
  imports: [MatFormField, MatLabel, MatOption, MatSelect],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-form-field class="field" subscriptSizing="dynamic">
      <mat-label>{{ filter().label }}</mat-label>
      <mat-select [value]="chosen()" (valueChange)="picked($event)">
        <mat-option value="any">Any</mat-option>
        <mat-option value="yes">Yes</mat-option>
        <mat-option value="no">No</mat-option>
      </mat-select>
    </mat-form-field>
  `,
  styles: `
    .field {
      width: 140px;
    }
  `,
})
export class BooleanFilter extends FilterControl {
  protected readonly chosen = computed(() => {
    const value = this.value()?.value;
    return value === true || value === 'true'
      ? 'yes'
      : value === false || value === 'false'
        ? 'no'
        : 'any';
  });

  protected picked(choice: 'any' | 'yes' | 'no'): void {
    this.set(choice === 'any' ? null : { op: 'eq', value: choice === 'yes' });
  }
}
