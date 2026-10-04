import { DOCUMENT } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  LOCALE_ID,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ApiClient, type Schema } from '../../core/api/api-client';
import { ProblemCode, isSessionProblem, problemMessage, problemOf } from '../../core/api/problem';
import { followCatalog } from '../../core/catalog/catalog-changes';
import { Message } from '../../core/ui/message';

type Entity = Schema<'EntityDto'>;
type EntityColumn = Schema<'EntityColumnDto'>;
type Navigation = Schema<'NavigationDto'>;
type Capabilities = Schema<'CapabilitiesDto'>;

/** An entity, as read for the name in the address. */
interface Shown {
  readonly name: string;
  readonly entity: Entity;
}

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
 * An entity's page: what it is, its columns (their types, keys, and what may be done with them), the navigations to
 * the rows its rows are linked to, and for a virtual entity its query. Read again when the catalog changes.
 */
@Component({
  selector: 'gd-entity-page',
  imports: [MatButton, MatIcon, MatProgressBar, Message, RouterLink],
  templateUrl: './entity-page.html',
  styleUrls: ['./browse-page.scss', './entity-page.scss'],
})
export class EntityPage {
  private readonly api = inject(ApiClient);
  private readonly locale = inject(LOCALE_ID);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  /** The entity's name as queries write it (the address's). */
  readonly entity = input.required<string>();

  private readonly followed = followCatalog(() => this.described.reload());
  protected readonly described = rxResource({
    params: () => ({ name: this.entity() }),
    stream: ({ params }) =>
      this.api.get('/api/catalog/entity', { query: params }).pipe(this.followed()),
  });
  /** The entity read: kept while it is read again (the catalog changed), not while another is read. */
  protected readonly shown = linkedSignal<Shown | undefined, Shown | undefined>({
    source: () =>
      this.described.hasValue()
        ? { name: this.entity(), entity: this.described.value() }
        : undefined,
    computation: (shown, previous) =>
      shown ?? (previous?.value?.name === this.entity() ? previous.value : undefined),
  });
  protected readonly problem = computed(() => {
    const error = this.described.error();
    const problem = error ? problemOf(error) : null;
    return problem && !isSessionProblem(problem) ? problem : null;
  });
  protected readonly notFound = computed(() => this.problem()?.code === ProblemCode.notFound);
  protected readonly changes = computed(() => {
    const entity = this.shown()?.entity;
    return entity ? describeChanges(entity.capabilities) : [];
  });
  /** Whether any of the entity's rows may be changed or added: then each column says what may be done with it. */
  protected readonly editable = computed(() => {
    const capabilities = this.shown()?.entity.capabilities;
    return !!capabilities && (capabilities.canUpdate || capabilities.canInsert);
  });
  protected readonly message = problemMessage;
  protected readonly describe = describeEntity;
  protected readonly throughOf = throughOf;
  protected readonly multiplicities = multiplicities;

  constructor() {
    this.focusWhenReplaced(inject(DOCUMENT), inject(Injector));
  }

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

  /**
   * Another entity shown in place of this one (a navigation's link followed, an entity chosen in the catalog over
   * the page): focus goes to its heading, from the link lost with the page, or the button that opened the catalog.
   * Focus in the catalog beside the page (its tree, its search) stays.
   */
  private focusWhenReplaced(document: Document, injector: Injector): void {
    let shownName: string | null = null;
    effect(() => {
      const name = this.shown()?.name ?? null;
      if (name === null || name === shownName) {
        return;
      }
      const replaced = shownName !== null;
      shownName = name;
      if (!replaced) {
        return;
      }
      untracked(() =>
        afterNextRender(
          () => {
            const focused = document.activeElement;
            if (!focused?.closest('gd-catalog-panel')) {
              this.heading()?.nativeElement.focus();
            }
          },
          { injector },
        ),
      );
    });
  }
}
