import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import {
  MatExpansionPanel,
  MatExpansionPanelHeader,
  MatExpansionPanelTitle,
  MatExpansionPanelDescription,
} from '@angular/material/expansion';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatSelect } from '@angular/material/select';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { LOCALE_ID } from '@angular/core';
import type { ConditionValue, Filter } from '../../model/definition';
import { filterValueText } from '../../model/describe';
import { addFilter, removeFilter, renameNewFilter, updateFilter } from '../../model/definition-ops';
import { ConditionValueEditor, filterOps, withOp } from '../controls/condition-editor';
import { FieldPicker } from '../controls/field-picker';
import { EditorStore } from '../editor-store';

const kinds: readonly { kind: Filter['kind']; label: string }[] = [
  { kind: 'values', label: 'Values' },
  { kind: 'range', label: 'A range' },
  { kind: 'relative', label: 'A period till today' },
  { kind: 'text', label: 'Text' },
  { kind: 'boolean', label: 'Yes or no' },
];

/** A filter kind's first value: what a default starts as. */
function startOf(kind: Filter['kind']): ConditionValue {
  return withOp({ op: filterOps[kind][0] }, filterOps[kind][0]);
}

/**
 * The dashboard's filters: each on a source's field, of a kind (values, a range, a period, text, yes or no), with a
 * default or none; shown or hidden, and if shown, fixed or the viewers' to change; applying to the widgets of
 * linked sources (the widget panel says which). Hidden filters aren't row-level security: they narrow only the
 * widgets they reach, and the dashboard's queries show them.
 */
@Component({
  selector: 'gd-filters-panel',
  imports: [
    ConditionValueEditor,
    FieldPicker,
    MatButton,
    MatExpansionPanel,
    MatExpansionPanelDescription,
    MatExpansionPanelHeader,
    MatExpansionPanelTitle,
    MatFormField,
    MatIcon,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
    MatSlideToggle,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="panel">
      <p class="aside">
        A hidden filter narrows the widgets it reaches; it isn't row-level security (signed-in
        viewers can see the dashboard's queries).
      </p>
      @for (filter of store.draft().filters; track $index) {
        <mat-expansion-panel>
          <mat-expansion-panel-header>
            <mat-panel-title>{{ filter.label || filter.id }}</mat-panel-title>
            <mat-panel-description>{{ state(filter) }}</mat-panel-description>
          </mat-expansion-panel-header>
          <div class="filter">
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Label</mat-label>
              <input
                matInput
                [value]="filter.label"
                (input)="change(filter, { label: text($event) }, 'label')"
              />
            </mat-form-field>
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Source</mat-label>
              <mat-select
                [value]="filter.field.source"
                (valueChange)="change(filter, { field: { source: $event, path: [], column: '' } })"
              >
                @for (source of store.draft().sources; track source.id) {
                  <mat-option [value]="source.id">{{ source.label }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <gd-field-picker
              [entity]="entityOf(filter.field.source)"
              [field]="filter.field.column ? filter.field : null"
              (fieldChange)="
                change(filter, {
                  field: {
                    source: filter.field.source,
                    path: $event?.path ?? [],
                    column: $event?.column ?? '',
                  },
                })
              "
            />
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Kind</mat-label>
              <mat-select [value]="filter.kind" (valueChange)="kinded(filter, $event)">
                @for (choice of kinds; track choice.kind) {
                  <mat-option [value]="choice.kind">{{ choice.label }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <mat-slide-toggle
              [checked]="filter.value !== null"
              (change)="change(filter, { value: $event.checked ? startOf(filter.kind) : null })"
            >
              A default value
            </mat-slide-toggle>
            @if (filter.value; as value) {
              <gd-condition-value
                label="Keeps rows whose value"
                [ops]="opsOf(filter.kind)"
                [boolean]="filter.kind === 'boolean'"
                [value]="value"
                (valueChange)="change(filter, { value: $event }, 'value')"
              />
            }
            <mat-slide-toggle
              [checked]="filter.visible"
              (change)="change(filter, { visible: $event.checked })"
            >
              Shown to viewers
            </mat-slide-toggle>
            <mat-slide-toggle
              [checked]="filter.editable"
              [disabled]="!filter.visible"
              (change)="change(filter, { editable: $event.checked })"
            >
              Viewers may change it
            </mat-slide-toggle>
            @if (filter.kind === 'values') {
              <mat-slide-toggle
                [checked]="filter.multiple"
                (change)="change(filter, { multiple: $event.checked })"
              >
                More than one value
              </mat-slide-toggle>
            }
            <button matButton type="button" class="remove" (click)="remove(filter)">
              <mat-icon>delete</mat-icon>
              Remove the filter
            </button>
          </div>
        </mat-expansion-panel>
      }
      <button
        matButton
        type="button"
        [disabled]="store.draft().sources.length === 0"
        (click)="add()"
      >
        <mat-icon>add</mat-icon>
        Add a filter
      </button>
    </div>
  `,
  styles: `
    .panel {
      display: grid;
      gap: 8px;
    }
    .filter {
      display: grid;
      gap: 12px;
    }
    .remove {
      justify-self: start;
      color: var(--mat-sys-error);
    }
    .aside {
      margin: 0;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class FiltersPanel {
  protected readonly store = inject(EditorStore);
  private readonly locale = inject(LOCALE_ID);
  protected readonly kinds = kinds;
  protected readonly startOf = startOf;

  private readonly entities = computed(
    () => new Map(this.store.draft().sources.map((s) => [s.id, s.entity])),
  );

  protected entityOf(source: string): string | null {
    return this.entities().get(source) ?? null;
  }

  protected opsOf(kind: Filter['kind']): readonly ConditionValue['op'][] {
    return filterOps[kind];
  }

  protected state(filter: Filter): string {
    const shown = !filter.visible ? 'hidden' : filter.editable ? 'viewers change it' : 'fixed';
    return `${filterValueText(filter.value, this.locale)} · ${shown}`;
  }

  protected text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected change(
    filter: Filter,
    change: Partial<Omit<Filter, 'id'>>,
    key: string | null = null,
  ): void {
    // A new filter's id follows its label (`f.status=` in addresses); a saved one's stays, as links name it.
    const saved = this.store.saved().definition.filters.some((f) => f.id === filter.id);
    const at = this.store.draft().filters.findIndex((f) => f.id === filter.id);
    this.store.apply(
      `Changed the filter ${filter.label}`,
      (d) => {
        const changed = updateFilter(d, filter.id, change);
        return change.label !== undefined && !saved
          ? renameNewFilter(changed, filter.id, change.label)
          : changed;
      },
      key ? `filter:${at}:${key}` : null,
    );
  }

  /** Another kind: a default kept only as far as the kind takes its op. */
  protected kinded(filter: Filter, kind: Filter['kind']): void {
    const value = filter.value;
    const ops = filterOps[kind];
    this.change(filter, {
      kind,
      value: value ? withOp(value, ops.includes(value.op) ? value.op : ops[0]) : null,
      multiple: kind === 'values' ? filter.multiple : true,
    });
  }

  protected add(): void {
    const source = this.store.draft().sources[0];
    this.store.apply(
      'Added a filter',
      (d) =>
        addFilter(d, {
          label: 'Filter',
          field: { source: source.id, path: [], column: '' },
          kind: 'values',
          value: null,
          visible: true,
          editable: true,
          multiple: true,
          except: [],
        }).definition,
    );
  }

  protected remove(filter: Filter): void {
    this.store.apply(`Removed the filter ${filter.label}`, (d) => removeFilter(d, filter.id));
  }
}
