import type { ListRange } from '@angular/cdk/collections';
import {
  CdkFixedSizeVirtualScroll,
  CdkVirtualForOf,
  CdkVirtualScrollViewport,
} from '@angular/cdk/scrolling';
import {
  Component,
  DestroyRef,
  Injector,
  LOCALE_ID,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressSpinner } from '@angular/material/progress-spinner';
import { Router, RouterLink } from '@angular/router';
import { POLL_INTERVAL } from '../../core/api/poll';
import { isSessionProblem, problemMessage } from '../../core/api/problem';
import { AuthStore } from '../../core/auth/auth-store';
import { keepFocus } from '../../core/browser/keep-focus';
import {
  CatalogTreeStore,
  beingRead,
  type TreeNode,
  type TreeRow,
} from '../../core/catalog/catalog-tree-store';
import { CatalogVersion } from '../../core/catalog/catalog-version';
import { Message } from '../../core/ui/message';
import { compactCount, iconOf, isEntity, kindOf } from './tree-nodes';

/** How tall a row of the tree is, in pixels. */
export const treeRowHeight = 32;

/** How long typed letters make one word to find a node by, in milliseconds. */
const typeAheadWait = 700;

/**
 * The catalog's tree: the sources, their schemas and entities, each node's children loaded as it is opened. Only the
 * rows in view are rendered, so a schema of thousands of tables opens at once.
 *
 * It is a tree as WAI-ARIA describes one: one stop in the tab order, the arrow keys move through it (left and right
 * close and open), Home and End go to the ends, Enter opens an entity, and typing a name's first letters goes to it.
 * The node the keyboard is on is the tree's active descendant, as rows out of view aren't in the page.
 */
@Component({
  selector: 'gd-catalog-tree',
  imports: [
    CdkFixedSizeVirtualScroll,
    CdkVirtualForOf,
    CdkVirtualScrollViewport,
    MatButton,
    MatIcon,
    MatProgressSpinner,
    Message,
    RouterLink,
  ],
  templateUrl: './catalog-tree.html',
  styleUrl: './catalog-tree.scss',
})
export class CatalogTree {
  protected readonly store = inject(CatalogTreeStore);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthStore);
  private readonly injector = inject(Injector);
  private readonly locale = inject(LOCALE_ID);
  private readonly viewport = viewChild.required(CdkVirtualScrollViewport);
  private readonly domIds = new Map<string, string>();
  private typed = '';
  private typedAt = 0;

  /** Whether choosing an entity leaves focus in the tree (beside the page), rather than going to the page. */
  readonly keepFocus = input(true);
  /** An entity was chosen, and its page opens. */
  readonly chosen = output<TreeNode>();

  protected readonly rowHeight = treeRowHeight;
  protected readonly rows = this.store.rows;
  protected readonly activeIndex = computed(() => {
    const active = this.store.active();
    return active === null ? -1 : this.rows().findIndex((row) => row.node.id === active);
  });
  /** The rows rendered: only those in view (and a little more) are in the page. */
  private readonly rendered = signal<ListRange>({ start: 0, end: 0 });
  /** The node the keyboard is on, once its row is in the page: until it is, the tree itself has focus. */
  protected readonly activeDomId = computed(() => {
    const index = this.activeIndex();
    const { start, end } = this.rendered();
    return index < start || index >= end ? null : this.domId(this.rows()[index].node.id);
  });
  protected readonly navigationState = computed(() => (this.keepFocus() ? keepFocus : undefined));
  protected readonly canEditData = computed(() => this.auth.permissions().canEditData);
  protected readonly canAdmin = computed(() => this.auth.permissions().canAdmin);
  protected readonly failure = computed(() => {
    const failure = this.store.lastFailure();
    return failure && !isSessionProblem(failure.problem) ? failure : null;
  });
  protected readonly rootProblem = computed(() => {
    const problem = this.store.problem();
    return problem && !isSessionProblem(problem) ? problem : null;
  });
  protected readonly message = problemMessage;
  protected readonly iconOf = iconOf;
  protected readonly kindOf = kindOf;
  protected readonly isEntity = isEntity;

  constructor() {
    void this.store.start();
    // The catalog changed while the tree was elsewhere, or as it is shown: it is loaded again.
    const versions = inject(CatalogVersion);
    effect(() => {
      const version = versions.version();
      untracked(() => this.store.catchUp(version));
    });
    this.followSchemasRead(inject(POLL_INTERVAL));
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const viewport = this.viewport();
      this.rendered.set(viewport.getRenderedRange());
      viewport.renderedRangeStream
        .pipe(takeUntilDestroyed(destroyRef))
        .subscribe((range) => this.rendered.set(range));
    });
    // The node the keyboard is on is kept in view: when it moves, and as the tree is shown.
    effect(() => {
      this.store.active();
      untracked(() =>
        afterNextRender(() => this.scrollIntoView(this.activeIndex()), {
          injector: this.injector,
        }),
      );
    });
  }

  /** Puts focus on the tree. */
  focus(): void {
    this.viewport().elementRef.nativeElement.focus();
  }

  protected domId(id: string): string {
    let domId = this.domIds.get(id);
    if (domId === undefined) {
      domId = `gd-tree-node-${this.domIds.size + 1}`;
      this.domIds.set(id, domId);
    }
    return domId;
  }

  protected readonly trackRow = (_index: number, row: TreeRow) => row.node.id;

  protected rowsOf(node: TreeNode): string | null {
    return node.rows === null || node.rows === undefined
      ? null
      : `${compactCount(node.rows, this.locale)} ${node.rows === 1 ? 'row' : 'rows'}`;
  }

  protected exactRows(node: TreeNode): string | null {
    return node.rows === null || node.rows === undefined
      ? null
      : `About ${node.rows.toLocaleString(this.locale)} rows`;
  }

  /**
   * Whether to mark a table whose rows the user can't change, though they change data and its source takes changes
   * (one without a primary key): views and virtual entities are never changed, and a read-only source says so.
   */
  protected unchangeable(row: TreeRow): boolean {
    return (
      this.canEditData() &&
      row.node.kind === 'table' &&
      row.node.editable === false &&
      !row.source?.isReadOnly
    );
  }

  /** Focus came to the tree: the keyboard is on the entity shown, or the first node, unless on one already. */
  protected focused(): void {
    if (this.activeIndex() >= 0) {
      return;
    }
    const selected = this.store.selected();
    const rows = this.rows();
    const start = rows.find((row) => row.node.id === selected) ?? rows[0];
    if (start) {
      this.store.active.set(start.node.id);
    }
  }

  protected clicked(row: TreeRow, event: MouseEvent): void {
    this.store.active.set(row.node.id);
    if (!isEntity(row.node)) {
      this.toggle(row);
    } else if (event.defaultPrevented) {
      // Its link opened it.
      this.chosen.emit(row.node);
    } else if (!(event.target as Element).closest('a')) {
      this.open(row.node);
    }
  }

  /** A row pressed: focus goes to the tree (not the entity's link in it, whose row may scroll out of the page). */
  protected pressed(event: MouseEvent): void {
    if (event.button === 0) {
      event.preventDefault();
      this.focus();
    }
  }

  protected toggled(row: TreeRow, event: MouseEvent): void {
    event.stopPropagation();
    this.store.active.set(row.node.id);
    this.toggle(row);
  }

  protected retry(): void {
    const failure = this.store.lastFailure();
    if (failure) {
      void this.store.expand(failure.node.id);
    }
  }

  protected keydown(event: KeyboardEvent): void {
    const rows = this.rows();
    if (rows.length === 0 || event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    const index = Math.max(this.activeIndex(), 0);
    const row = rows[index];
    // A space in a name being typed is part of it.
    if (event.key === ' ' && this.typing()) {
      this.typeAhead(event.key, index);
      event.preventDefault();
      return;
    }
    switch (event.key) {
      case 'ArrowDown':
        this.moveTo(index + 1);
        break;
      case 'ArrowUp':
        this.moveTo(index - 1);
        break;
      case 'Home':
        this.moveTo(0);
        break;
      case 'End':
        this.moveTo(rows.length - 1);
        break;
      case 'PageDown':
        this.moveTo(index + this.pageSize());
        break;
      case 'PageUp':
        this.moveTo(index - this.pageSize());
        break;
      case 'ArrowRight':
        if (row.node.hasChildren && !row.expanded) {
          this.store.active.set(row.node.id);
          void this.store.expand(row.node.id);
        } else if (row.expanded && rows[index + 1]?.parent === row.node.id) {
          this.moveTo(index + 1);
        }
        break;
      case 'ArrowLeft':
        if (row.expanded) {
          this.store.collapse(row.node.id);
        } else if (row.parent !== null) {
          this.store.active.set(row.parent);
        }
        break;
      case 'Enter':
      case ' ':
        this.store.active.set(row.node.id);
        if (isEntity(row.node)) {
          this.open(row.node);
        } else {
          this.toggle(row);
        }
        break;
      case '*':
        for (const sibling of rows.filter((other) => other.parent === row.parent)) {
          if (sibling.node.hasChildren && !sibling.expanded) {
            void this.store.expand(sibling.node.id);
          }
        }
        break;
      default:
        if (event.key.length !== 1 || !this.typeAhead(event.key, index)) {
          return;
        }
    }
    event.preventDefault();
  }

  private toggle(row: TreeRow): void {
    if (row.expanded) {
      this.store.collapse(row.node.id);
    } else if (row.node.hasChildren) {
      void this.store.expand(row.node.id);
    }
  }

  private open(node: TreeNode): void {
    void this.router.navigate(['/browse', node.id], { state: this.navigationState() });
    this.chosen.emit(node);
  }

  private moveTo(index: number): void {
    const rows = this.rows();
    const row = rows[Math.min(Math.max(index, 0), rows.length - 1)];
    this.store.active.set(row.node.id);
  }

  private typing(): boolean {
    return this.typed !== '' && Date.now() - this.typedAt <= typeAheadWait;
  }

  /** Goes to the next node (after the one the keyboard is on) whose name starts with what was typed. */
  private typeAhead(key: string, index: number): boolean {
    const now = Date.now();
    this.typed = this.typing() ? this.typed + key : key;
    this.typedAt = now;
    const wanted = this.typed.toLowerCase();
    // The same letter typed again goes on to the next node it starts.
    const repeated = [...wanted].every((letter) => letter === wanted[0]);
    const rows = this.rows();
    for (let step = 1; step <= rows.length; step++) {
      // A word being typed may still be the node the keyboard is on.
      const candidate = (index + step - (repeated ? 0 : 1)) % rows.length;
      const name = rows[candidate].node.name.toLowerCase();
      if (name.startsWith(repeated ? wanted[0] : wanted)) {
        this.store.active.set(rows[candidate].node.id);
        return true;
      }
    }
    return true;
  }

  private pageSize(): number {
    return Math.max(Math.floor(this.viewport().getViewportSize() / treeRowHeight) - 1, 1);
  }

  private scrollIntoView(index: number): void {
    if (index < 0) {
      return;
    }
    const viewport = this.viewport();
    const top = index * treeRowHeight;
    const bottom = top + treeRowHeight;
    const offset = viewport.measureScrollOffset();
    const size = viewport.getViewportSize();
    if (top < offset) {
      viewport.scrollToOffset(top);
    } else if (bottom > offset + size) {
      viewport.scrollToOffset(bottom - size);
    }
  }

  /**
   * Loads the sources again while a schema is being read, until each is read; after a load that fails, waits twice
   * as long each time (to 30 seconds). A schema read changes the catalog's version, so the tree is loaded again.
   */
  private followSchemasRead(interval: number): void {
    let failures = 0;
    effect((onCleanup) => {
      if (this.store.loadingRoots()) {
        return;
      }
      const reading = this.store.roots().some(beingRead);
      if (!reading) {
        failures = 0;
        return;
      }
      failures = this.store.problem() ? failures + 1 : 0;
      const wait = failures ? Math.min(interval * 2 ** failures, 30_000) : interval;
      const timer = setTimeout(() => void this.store.reloadRoots(), wait);
      onCleanup(() => clearTimeout(timer));
    });
  }
}
