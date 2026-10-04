import { computed, signal, type Injector, type WritableSignal } from '@angular/core';
import {
  applyEach,
  form,
  required,
  validate,
  type FieldTree,
  type ReadonlyFieldTree,
  type SchemaPathTree,
} from '@angular/forms/signals';
import type { Schema } from '../../../core/api/api-client';

export type ConnectionKind = Schema<'ConnectionKindDto'>;
export type Connection = Schema<'ConnectionDto'>;
export type ConnectionInput = Schema<'ConnectionInput'>;
export type ConnectionMode = Schema<'ConnectionMode'>;
export type FieldDescriptor = Schema<'FieldDto'>;
export type GroupDescriptor = Schema<'GroupDto'>;
export type SecretInput = Schema<'SecretInput'>;

/**
 * A secret as it is edited: kept as stored, set to `text`, or cleared. `stored` says whether there is one stored
 * (secrets are never sent back, only whether they have a value).
 */
export interface SecretModel {
  action: Schema<'SecretAction'>;
  text: string;
  stored: boolean;
}

/** A setting the kind's form doesn't describe: any keyword the provider takes. */
export interface OtherSetting {
  key: string;
  value: string;
}

/** A connection as it is edited. */
export interface ConnectionModel {
  alias: string;
  displayName: string;
  mode: ConnectionMode;
  /** The described settings that aren't secrets, by keyword, as text; empty for the provider's default. */
  settings: Record<string, string>;
  /** Secrets by keyword: the described ones (passwords) and any other stored. */
  secrets: Record<string, SecretModel>;
  others: OtherSetting[];
  connectionString: string;
  /** The source's options, by key; empty for the default. */
  options: Record<string, string>;
  isReadOnly: boolean;
}

/** Words in a keyword that make it a secret, as the server reads them. */
const secretWords = ['password', 'pwd', 'token', 'secret'];

/** Whether the server takes a keyword for a secret's. */
export function isSecretKeyword(keyword: string): boolean {
  const lower = keyword.toLowerCase();
  return secretWords.some((word) => lower.includes(word));
}

/** The words of the language, which no alias may be. */
const words = ['and', 'or', 'not', 'in', 'true', 'false', 'null'];

const aliasRule =
  'An alias is a plain name: a letter or _, then letters, digits and _ (64 at most), and not a word the language has (and, or, not, in, true, false, null)';

/**
 * A connection being made or edited, in the form its kind describes (`GET /api/connection-kinds`): its settings
 * field by field or as a connection string, its secrets kept, set or cleared, and its options. It gives what the
 * API takes (`input()`), and says which of its fields a problem's errors are about (`errorFields()`).
 */
export class ConnectionDraft {
  readonly model: WritableSignal<ConnectionModel>;
  readonly form: FieldTree<ConnectionModel>;
  /** Whether it differs from the connection as it was read. */
  readonly dirty = computed(() => JSON.stringify(this.model()) !== this.initial);

  private readonly initial: string;
  /** The secrets a conversion to a connection string gave, sent with it. */
  private rawSecrets: Record<string, SecretInput> = {};

  constructor(
    readonly kind: ConnectionKind,
    readonly connection: Connection | null,
    injector: Injector,
  ) {
    const model = initialModel(kind, connection);
    this.initial = JSON.stringify(model);
    this.model = signal(model);
    this.form = form(this.model, (path) => this.rules(path), { injector });
  }

  /** Whether the connection is a new one. */
  get isNew(): boolean {
    return this.connection === null;
  }

  /** The kind's fields in a group, in order. */
  fieldsIn(group: GroupDescriptor): FieldDescriptor[] {
    const first = this.kind.groups[0]?.key;
    return this.kind.fields.filter((field) => (field.group ?? first) === group.key);
  }

  /** Whether a field is shown: always, or while the setting it depends on has one of its values. */
  visible(field: FieldDescriptor): boolean {
    const when = field.visibleWhen;
    if (!when) {
      return true;
    }
    const current = this.valueOf(when.field).toLowerCase();
    return when.values.some((value) => value.toLowerCase() === current);
  }

  /** A setting's value, or its field's default when it has none. */
  valueOf(keyword: string): string {
    const value = this.model().settings[keyword] ?? '';
    return value || (this.kind.fields.find((field) => field.key === keyword)?.default ?? '');
  }

  /** Whether a bool setting is on, its default when it isn't set. */
  flag(field: FieldDescriptor): boolean {
    return isTrue(this.valueOf(field.key));
  }

  /** Sets a bool setting: unset when it is the default, else as the default is written (`True`, `true`). */
  setFlag(field: FieldDescriptor, on: boolean): void {
    this.model.update((model) => ({
      ...model,
      settings: { ...model.settings, [field.key]: flagText(field, on) },
    }));
  }

  /** Whether a bool option is on, its default when it isn't set. */
  optionFlag(field: FieldDescriptor): boolean {
    return isTrue(this.model().options[field.key] || (field.default ?? ''));
  }

  setOptionFlag(field: FieldDescriptor, on: boolean): void {
    this.model.update((model) => ({
      ...model,
      options: { ...model.options, [field.key]: flagText(field, on) },
    }));
  }

  /** The stored secrets the form doesn't describe (a token in the other settings). */
  readonly otherSecrets = computed(() =>
    Object.keys(this.model().secrets).filter(
      (key) => !this.kind.fields.some((field) => field.key === key),
    ),
  );

  addOther(): void {
    this.model.update((model) => ({ ...model, others: [...model.others, { key: '', value: '' }] }));
  }

  removeOther(index: number): void {
    this.model.update((model) => ({
      ...model,
      others: model.others.filter((_, at) => at !== index),
    }));
  }

  setReadOnly(readOnly: boolean): void {
    this.model.update((model) => ({ ...model, isReadOnly: readOnly || this.kind.alwaysReadOnly }));
  }

  /**
   * The connection as the API takes it. In the form: the settings shown and not empty; the secrets set, cleared,
   * or kept (those stored, so a conversion masks them; a stored secret whose field is hidden is cleared, as the
   * server would keep it); and the other settings, secrets among them as secrets. As a connection string: it, with
   * the secrets its conversion gave that it still masks.
   */
  input(): ConnectionInput {
    const model = this.model();
    const options = Object.fromEntries(
      Object.entries(model.options).filter(([, value]) => value !== ''),
    );
    const isReadOnly = model.isReadOnly || this.kind.alwaysReadOnly;
    if (model.mode === 'raw') {
      const masked = maskedKeywords(model.connectionString);
      return {
        mode: 'raw',
        connectionString: model.connectionString,
        secrets: Object.fromEntries(
          Object.entries(this.rawSecrets).filter(([key]) => masked.has(key.toLowerCase())),
        ),
        options,
        isReadOnly,
      };
    }
    const settings: Record<string, string> = {};
    const secrets: Record<string, SecretInput> = {};
    for (const field of this.kind.fields) {
      if (field.type === 'keyValues' || field.type === 'password' || !this.visible(field)) {
        continue;
      }
      const value = (model.settings[field.key] ?? '').trim();
      if (value !== '') {
        settings[field.key] = value;
      }
    }
    for (const [key, secret] of Object.entries(model.secrets)) {
      const field = this.kind.fields.find((described) => described.key === key);
      const action = secretAction(secret, !field || this.visible(field));
      if (action) {
        secrets[key] = action;
      }
    }
    for (const other of model.others) {
      const key = other.key.trim();
      const value = other.value.trim();
      if (key && value) {
        if (isSecretKeyword(key)) {
          secrets[key] = { action: 'set', value };
        } else {
          settings[key] = value;
        }
      }
    }
    return { mode: 'form', settings, secrets, options, isReadOnly };
  }

  /**
   * Takes the settings a conversion gave (`POST /api/connection-kinds/{kind}/convert`), in its mode. Back in the
   * form, the connection string decides the secrets: one it masked is kept, one it wrote out is set, and a stored
   * one it left out is cleared (the server says so only of those it was told about).
   */
  converted(input: ConnectionInput): void {
    if (input.mode === 'raw') {
      this.rawSecrets = { ...(input.secrets ?? {}) };
      this.model.update((model) => ({
        ...model,
        mode: 'raw',
        connectionString: input.connectionString ?? '',
      }));
      return;
    }
    const stored = this.connection?.secrets ?? {};
    const { settings, others } = splitSettings(this.kind, input.settings ?? {});
    const secrets = emptySecrets(this.kind, stored);
    for (const [key, state] of Object.entries(stored)) {
      if (state.hasValue) {
        secrets[keyIn(secrets, key) ?? key] = { action: 'clear', text: '', stored: true };
      }
    }
    for (const [key, given] of Object.entries(input.secrets ?? {})) {
      const known = keyIn(secrets, key) ?? key;
      const has = secrets[known]?.stored ?? false;
      secrets[known] = {
        // A secret not stored can't be kept or cleared: it is set, by typing it.
        action: has ? given.action : 'set',
        text: given.action === 'set' ? (given.value ?? '') : '',
        stored: has,
      };
    }
    this.rawSecrets = {};
    this.model.update((model) => ({ ...model, mode: 'form', settings, secrets, others }));
  }

  /** The fields that a problem's errors (by the API's names: `settings.Host`) can be shown on. */
  errorFields(): Record<string, ReadonlyFieldTree<unknown>> {
    const fields: Record<string, ReadonlyFieldTree<unknown>> = {
      alias: this.form.alias,
      displayName: this.form.displayName,
      connectionString: this.form.connectionString,
    };
    for (const field of this.kind.fields) {
      if (field.type === 'password') {
        fields[`secrets.${field.key}`] = this.form.secrets[field.key].text;
      } else if (field.type !== 'bool' && field.type !== 'keyValues') {
        fields[`settings.${field.key}`] = this.form.settings[field.key];
      }
    }
    for (const option of this.kind.options) {
      if (option.type === 'select') {
        fields[`options.${option.key}`] = this.form.options[option.key];
      }
    }
    return fields;
  }

  /** The fields that can have errors, in their order on the page. */
  fieldsInOrder(): ReadonlyFieldTree<unknown>[] {
    const fields: ReadonlyFieldTree<unknown>[] = [this.form.alias, this.form.displayName];
    if (this.model().mode === 'raw') {
      fields.push(this.form.connectionString);
    } else {
      for (const field of this.kind.fields) {
        if (field.type === 'password') {
          fields.push(this.form.secrets[field.key].text);
        } else if (field.type === 'keyValues') {
          this.model().others.forEach((_, index) => fields.push(this.form.others[index].key));
        } else if (field.type !== 'bool') {
          fields.push(this.form.settings[field.key]);
        }
      }
    }
    for (const option of this.kind.options) {
      if (option.type === 'select') {
        fields.push(this.form.options[option.key]);
      }
    }
    return fields;
  }

  private rules(path: SchemaPathTree<ConnectionModel>): void {
    const inForm = () => this.model().mode === 'form';
    if (this.isNew) {
      required(path.alias, { message: 'Give an alias' });
      validate(path.alias, ({ value }) =>
        value() && (!/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(value()) || words.includes(value()))
          ? { kind: 'alias', message: aliasRule }
          : null,
      );
    }
    for (const field of this.kind.fields) {
      if (field.type === 'password') {
        const secret = path.secrets[field.key];
        validate(secret.text, ({ value, valueOf }) =>
          inForm() &&
          this.visible(field) &&
          valueOf(secret.stored) &&
          valueOf(secret.action) === 'set' &&
          !value()
            ? { kind: 'required', message: `Give the new ${field.label.toLowerCase()}, or keep it` }
            : null,
        );
      } else if (field.type !== 'bool' && field.type !== 'keyValues') {
        const setting = path.settings[field.key];
        if (field.required) {
          required(setting, {
            message: `${field.label} is needed`,
            when: () => inForm() && this.visible(field),
          });
        }
        if (field.type === 'number') {
          validate(setting, ({ value }) =>
            inForm() && this.visible(field) ? numberProblem(value(), field) : null,
          );
        }
      }
    }
    applyEach(path.others, (other) => {
      validate(other.key, ({ value, valueOf }) =>
        inForm() && !value().trim() && valueOf(other.value).trim()
          ? { kind: 'required', message: 'Name the setting' }
          : null,
      );
    });
    required(path.connectionString, {
      message: 'Write the connection string',
      when: () => !inForm(),
    });
  }
}

/** The model of a connection as read, or of a new one of a kind. */
function initialModel(kind: ConnectionKind, connection: Connection | null): ConnectionModel {
  const stored = connection?.secrets ?? {};
  const { settings, others } = splitSettings(kind, connection?.settings ?? {});
  const secrets = emptySecrets(kind, stored);
  for (const [key, state] of Object.entries(stored)) {
    if (state.hasValue && keyIn(secrets, key) === undefined) {
      secrets[key] = { action: 'keep', text: '', stored: true };
    }
  }
  const options: Record<string, string> = {};
  for (const option of kind.options) {
    options[option.key] = connection?.options[option.key] ?? '';
  }
  return {
    alias: connection?.alias ?? '',
    displayName: connection?.displayName ?? '',
    mode: kind.supportsRaw ? (connection?.mode ?? 'form') : 'form',
    settings,
    secrets,
    others,
    connectionString: connection?.connectionString ?? '',
    options,
    isReadOnly: kind.alwaysReadOnly || (connection?.isReadOnly ?? true),
  };
}

/** Settings by keyword: those the kind's fields describe, and the others. */
function splitSettings(
  kind: ConnectionKind,
  given: Record<string, string>,
): { settings: Record<string, string>; others: OtherSetting[] } {
  const settings: Record<string, string> = {};
  for (const field of kind.fields) {
    if (field.type !== 'password' && field.type !== 'keyValues') {
      settings[field.key] = '';
    }
  }
  const others: OtherSetting[] = [];
  for (const [key, value] of Object.entries(given)) {
    const described = keyIn(settings, key);
    if (described !== undefined) {
      settings[described] = value;
    } else {
      others.push({ key, value });
    }
  }
  return { settings, others };
}

/** The described secrets, kept when stored, to set when not. */
function emptySecrets(
  kind: ConnectionKind,
  stored: Record<string, Schema<'SecretStateDto'>>,
): Record<string, SecretModel> {
  const secrets: Record<string, SecretModel> = {};
  for (const field of kind.fields.filter((described) => described.type === 'password')) {
    const has = Object.entries(stored).some(
      ([key, state]) => key.toLowerCase() === field.key.toLowerCase() && state.hasValue,
    );
    secrets[field.key] = { action: has ? 'keep' : 'set', text: '', stored: has };
  }
  return secrets;
}

/**
 * What to do with a secret, as the API takes it; none when there is nothing to say. One not stored is set when
 * typed; one stored is kept, changed or cleared, and cleared when its field is hidden.
 */
function secretAction(secret: SecretModel, visible: boolean): SecretInput | null {
  if (!secret.stored) {
    return visible && secret.text ? { action: 'set', value: secret.text } : null;
  }
  if (!visible) {
    return { action: 'clear' };
  }
  switch (secret.action) {
    case 'set':
      return secret.text ? { action: 'set', value: secret.text } : null;
    case 'clear':
      return { action: 'clear' };
    default:
      return { action: 'keep' };
  }
}

/** The keywords a connection string writes as `********` (lower case). */
function maskedKeywords(connectionString: string): Set<string> {
  const masked = new Set<string>();
  for (const part of connectionString.split(';')) {
    const at = part.indexOf('=');
    if (at > 0 && part.slice(at + 1).trim() === '********') {
      masked.add(part.slice(0, at).trim().toLowerCase());
    }
  }
  return masked;
}

/** The key of a record that is `key` but for case (keywords ignore it, as providers do). */
function keyIn(record: Record<string, unknown>, key: string): string | undefined {
  if (Object.hasOwn(record, key)) {
    return key;
  }
  const lower = key.toLowerCase();
  return Object.keys(record).find((known) => known.toLowerCase() === lower);
}

function isTrue(value: string): boolean {
  return value.toLowerCase() === 'true';
}

/** A bool as a field's default is written: `True` for SQL Server's, `true` for the others; empty for the default. */
function flagText(field: FieldDescriptor, on: boolean): string {
  const fallback = field.default ?? 'false';
  if (on === isTrue(fallback)) {
    return '';
  }
  const capital = /^[A-Z]/.test(fallback);
  return on ? (capital ? 'True' : 'true') : capital ? 'False' : 'false';
}

function numberProblem(value: string, field: FieldDescriptor) {
  const text = value.trim();
  if (!text) {
    return null;
  }
  if (!/^\d+$/.test(text)) {
    return { kind: 'number', message: 'Give a whole number' };
  }
  const number = Number(text);
  if (field.min != null && number < field.min) {
    return { kind: 'min', message: `At least ${field.min}` };
  }
  if (field.max != null && number > field.max) {
    return { kind: 'max', message: `At most ${field.max}` };
  }
  return null;
}
