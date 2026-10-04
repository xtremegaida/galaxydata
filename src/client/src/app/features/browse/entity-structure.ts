import { Component, LOCALE_ID, computed, inject, input } from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import type { Schema } from '../../core/api/api-client';
import { entityUrl } from '../../core/browse/browse-url';
import { Message } from '../../core/ui/message';

export type Entity = Schema<'EntityDto'>;
type EntityColumn = Schema<'EntityColumnDto'>;
type Navigation = Schema<'NavigationDto'>;
type Capabilities = Schema<'CapabilitiesDto'>;

const multiplicities: Readonly<Record<Schema<'Multiplicity'>, string>> = {
  one: 'one',
  zeroOrOne: 'one or none',
  many: 'many',
};

/** What an entity is, in words: a table or view of a source, or a virtual entity. */
export function describeEntity(entity: Entity): string {
  if (entity.kind === 'virtual') {
    return 'A virtual entity: the rows of a query, as the catalog defines it';
  }
  const what = entity.kind === 'view' ? 'A view' : 'A table';
  return entity.source ? `${what} of ${entity.source}` : what;
}

/** What the user may do with an entity's rows, and why not, in sentences. */
export function describeChanges(capabilities: Capabilities): string[] {
  const allowed = [
    capabilities.canInsert ? 'add' : null,
    capabilities.canUpdate ? 'change' : null,
    capabilities.canDelete ? 'delete' : null,
  ].filter((verb) => verb !== null);
  const sentences: string[] = [];
  if (allowed.length > 0) {
    const verbs =
      allowed.length === 1
        ? allowed[0]
        : `${allowed.slice(0, -1).join(', ')} and ${allowed[allowed.length - 1]}`;
    sentences.push(`You may ${verbs} its rows.`);
  }
  if (capabilities.changeReason) {
    sentences.push(sentence(capabilities.changeReason));
  }
  if (capabilities.insertReason && capabilities.insertReason !== capabilities.changeReason) {
    sentences.push(sentence(`No rows can be added: ${capabilities.insertReason}`));
  }
  return sentences;
}

/** The columns a navigation goes through, each with the one it meets: `customer_id → id`. */
export function throughOf(navigation: Navigation): string {
  return navigation.columns
    .map((column, index) => `${column} → ${navigation.targetColumns[index] ?? '?'}`)
    .join(', ');
}

function sentence(text: string): string {
  return /[.!?]$/.test(text) ? text : `${text}.`;
}

/**
 * What an entity is: its facts (full name, rows, keys, the column that shows its rows, triggers, what the user may do
 * with its rows), its columns (their types, keys, and what may be done with them), the navigations to the rows its
 * rows are linked to, and for a virtual entity its query.
 */
@Component({
  selector: 'gd-entity-structure',
  imports: [MatIcon, Message, RouterLink],
  templateUrl: './entity-structure.html',
  styleUrls: ['./browse-shared.scss', './entity-structure.scss'],
})
export class EntityStructure {
  private readonly locale = inject(LOCALE_ID);

  readonly entity = input.required<Entity>();

  protected readonly changes = computed(() => describeChanges(this.entity().capabilities));
  /** Whether any of the entity's rows may be changed or added: then each column says what may be done with it. */
  protected readonly editable = computed(() => {
    const capabilities = this.entity().capabilities;
    return capabilities.canUpdate || capabilities.canInsert;
  });
  protected readonly throughOf = throughOf;
  protected readonly multiplicities = multiplicities;
  protected readonly entityUrl = entityUrl;

  protected rows(count: number): string {
    return `About ${count.toLocaleString(this.locale)} ${count === 1 ? 'row' : 'rows'}, as the database estimates`;
  }

  protected flagsOf(column: EntityColumn): string[] {
    return [
      column.isIdentity ? 'identity' : null,
      column.isComputed ? 'computed' : null,
      column.hasDefault ? 'default' : null,
      column.isRowVersion ? 'row version' : null,
      column.hidden ? 'hidden' : null,
    ].filter((flag) => flag !== null);
  }

  protected notesOf(navigation: Navigation): string[] {
    return [
      navigation.isInverse ? 'from the other side' : null,
      navigation.origin === 'overlay' ? 'added in the catalog' : null,
      navigation.isEnforced ? null : 'not enforced',
      navigation.isCrossSource ? 'another connection' : null,
      navigation.hidden ? 'hidden' : null,
      navigation.inherited ? "its query's entity's" : null,
    ].filter((note) => note !== null);
  }

  protected insertOf(column: EntityColumn): string | null {
    switch (column.insert) {
      case 'required':
        return 'Needed in a new row';
      case 'never':
        return 'Not given in a new row';
      default:
        return null;
    }
  }
}
