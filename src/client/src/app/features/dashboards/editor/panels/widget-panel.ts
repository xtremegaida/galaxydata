import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatSelect } from '@angular/material/select';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import {
  type DataConfig,
  type WidgetConfig,
  configOf,
  isData,
  reachedFrom,
} from '../../model/definition';
import {
  duplicateWidget,
  hideAt,
  removeWidget,
  setFilterApplies,
  updateWidget,
} from '../../model/definition-ops';
import { WIDGET_KINDS, kindOf } from '../../model/widget-registry';
import { ConditionList } from '../controls/condition-editor';
import { EditorStore } from '../editor-store';
import { KindSettingsHost } from './kind-settings-host';

type Listens = DataConfig['listens'];

/**
 * The settings of the widget chosen: its title; for widgets of data, their source, whether choosing their slices
 * filters others, which they listen to, which filters apply to them, and their own conditions; then their kind's
 * own (loaded as the kind's editor). Every change is a step of the history.
 */
@Component({
  selector: 'gd-widget-panel',
  imports: [
    ConditionList,
    KindSettingsHost,
    MatButton,
    MatCheckbox,
    MatFormField,
    MatHint,
    MatIcon,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
    MatSlideToggle,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (widget(); as widget) {
      <div class="panel">
        <p class="kind">{{ kind()?.label ?? widget.config.kind }} · {{ widget.id }}</p>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Title</mat-label>
          <input
            matInput
            [value]="widget.title ?? ''"
            (input)="titled($event)"
            autocomplete="off"
          />
          @if (!data()) {
            <mat-hint>Without one, the text stands on the page, unframed</mat-hint>
          }
        </mat-form-field>
        @if (data(); as config) {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Source</mat-label>
            <mat-select
              [value]="config.source"
              (valueChange)="setConfig({ source: $event }, 'Changed the source of')"
            >
              @for (source of store.draft().sources; track source.id) {
                <mat-option [value]="source.id"
                  >{{ source.label }} ({{ source.entity }})</mat-option
                >
              }
            </mat-select>
            @if (store.draft().sources.length === 0) {
              <mat-hint>Add a source first (Sources)</mat-hint>
            }
          </mat-form-field>
        }
        @if (kind(); as kind) {
          <gd-kind-settings-host
            [kind]="kind"
            [entity]="entity()"
            [config]="configOf(widget)"
            (configChange)="configured($event)"
          />
        }
        @if (data(); as config) {
          <h3>Filtering</h3>
          <mat-slide-toggle
            [checked]="config.emits"
            (change)="setConfig({ emits: $event.checked }, 'Changed what choosing does in')"
          >
            Choosing its slices filters the widgets that listen
          </mat-slide-toggle>
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Listens to</mat-label>
            <mat-select
              [value]="config.listens.mode"
              (valueChange)="listened({ mode: $event, widgets: config.listens.widgets })"
            >
              <mat-option value="all">Every widget linked</mat-option>
              <mat-option value="none">None</mat-option>
              <mat-option value="chosen">Those chosen</mat-option>
            </mat-select>
          </mat-form-field>
          @if (config.listens.mode === 'chosen') {
            <div class="checks" role="group" aria-label="The widgets it listens to">
              @for (other of emitters(); track other.id) {
                <mat-checkbox
                  [checked]="config.listens.widgets.includes(other.id)"
                  (change)="listenTo(config.listens, other.id, $event.checked)"
                >
                  {{ other.name }}
                </mat-checkbox>
              } @empty {
                <p class="aside">No other widget chooses slices.</p>
              }
            </div>
          }
          @if (filters().length > 0) {
            <div class="checks" role="group" aria-label="The filters that apply to it">
              <p class="aside">The filters that apply to it:</p>
              @for (filter of filters(); track filter.id) {
                <mat-checkbox
                  [checked]="!filter.except.includes(widget.id)"
                  (change)="applies(filter.id, $event.checked)"
                >
                  {{ filter.label }}
                </mat-checkbox>
              }
            </div>
          }
          <h3>Its own conditions</h3>
          <gd-condition-list
            [entity]="entity()"
            [conditions]="config.conditions"
            (conditionsChange)="
              setConfig({ conditions: [...$event] }, 'Changed the conditions of', 'conditions')
            "
          />
        }
        <h3>Widget</h3>
        <div class="actions">
          @if (store.laidOut() && !store.designed()) {
            <mat-slide-toggle [checked]="false" (change)="hide()">
              Hidden at {{ store.breakpoint().label }}
            </mat-slide-toggle>
          }
          <button matButton type="button" (click)="duplicate()">
            <mat-icon>content_copy</mat-icon>
            Duplicate
          </button>
          <button matButton type="button" class="remove" (click)="remove()">
            <mat-icon>delete</mat-icon>
            Remove
          </button>
        </div>
      </div>
    } @else {
      <p class="aside empty">Choose a widget on the canvas to see its settings, or add one.</p>
    }
  `,
  styles: `
    .panel {
      display: grid;
      gap: 12px;
    }
    .kind {
      margin: 0;
      font: var(--mat-sys-label-medium);
      color: var(--mat-sys-on-surface-variant);
    }
    h3 {
      margin: 8px 0 0;
      font: var(--mat-sys-title-small);
    }
    .checks {
      display: grid;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: center;
    }
    .remove {
      color: var(--mat-sys-error);
    }
    .aside {
      margin: 0;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
    .empty {
      margin: 24px 0;
    }
  `,
})
export class WidgetPanel {
  protected readonly store = inject(EditorStore);
  private readonly kinds = inject(WIDGET_KINDS);
  protected readonly configOf = configOf;

  protected readonly widget = computed(() => {
    const id = this.store.selected();
    return this.store.draft().widgets.find((w) => w.id === id) ?? null;
  });
  protected readonly kind = computed(() => {
    const widget = this.widget();
    return widget ? (kindOf(this.kinds, configOf(widget)) ?? null) : null;
  });
  protected readonly data = computed(() => {
    const widget = this.widget();
    const config = widget ? configOf(widget) : null;
    return config && isData(config) ? config : null;
  });
  protected readonly entity = computed(() => {
    const config = this.data();
    return this.store.draft().sources.find((s) => s.id === config?.source)?.entity ?? null;
  });

  /** The other widgets that choose slices, to listen to. */
  protected readonly emitters = computed(() => {
    const id = this.widget()?.id;
    return this.store
      .draft()
      .widgets.filter((w) => {
        const config = configOf(w);
        return w.id !== id && isData(config) && config.emits;
      })
      .map((w) => ({ id: w.id, name: this.store.nameOf(w.id) }));
  });

  /** The filters on sources linked to the widget's: those that could apply to it. */
  protected readonly filters = computed(() => {
    const config = this.data();
    if (!config) {
      return [];
    }
    const reached = reachedFrom(this.store.draft(), config.source);
    return this.store.draft().filters.filter((f) => reached.has(f.field.source));
  });

  protected titled(event: Event): void {
    const id = this.widget()!.id;
    const title = (event.target as HTMLInputElement).value;
    this.store.apply(
      `Renamed ${this.store.nameOf(id)}`,
      (d) => updateWidget(d, id, { title: title.trim() ? title : null }),
      `title:${id}`,
    );
  }

  protected configured(config: WidgetConfig): void {
    const id = this.widget()!.id;
    this.store.apply(
      `Changed ${this.store.nameOf(id)}`,
      (d) => updateWidget(d, id, { config }),
      `config:${id}`,
    );
  }

  protected setConfig(change: Partial<DataConfig>, verb: string, key: string | null = null): void {
    const widget = this.widget()!;
    this.store.apply(
      `${verb} ${this.store.nameOf(widget.id)}`,
      (d) =>
        updateWidget(d, widget.id, {
          config: { ...(configOf(widget) as DataConfig), ...change } as unknown as WidgetConfig,
        }),
      key ? `${key}:${widget.id}` : null,
    );
  }

  protected listened(listens: Listens): void {
    this.setConfig({ listens }, 'Changed what listens');
  }

  protected listenTo(listens: Listens, other: string, on: boolean): void {
    this.listened({
      ...listens,
      widgets: on ? [...listens.widgets, other] : listens.widgets.filter((w) => w !== other),
    });
  }

  protected applies(filter: string, on: boolean): void {
    const id = this.widget()!.id;
    this.store.apply(`Changed the filters of ${this.store.nameOf(id)}`, (d) =>
      setFilterApplies(d, filter, id, on),
    );
  }

  protected duplicate(): void {
    const id = this.widget()!.id;
    let made = id;
    this.store.apply(`Duplicated ${this.store.nameOf(id)}`, (d) => {
      const copied = duplicateWidget(d, id);
      made = copied.id;
      return copied.definition;
    });
    this.store.selected.set(made);
  }

  protected remove(): void {
    const id = this.widget()!.id;
    this.store.apply(`Removed ${this.store.nameOf(id)}`, (d) => removeWidget(d, id));
    this.store.selected.set(null);
  }

  protected hide(): void {
    const id = this.widget()!.id;
    const breakpoint = this.store.breakpoint();
    this.store.apply(`Hid ${this.store.nameOf(id)} at ${breakpoint.label}`, (d) =>
      hideAt(d, breakpoint.id, id, true),
    );
  }
}
