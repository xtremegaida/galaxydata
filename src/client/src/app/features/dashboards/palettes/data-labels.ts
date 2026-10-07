import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatAutocomplete, MatAutocompleteTrigger, MatOption } from '@angular/material/autocomplete';
import { MatButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { ApiClient } from '../../../core/api/api-client';
import { problemMessage, problemOf } from '../../../core/api/problem';
import { EntitySearch } from '../../../core/catalog/catalog-lookups';
import { debounced } from '../../../core/ui/debounced';
import { labelText } from '../charts/series-colors';
import { categoryText } from '../charts/chart-values';
import { FieldPicker } from '../editor/controls/field-picker';
import type { Definition, FieldRef } from '../model/definition';
import { blankDefinition } from '../state/dashboard-store';
import { DASHBOARD_WAITS } from '../state/waits';

/** A label found in the data: its text (none: no value), as it shows, and how many rows have it. */
export interface FoundLabel {
  readonly label: string | null;
  readonly shown: string;
  readonly rows: number;
}

/** The definition a field's values are asked for with: its entity as a source, and a filter on the field. */
function sliceFor(entity: string, field: FieldRef): Definition {
  return {
    ...blankDefinition(),
    sources: [{ id: 'labels', entity, label: 'Labels' }],
    filters: [
      {
        id: 'labels',
        label: 'Labels',
        field: { source: 'labels', path: field.path, column: field.column },
        kind: 'values',
        value: null,
        visible: true,
        editable: true,
        multiple: true,
        except: [],
      },
    ],
  };
}

/**
 * Labels found in the data: an entity's field's values, most rows first (as a filter's are found), to give their
 * colours. Periods' and measures' labels are a chart's, which its colours in the dashboard editor show.
 */
@Component({
  selector: 'gd-data-labels',
  imports: [
    FieldPicker,
    MatAutocomplete,
    MatAutocompleteTrigger,
    MatButton,
    MatCheckbox,
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
    MatOption,
    MatProgressBar,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="find">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Entity</mat-label>
        <input
          #find
          matInput
          [value]="typed()"
          [matAutocomplete]="entities"
          (input)="typed.set(find.value)"
          autocomplete="off"
          spellcheck="false"
        />
        <mat-autocomplete #entities="matAutocomplete" (optionSelected)="chose($event.option.value)">
          @for (path of search.paths(); track path) {
            <mat-option [value]="path">{{ path }}</mat-option>
          }
        </mat-autocomplete>
        <mat-hint>A table, view or virtual entity, by name</mat-hint>
      </mat-form-field>
      <gd-field-picker
        [entity]="entity()"
        label="Field"
        [field]="field()"
        (fieldChange)="field.set($event); chosen.set([])"
      />
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Values holding</mat-label>
        <input
          #holding
          matInput
          [value]="text()"
          (input)="text.set(holding.value)"
          autocomplete="off"
          [disabled]="!field()"
        />
      </mat-form-field>
    </div>
    @if (values.isLoading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (problem(); as problem) {
      <p class="problem" role="alert">{{ problem }}</p>
    }
    @if (found(); as found) {
      <ul class="found" aria-label="Labels found">
        @for (item of found.labels; track $index) {
          <li>
            <mat-checkbox [checked]="isChosen(item)" (change)="toggle(item, $event.checked)">
              {{ item.shown }}
              <span class="rows">{{ item.rows }} {{ item.rows === 1 ? 'row' : 'rows' }}</span>
            </mat-checkbox>
          </li>
        } @empty {
          <li class="aside">No values</li>
        }
      </ul>
      @if (found.more) {
        <p class="aside">
          The {{ found.labels.length }} with most rows: write what they hold for others.
        </p>
      }
      <button
        matButton="tonal"
        type="button"
        [disabled]="chosen().length === 0 || disabled()"
        (click)="add.emit(chosen()); chosen.set([])"
      >
        Add {{ chosen().length || '' }} {{ chosen().length === 1 ? 'override' : 'overrides' }}
      </button>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: 8px;
    }
    .find {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: flex-start;
    }
    .found {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(14rem, 1fr));
      max-height: 16rem;
      overflow: auto;
    }
    .rows,
    .aside {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    .problem {
      color: var(--mat-sys-error);
    }
  `,
})
export class DataLabels {
  private readonly api = inject(ApiClient);
  private readonly waits = inject(DASHBOARD_WAITS);
  private readonly locale = inject(LOCALE_ID);

  /** Labels chosen to add as overrides. */
  readonly add = output<FoundLabel[]>();
  readonly disabled = input(false);

  protected readonly typed = signal('');
  protected readonly search = new EntitySearch(() => this.typed(), this.waits.filterTyping);
  protected readonly entity = signal<string | null>(null);
  protected readonly field = signal<FieldRef | null>(null);
  protected readonly text = signal('');
  private readonly holding = debounced(() => this.text().trim(), this.waits.filterTyping);
  protected readonly chosen = signal<FoundLabel[]>([]);

  protected readonly values = rxResource({
    params: () => {
      const entity = this.entity();
      const field = this.field();
      return entity && field ? { entity, field, search: this.holding() || null } : undefined;
    },
    stream: ({ params }) =>
      this.api.post('/api/dashboards/filter-values', {
        body: {
          slice: sliceFor(params.entity, params.field),
          filter: 'labels',
          state: { filters: {}, selections: {} },
          search: params.search,
        },
      }),
  });

  protected readonly found = computed(() => {
    if (!this.values.hasValue()) {
      return null;
    }
    const read = this.values.value();
    return {
      more: read.more,
      labels: read.values.map((v) => ({
        label: labelText(v.value),
        shown: categoryText(v.value, read.type, null, this.locale),
        rows: v.rows,
      })),
    };
  });

  protected readonly problem = computed(() => {
    const error = this.values.error();
    return error ? `Couldn't find its values: ${problemMessage(problemOf(error))}` : null;
  });

  protected chose(path: string): void {
    this.entity.set(path);
    this.typed.set(path);
    this.field.set(null);
    this.chosen.set([]);
  }

  protected isChosen(item: FoundLabel): boolean {
    return this.chosen().some((c) => c.label === item.label);
  }

  protected toggle(item: FoundLabel, on: boolean): void {
    const others = this.chosen().filter((c) => c.label !== item.label);
    this.chosen.set(on ? [...others, item] : others);
  }
}
