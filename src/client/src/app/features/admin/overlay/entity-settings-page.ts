import { DatePipe } from '@angular/common';
import { Component, computed, signal } from '@angular/core';
import {
  FormField,
  FormRoot,
  applyEach,
  form,
  maxLength,
  readonly,
  required,
  validate,
  type ReadonlyFieldTree,
} from '@angular/forms/signals';
import { MatAutocomplete, MatAutocompleteTrigger } from '@angular/material/autocomplete';
import { MatAnchor, MatButton, MatIconButton } from '@angular/material/button';
import { MatCheckbox } from '@angular/material/checkbox';
import { MatOption } from '@angular/material/core';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { Message } from '../../../core/ui/message';
import { EntityStructure } from '../../browse/entity-structure';
import { EntityLookup, EntitySearch, suggestions } from './catalog-lookups';
import { OverlayCheckPanel } from './overlay-check';
import { OverlayItemPage } from './overlay-item-page';
import {
  namesOf,
  trimmed,
  type ColumnSetting,
  type EntitySettings,
  type EntitySettingsInput,
  type OverlayItemKind,
} from './overlay-items';

/** A column's settings as edited. */
interface ColumnModel {
  name: string;
  hidden: boolean;
  label: string;
  type: string;
}

interface SettingsModel {
  entity: string;
  hidden: boolean;
  displayColumn: string;
  key: string[];
  /** The settings of columns, those with something set (and those emptied, which aren't sent). */
  columns: ColumnModel[];
}

/** A row of the columns' table: a column of the entity, or one the settings name that it hasn't. */
interface ColumnRow {
  readonly name: string;
  /** Its type as the database declares it (or as the query makes it). */
  readonly type: string | null;
  readonly setting: ColumnModel | null;
  /** Whether the entity hasn't the column (its settings are left out). */
  readonly gone: boolean;
}

/** Types a column may be read as, to suggest. */
const types = [
  'string',
  'int32',
  'int64',
  'decimal(12,2)',
  'double',
  'boolean',
  'date',
  'datetime',
  'datetimeoffset',
  'time',
  'guid',
  'json',
];

/** Whether a column's settings set anything. */
function setsAnything(column: ColumnModel): boolean {
  return column.hidden || column.label.trim().length > 0 || column.type.trim().length > 0;
}

/**
 * An entity's settings, made or edited: the entity (suggested from the catalog), whether it is hidden, the column
 * that shows its rows, a declared key, and each column's settings (hidden, a label, a type it is read as), in a
 * table of the entity's columns; tried as they are edited (the entity as they make it).
 */
@Component({
  selector: 'gd-entity-settings-page',
  imports: [
    DatePipe,
    EntityStructure,
    FormField,
    FormRoot,
    MatAnchor,
    MatAutocomplete,
    MatAutocompleteTrigger,
    MatButton,
    MatCheckbox,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatOption,
    MatProgressBar,
    MatSlideToggle,
    Message,
    OverlayCheckPanel,
    RouterLink,
  ],
  templateUrl: './entity-settings-page.html',
  styleUrls: ['../admin-page.scss', './overlay-page.scss'],
})
export class EntitySettingsPage extends OverlayItemPage<
  EntitySettings,
  SettingsModel,
  EntitySettingsInput
> {
  protected readonly kind: OverlayItemKind = 'entitySettings';
  protected readonly bodyField = 'settings';
  protected readonly deleteMessage = 'The entity is as its database declares it again.';
  protected readonly suggest = suggestions;
  protected readonly types = types;

  protected readonly form = form(
    this.model,
    (path) => {
      readonly(path, () => this.saving());
      required(path.entity, { message: 'Name the entity' });
      maxLength(path.entity, 400, { message: 'A path has 400 characters at most' });
      maxLength(path.displayColumn, 200, { message: "A column's name has 200 characters at most" });
      applyEach(path.key, (column) => {
        // Spaces alone are no name either.
        required(column, { message: 'Name the column, or remove it' });
        validate(column, ({ value }) =>
          value() && !value().trim()
            ? { kind: 'required', message: 'Name the column, or remove it' }
            : null,
        );
        maxLength(column, 200, { message: "A column's name has 200 characters at most" });
      });
    },
    {
      submission: {
        action: () => this.save(),
        onInvalid: () => this.focusFirstWrong(),
      },
    },
  );

  protected readonly entitySearch = new EntitySearch(() => this.model().entity, this.waits.lookup);
  protected readonly entity = new EntityLookup(() => this.model().entity, this.waits.lookup);

  /**
   * The entity's columns, then the columns the settings name that it hasn't (or that aren't known, the entity not
   * being in the catalog), which are kept till they are dropped.
   */
  protected readonly columnRows = computed<ColumnRow[]>(() => {
    const settings = this.model().columns;
    const entity = this.entity.entity();
    const known = entity?.columns ?? [];
    const rows: ColumnRow[] = known.map((column) => ({
      name: column.name,
      type: column.nativeType ?? column.type.text,
      setting: settings.find((setting) => setting.name === column.name) ?? null,
      gone: false,
    }));
    for (const setting of settings) {
      if (!known.some((column) => column.name === setting.name)) {
        rows.push({ name: setting.name, type: null, setting, gone: entity !== null });
      }
    }
    return rows;
  });

  /** The name of another column to give settings, as typed. */
  protected readonly another = signal('');

  /** The entity as the settings would make it. */
  protected readonly made = computed(() => this.shownCheck()?.entity ?? null);

  /** Changes a column's settings (adding them for a column that had none). */
  protected setColumn(name: string, change: Partial<Omit<ColumnModel, 'name'>>): void {
    this.model.update((model) => {
      const columns = model.columns.some((column) => column.name === name)
        ? model.columns.map((column) => (column.name === name ? { ...column, ...change } : column))
        : [...model.columns, { name, hidden: false, label: '', type: '', ...change }];
      return { ...model, columns };
    });
  }

  /** Drops the settings of a column the entity hasn't; the keyboard goes to naming another. */
  protected dropColumn(name: string): void {
    this.model.update((model) => ({
      ...model,
      columns: model.columns.filter((column) => column.name !== name),
    }));
    this.keepKeyboard('[data-at="another-column"]');
  }

  /** Gives settings to a column the table doesn't list (or finds the row of one it does), the keyboard on its label. */
  protected addColumn(event?: Event): void {
    event?.preventDefault();
    const name = this.another().trim();
    if (!name || this.saving()) {
      return;
    }
    if (!this.columnRows().some((row) => row.name === name)) {
      this.model.update((model) => ({
        ...model,
        columns: [...model.columns, { name, hidden: false, label: '', type: '' }],
      }));
    }
    this.another.set('');
    this.keepKeyboard(`[data-at="label-${CSS.escape(name)}"]`);
  }

  /** Adds a key column, the keyboard in it. */
  protected addKeyColumn(): void {
    const at = this.model().key.length;
    this.model.update((model) => ({ ...model, key: [...model.key, ''] }));
    this.keepKeyboard(`[data-at="key-${at}"]`);
  }

  /** Removes a key column, the keyboard going to the one in its place (or before; or to adding one). */
  protected removeKeyColumn(index: number): void {
    this.model.update((model) => ({ ...model, key: model.key.filter((_, at) => at !== index) }));
    const left = this.model().key.length;
    this.keepKeyboard(
      left > 0 ? `[data-at="key-${Math.min(index, left - 1)}"]` : '[data-at="add-key"]',
    );
  }

  /** Another item shown: the column being named was for the one before. */
  protected override shown(): void {
    this.another.set('');
  }

  protected typedIn(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected modelOf(settings: EntitySettings | undefined): SettingsModel {
    return {
      entity: settings?.entity ?? '',
      hidden: settings?.hidden ?? false,
      displayColumn: settings?.displayColumn ?? '',
      key: [...(settings?.key ?? [])],
      columns: (settings?.columns ?? []).map((column) => ({
        name: column.name,
        hidden: column.hidden,
        label: column.label ?? '',
        type: column.type ?? '',
      })),
    };
  }

  protected inputOf(model: SettingsModel): EntitySettingsInput {
    return {
      entity: model.entity.trim(),
      key: namesOf(model.key),
      displayColumn: trimmed(model.displayColumn),
      hidden: model.hidden,
      columns: model.columns.filter(setsAnything).map((column): ColumnSetting => ({
        name: column.name,
        hidden: column.hidden,
        label: trimmed(column.label),
        type: trimmed(column.type),
      })),
    };
  }

  protected needs(model: SettingsModel): string | null {
    return model.entity.trim() ? null : 'Name the entity, and they are tried as you go.';
  }

  protected errorFields(): Readonly<Record<string, ReadonlyFieldTree<unknown>>> {
    const fields: Record<string, ReadonlyFieldTree<unknown>> = {
      entity: this.form.entity,
      displayColumn: this.form.displayColumn,
    };
    this.model().key.forEach((_, at) => (fields[`key[${at}]`] = this.form.key[at]));
    return fields;
  }

  /** A column's errors (`columns[2].type`) say which column they are about. */
  protected override unplacedMessage(field: string, message: string): string {
    const at = /^columns\[(\d+)\]/.exec(field);
    const sent = at ? this.inputOf(this.model()).columns?.[Number(at[1])] : undefined;
    return sent ? `${sent.name}: ${message}` : message;
  }

  protected fieldsInOrder(): ReadonlyFieldTree<unknown>[] {
    return [
      this.form.entity,
      this.form.displayColumn,
      ...this.model().key.map((_, at) => this.form.key[at]),
    ];
  }

  protected takenField(): ReadonlyFieldTree<unknown> | null {
    return this.form.entity;
  }

  protected read(id: number) {
    return this.api.get('/api/overlay/entity-settings/{id}', { path: { id } });
  }

  protected create(input: EntitySettingsInput) {
    return this.api.post('/api/overlay/entity-settings', { body: input });
  }

  protected update(id: number, input: EntitySettingsInput, version: number) {
    return this.api.put('/api/overlay/entity-settings/{id}', {
      path: { id },
      body: { settings: input, version },
    });
  }

  protected remove(id: number, version: number) {
    return this.api.delete('/api/overlay/entity-settings/{id}', {
      path: { id },
      query: { version },
    });
  }

  protected tryIt(input: EntitySettingsInput, id: number | null) {
    return this.api.post('/api/overlay/entity-settings/validate', {
      query: { id: id ?? undefined },
      body: input,
    });
  }
}
