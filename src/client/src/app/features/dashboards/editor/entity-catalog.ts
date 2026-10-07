import {
  Injectable,
  type Signal,
  type WritableSignal,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { ApiClient, type Schema } from '../../../core/api/api-client';
import { CatalogVersion } from '../../../core/catalog/catalog-version';

export type Entity = Schema<'EntityDto'>;
export type EntityColumn = Schema<'EntityColumnDto'>;
export type TypeDto = Schema<'TypeDto'>;

/**
 * The entities the editor has looked at (its sources', and those their fields lead through), each read once and
 * again when the catalog changes. The editor page provides it.
 */
@Injectable()
export class EntityCatalog {
  private readonly api = inject(ApiClient);
  private readonly versions = inject(CatalogVersion);
  private readonly entries = new Map<string, WritableSignal<Entity | null | undefined>>();
  /** The catalog's version the entities were read at. */
  private readAt: string | null = null;

  constructor() {
    effect(() => {
      const version = this.versions.version();
      if (version !== null && this.readAt !== null && version !== this.readAt) {
        untracked(() => this.entries.forEach((entry, name) => this.read(name, entry)));
      }
    });
  }

  /** An entity by its name as queries write it: undefined while it is read, null when there is none to read. */
  entity(name: string): Signal<Entity | null | undefined> {
    let entry = this.entries.get(name);
    if (!entry) {
      entry = signal<Entity | null | undefined>(undefined);
      this.entries.set(name, entry);
      this.read(name, entry);
    }
    return entry.asReadonly();
  }

  /**
   * A field's column, through its navigations from an entity: undefined while what it leads through is read, null
   * when there is no such column. Read in a reactive context, it follows the entities as they are read.
   */
  columnOf(
    entity: string | null,
    field: { path: readonly string[]; column: string } | null,
  ): EntityColumn | null | undefined {
    if (!entity || !field?.column) {
      return null;
    }
    let at = this.entity(entity)();
    for (const step of field.path) {
      if (!at) {
        return at;
      }
      const navigation = at.navigations.find((n) => n.name === step && n.multiplicity !== 'many');
      if (!navigation) {
        return null;
      }
      at = this.entity(navigation.target)();
    }
    return at ? (at.columns.find((c) => c.name === field.column) ?? null) : at;
  }

  private read(name: string, entry: WritableSignal<Entity | null | undefined>): void {
    this.api.get('/api/catalog/entity', { query: { name } }).subscribe({
      next: (entity) => {
        this.readAt = this.versions.version();
        entry.set(entity);
      },
      error: () => entry.set(null),
    });
  }
}

const numeric = new Set(['int16', 'int32', 'int64', 'decimal', 'single', 'double']);
const dated = new Set(['date', 'dateTime', 'dateTimeOffset']);

/** Which types a field may be, by what it is for. */
export type FieldUse = 'any' | 'number' | 'ordered' | 'dimension';

export function fieldAllowed(type: TypeDto, use: FieldUse): boolean {
  switch (use) {
    case 'number':
      return numeric.has(type.kind);
    case 'ordered':
      return type.kind !== 'boolean' && type.kind !== 'binary' && type.kind !== 'guid';
    case 'dimension':
      return type.kind !== 'binary';
    case 'any':
      return true;
  }
}

export function isNumeric(type: TypeDto): boolean {
  return numeric.has(type.kind);
}

export function isDated(type: TypeDto): boolean {
  return dated.has(type.kind);
}
