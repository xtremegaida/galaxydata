import { computed, inject, linkedSignal, type Signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { catchError, map, of } from 'rxjs';
import { ApiClient, type Schema } from '../api/api-client';
import { followCatalog } from './catalog-changes';
import { debounced } from '../ui/debounced';

type Entity = Schema<'EntityDto'>;

/** How many entities a search suggests. */
const suggested = 20;

/**
 * The entities the catalog finds for what is typed (as typing pauses): their paths, as queries write them, to
 * suggest. Nothing found, or a search that fails, suggests nothing; the item's check says what is wrong with a path.
 * Make it in an injection context.
 */
export class EntitySearch {
  private readonly api = inject(ApiClient);
  private readonly text: Signal<string>;
  private readonly found = rxResource({
    params: () => this.text() || undefined,
    stream: ({ params: text }) =>
      this.api.get('/api/catalog/tree/search', { query: { text, take: suggested } }).pipe(
        map((search) =>
          search.hits
            .filter(
              (hit) =>
                hit.node.kind !== 'source' &&
                hit.node.kind !== 'schema' &&
                hit.node.kind !== 'folder',
            )
            .map((hit) => hit.node.id),
        ),
        catchError(() => of([])),
      ),
  });

  /** The paths found for the text last searched; kept while the next text is searched, so they don't flicker. */
  readonly paths = linkedSignal<string[] | undefined, readonly string[]>({
    source: () => (this.found.hasValue() ? this.found.value() : this.text() ? undefined : []),
    computation: (paths, previous) => paths ?? previous?.value ?? [],
  });

  constructor(typed: () => string, wait: number) {
    this.text = debounced(() => typed().trim(), wait);
  }
}

/**
 * The entity a path names, as the catalog has it now (looked up as typing pauses, and again as the catalog changes),
 * for its columns and navigations to be suggested; null while it is looked up, and when there is none. Make it in
 * an injection context.
 */
export class EntityLookup {
  private readonly api = inject(ApiClient);
  private readonly path: Signal<string>;
  private readonly follow = followCatalog(() => this.found.reload());
  private readonly found = rxResource({
    params: () => this.path() || undefined,
    stream: ({ params: name }) =>
      this.api.get('/api/catalog/entity', { query: { name } }).pipe(this.follow()),
  });

  readonly entity: Signal<Entity | null> = computed(() =>
    this.found.hasValue() ? this.found.value() : null,
  );
  /** Its columns' names, in order. */
  readonly columns = computed(() => this.entity()?.columns.map((column) => column.name) ?? []);
  /** Its key's columns, when it has one. */
  readonly key = computed(() => this.entity()?.key?.columns ?? null);

  constructor(path: () => string, wait: number) {
    this.path = debounced(() => path().trim(), wait);
  }
}

/** The names to suggest for what is typed: those holding it, ignoring case; all of them when nothing is typed. */
export function suggestions(names: readonly string[], typed: string): readonly string[] {
  const text = typed.trim().toLowerCase();
  return text ? names.filter((name) => name.toLowerCase().includes(text)) : names;
}
