import {
  Component,
  ElementRef,
  InjectionToken,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatFormField, MatLabel, MatPrefix, MatSuffix } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { ApiClient, type Schema } from '../../core/api/api-client';
import { isSessionProblem, problemMessage, problemOf } from '../../core/api/problem';
import { keepFocus } from '../../core/browser/keep-focus';
import { CatalogTreeStore, type TreeNode } from '../../core/catalog/catalog-tree-store';
import { debounced } from '../../core/ui/debounced';
import { Message } from '../../core/ui/message';
import { CatalogTree } from './catalog-tree';
import { iconOf, isEntity, kindOf, matchParts } from './tree-nodes';

type TreeHit = Schema<'TreeHitDto'>;

/** How many nodes a search asks for at first, and at most. */
export const firstHits = 50;
export const mostHits = 200;

/** How long typing must pause before the search asks, in milliseconds. */
export const SEARCH_WAIT = new InjectionToken<number>('SEARCH_WAIT', { factory: () => 250 });

/**
 * The catalog beside the pages: a search, and the tree. What is typed is looked for in the names of the catalog's
 * nodes (and in paths, and columns); the nodes found are listed in place of the tree, and the one chosen is shown in
 * it, its ancestors opened (an entity's page opens too).
 *
 * The search field is a combobox: the arrow keys go through what was found while focus stays in the field, Enter
 * chooses, Escape clears.
 */
@Component({
  selector: 'gd-catalog-panel',
  imports: [
    CatalogTree,
    MatButton,
    MatFormField,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatPrefix,
    MatProgressBar,
    MatSuffix,
    Message,
  ],
  templateUrl: './catalog-panel.html',
  styleUrl: './catalog-panel.scss',
})
export class CatalogPanel {
  private readonly api = inject(ApiClient);
  private readonly store = inject(CatalogTreeStore);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly tree = viewChild(CatalogTree);
  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');

  /** Whether choosing an entity leaves focus in the catalog (beside the page), rather than going to the page. */
  readonly keepFocus = input(true);
  /** An entity was chosen, and its page opens. */
  readonly chosen = output<TreeNode>();

  protected readonly text = signal('');
  protected readonly searching = computed(() => this.text().trim() !== '');
  private readonly wanted = debounced(() => this.text().trim(), inject(SEARCH_WAIT));
  protected readonly take = linkedSignal({ source: this.wanted, computation: () => firstHits });
  protected readonly search = rxResource({
    params: () => {
      const text = this.wanted();
      return text ? { text, take: this.take() } : undefined;
    },
    stream: ({ params }) => this.api.get('/api/catalog/tree/search', { query: params }),
  });
  /** What was found: kept while the search asks again, as it does while typing. */
  protected readonly found = linkedSignal<
    Schema<'TreeSearchDto'> | undefined,
    Schema<'TreeSearchDto'> | undefined
  >({
    source: () => (this.search.hasValue() ? this.search.value() : undefined),
    computation: (found, previous) =>
      found ?? (this.search.isLoading() ? previous?.value : undefined),
  });
  protected readonly hits = computed(() => (this.searching() ? (this.found()?.hits ?? []) : []));
  /** The node found that the arrow keys are on; -1 before they are used. */
  protected readonly activeHit = linkedSignal({ source: this.found, computation: () => -1 });
  protected readonly waiting = computed(
    () => this.search.isLoading() || this.text().trim() !== this.wanted(),
  );
  protected readonly status = computed(() => {
    const found = this.found();
    if (!this.searching() || !found || this.waiting()) {
      return '';
    }
    const count = found.hits.length;
    if (count === 0) {
      return `Nothing in the catalog has “${found.text}” in its name.`;
    }
    if (found.more) {
      return `The first ${count} found.`;
    }
    return count === 1 ? '1 found.' : `${count} found.`;
  });
  protected readonly problem = computed(() => {
    const error = this.searching() ? this.search.error() : undefined;
    const problem = error ? problemOf(error) : null;
    return problem && !isSessionProblem(problem) ? problem : null;
  });
  /** Whether Enter was pressed before the search answered, until more is typed. */
  private readonly enterPending = linkedSignal({ source: this.text, computation: () => false });
  protected readonly message = problemMessage;
  protected readonly iconOf = iconOf;
  protected readonly kindOf = kindOf;
  protected readonly mostHits = mostHits;

  constructor() {
    effect(() => {
      if (!this.enterPending() || this.waiting()) {
        return;
      }
      const hit = this.problem() ? undefined : this.hits()[0];
      untracked(() => {
        this.enterPending.set(false);
        if (hit) {
          void this.choose(hit);
        }
      });
    });
  }

  protected typed(event: Event): void {
    this.text.set((event.target as HTMLInputElement).value);
  }

  protected clear(): void {
    this.text.set('');
  }

  protected parts(text: string) {
    return matchParts(text, this.wanted());
  }

  protected hitId(index: number): string {
    return `gd-catalog-hit-${index}`;
  }

  protected keydown(event: KeyboardEvent): void {
    const hits = this.hits();
    switch (event.key) {
      case 'ArrowDown':
        if (hits.length > 0) {
          this.activeHit.set(Math.min(this.activeHit() + 1, hits.length - 1));
        }
        break;
      case 'ArrowUp':
        if (hits.length > 0) {
          this.activeHit.set(Math.max(this.activeHit() - 1, 0));
        }
        break;
      case 'Enter': {
        if (!this.searching()) {
          return;
        }
        // Typed and chosen before the search answered: the first node it finds is chosen.
        if (this.waiting()) {
          this.enterPending.set(true);
          break;
        }
        const hit = hits[Math.max(this.activeHit(), 0)];
        if (hit) {
          void this.choose(hit);
        }
        break;
      }
      case 'Escape':
        if (!this.text()) {
          return;
        }
        // Cleared, the search stays: the catalog doesn't close over the page.
        event.stopPropagation();
        this.clear();
        break;
      default:
        return;
    }
    event.preventDefault();
  }

  /** Shows a node found in the tree, its ancestors opened; an entity's page opens. */
  protected async choose(hit: TreeHit): Promise<void> {
    this.clear();
    const entity = isEntity(hit.node);
    const revealing = this.store.reveal(hit.node.id, hit.path);
    if (entity) {
      void this.router.navigate(['/browse', hit.node.id], {
        state: this.keepFocus() ? keepFocus : undefined,
      });
      this.chosen.emit(hit.node);
    }
    if (!entity || this.keepFocus()) {
      afterNextRender(() => this.tree()?.focus(), { injector: this.injector });
    }
    if ((await revealing) && !entity) {
      await this.store.expand(hit.node.id);
    }
  }

  /** Asks for more of what was found; focus goes back to the field, as the button goes once they are found. */
  protected more(): void {
    this.take.set(mostHits);
    this.field().nativeElement.focus();
  }
}
