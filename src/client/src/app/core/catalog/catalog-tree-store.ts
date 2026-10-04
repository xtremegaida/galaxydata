import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../api/api-client';
import { ProblemCode, problemOf, type Problem } from '../api/problem';
import { CatalogVersion } from './catalog-version';

export type TreeNode = Schema<'TreeNodeDto'>;

/** A node as the tree shows it: where it is (its level, its parent, its place among its siblings), and how it stands. */
export interface TreeRow {
  readonly node: TreeNode;
  /** 1 for the top nodes (the sources). */
  readonly level: number;
  readonly parent: string | null;
  /** Its place among its siblings, from 1. */
  readonly position: number;
  readonly siblings: number;
  /** Whether it is open: its children shown, or being loaded to be. */
  readonly expanded: boolean;
  /** Whether its children are being loaded. */
  readonly loading: boolean;
  /** The source it is in (a source's own node), or null for what is in none (virtual entities). */
  readonly source: TreeNode | null;
}

/** A node that couldn't be opened, and why. */
export interface TreeFailure {
  readonly node: TreeNode;
  readonly problem: Problem;
}

/** Whether a source's schema is still to be read, or being read: the tree follows it until it is read. */
export function beingRead(node: TreeNode): boolean {
  return node.kind === 'source' && (node.status === 'loading' || node.status === 'notLoaded');
}

/**
 * The catalog's tree as the user has opened it: the nodes loaded (each node's children loaded when it is first
 * opened), those open, the one the keyboard is on and the entity shown. Kept while the user is elsewhere, so the tree
 * is as it was when they come back; loaded again, keeping what was open, when the catalog changes.
 */
@Injectable({ providedIn: 'root' })
export class CatalogTreeStore {
  private readonly api = inject(ApiClient);
  private readonly versions = inject(CatalogVersion);

  /** Each loaded node's children, by its id; the top nodes by null. */
  private readonly children = signal<ReadonlyMap<string | null, readonly TreeNode[]>>(new Map());
  private readonly opened = signal<ReadonlySet<string>>(new Set());
  private readonly pending = signal<ReadonlySet<string | null>>(new Set());
  private readonly rootProblem = signal<Problem | null>(null);
  private readonly failure = signal<TreeFailure | null>(null);
  /** The last load asked for of each node's children, so that an earlier one's answer doesn't replace its. */
  private readonly asked = new Map<string | null, number>();
  private asks = 0;
  /** Each node's last load (the one whose answer counts), for those an answer outdates to wait for. */
  private readonly loads = new Map<string | null, Promise<Problem | null>>();
  /** How many times the tree has been loaded again, and when each node's children were last asked for. */
  private generation = 0;
  private readonly loadedIn = new Map<string | null, number>();
  /** The catalog's version when the tree was last loaded, whole; null when it isn't known. */
  private loadedVersion: string | null = null;
  private starting: Promise<Problem | null> | null = null;
  private refreshing: Promise<void> | null = null;
  /** The catalog's version as the refresh under way began, which it catches up to. */
  private refreshingAt: string | null = null;
  private revealing: { readonly id: string; readonly done: Promise<boolean> } | null = null;
  private refreshAgain = false;

  /** The node the keyboard is on. */
  readonly active = signal<string | null>(null);
  /** The entity whose page is open. */
  readonly selected = signal<string | null>(null);

  /** Whether the top nodes have been loaded. */
  readonly loaded = computed(() => this.children().has(null));
  readonly roots = computed(() => this.children().get(null) ?? []);
  readonly loadingRoots = computed(() => this.pending().has(null));
  /** Why the top nodes couldn't be loaded, or loaded again (those loaded before stay). */
  readonly problem = this.rootProblem.asReadonly();
  /** The node that last couldn't be opened, until it is, or the failure is let go. */
  readonly lastFailure = this.failure.asReadonly();

  /** The nodes loaded, by id. */
  private readonly nodes = computed(() => {
    const nodes = new Map<string, TreeNode>();
    for (const list of this.children().values()) {
      for (const node of list) {
        nodes.set(node.id, node);
      }
    }
    return nodes;
  });

  /** Each loaded node's parent (null for the top ones). */
  private readonly parents = computed(() => {
    const parents = new Map<string, string | null>();
    for (const [parent, list] of this.children()) {
      for (const node of list) {
        parents.set(node.id, parent);
      }
    }
    return parents;
  });

  /** The nodes shown, in order: the top nodes, and under each node open its children. */
  readonly rows = computed<readonly TreeRow[]>(() => {
    const children = this.children();
    const opened = this.opened();
    const pending = this.pending();
    const rows: TreeRow[] = [];
    const add = (parent: string | null, level: number, source: TreeNode | null) => {
      const nodes = children.get(parent) ?? [];
      nodes.forEach((node, index) => {
        // Open, with its children loaded or being loaded (those let go are loaded as it is opened again).
        const expanded =
          node.hasChildren &&
          opened.has(node.id) &&
          (children.has(node.id) || pending.has(node.id));
        const within = node.kind === 'source' ? node : source;
        rows.push({
          node,
          level,
          parent,
          position: index + 1,
          siblings: nodes.length,
          expanded,
          loading: pending.has(node.id),
          source: within,
        });
        if (expanded) {
          add(node.id, level + 1, within);
        }
      });
    };
    add(null, 1, null);
    return rows;
  });

  /** A loaded node. */
  node(id: string): TreeNode | undefined {
    return this.nodes().get(id);
  }

  /** Loads the top nodes, unless they are loaded (or being loaded: that load is waited for). */
  async start(): Promise<void> {
    if (!this.loaded()) {
      const problem = await (this.starting ??= this.load(null).finally(
        () => (this.starting = null),
      ));
      if (!problem) {
        this.loadedVersion = this.versions.version();
      }
    }
  }

  /**
   * Loads the top nodes again (the sources, as their schemas are read); then the rest, when the catalog changed
   * since it was loaded.
   */
  async reloadRoots(): Promise<void> {
    if (!(await this.load(null))) {
      this.catchUp(this.versions.version());
    }
  }

  /** Tries again what failed: the whole tree once its top nodes are loaded, or them. */
  retry(): Promise<void> {
    return this.loaded() ? this.refresh() : this.start();
  }

  /**
   * The catalog is at `version` (as an answer said): when it was another as the tree was loaded (or that wasn't
   * known), the tree is loaded again.
   */
  catchUp(version: string | null): void {
    const caughtUp =
      version === this.loadedVersion || (this.refreshing !== null && version === this.refreshingAt);
    if (this.loaded() && version !== null && !caughtUp) {
      void this.refresh();
    }
  }

  /** Opens a node, loading its children when they aren't: false when it can't be opened. */
  async expand(id: string): Promise<boolean> {
    const node = this.node(id);
    if (!node?.hasChildren) {
      return false;
    }
    this.opened.update((opened) => withItem(opened, id));
    if (!this.children().has(id)) {
      const problem = await this.load(id);
      if (problem) {
        this.opened.update((opened) => withoutItem(opened, id));
        this.failure.set({ node, problem });
        return false;
      }
    }
    this.clearFailureOf(id);
    this.reopenUnder(id);
    return true;
  }

  /** Nodes left open under one, whose children were let go as the tree was loaded again, open again with it. */
  private reopenUnder(id: string): void {
    for (const child of this.children().get(id) ?? []) {
      if (
        child.hasChildren &&
        this.opened().has(child.id) &&
        !this.children().has(child.id) &&
        !this.pending().has(child.id)
      ) {
        void this.expand(child.id);
      }
    }
  }

  /** Closes a node; the keyboard, when it was on a node under it, goes to it. */
  collapse(id: string): void {
    this.opened.update((opened) => withoutItem(opened, id));
    const active = this.active();
    if (active !== null && this.ancestorsOf(active)?.includes(id)) {
      this.active.set(id);
    }
  }

  /** Lets the last failure to open a node go. */
  dismissFailure(): void {
    this.failure.set(null);
  }

  /**
   * Shows a node: opens its ancestors (by their ids, the top one first, as search gives them) and puts the keyboard
   * on it. False when it isn't there (any more).
   */
  reveal(id: string, ancestors: readonly string[]): Promise<boolean> {
    const done = this.revealAlong(id, ancestors);
    this.revealing = { id, done };
    void done.finally(() => {
      if (this.revealing?.done === done) {
        this.revealing = null;
      }
    });
    return done;
  }

  private async revealAlong(id: string, ancestors: readonly string[]): Promise<boolean> {
    await this.start();
    if (!this.loaded()) {
      return false;
    }
    for (const ancestor of ancestors) {
      if (!(await this.expand(ancestor))) {
        return false;
      }
    }
    // Children loaded before the node was made (a table added since) don't have it: they are loaded again.
    const parent = ancestors.at(-1) ?? null;
    if (!this.node(id) && (await this.load(parent))) {
      return false;
    }
    if (!this.node(id)) {
      return false;
    }
    this.active.set(id);
    return true;
  }

  /**
   * The entity whose page is open: shown in the tree, its ancestors opened. When it isn't loaded, search finds where
   * it is.
   */
  async select(id: string | null): Promise<void> {
    this.selected.set(id);
    if (id === null) {
      return;
    }
    // Chosen where it was found, it is being shown already.
    if (this.revealing?.id === id) {
      await this.revealing.done;
      return;
    }
    await this.start();
    const ancestors = this.ancestorsOf(id);
    if (ancestors) {
      await this.reveal(id, ancestors);
      return;
    }
    try {
      const found = await firstValueFrom(
        this.api.get('/api/catalog/tree/search', { query: { text: id, take: 20 } }),
      );
      const hit = found.hits.find((candidate) => candidate.node.id === id);
      if (hit && this.selected() === id) {
        await this.reveal(id, hit.path);
      }
    } catch {
      // Not found where it is: the page says what is wrong with it.
    }
  }

  /**
   * Loads again what is loaded: the top nodes, and the children of those open (which stay open while they are
   * there). Children it doesn't load again (under nodes closed) are let go, to be loaded as their nodes open. Asked
   * for while it runs, it runs again once it ends.
   */
  refresh(): Promise<void> {
    if (this.refreshing) {
      this.refreshAgain = true;
      return this.refreshing;
    }
    this.refreshingAt = this.versions.version();
    this.refreshing = this.reloadAll().finally(() => {
      this.refreshing = null;
      if (this.refreshAgain) {
        this.refreshAgain = false;
        void this.refresh();
      }
    });
    return this.refreshing;
  }

  private async reloadAll(): Promise<void> {
    const generation = ++this.generation;
    const before = this.children();
    const parentsBefore = this.parents();
    if (await this.load(null)) {
      return;
    }
    // The top nodes alone loaded again (as sources are read) leave the rest as it was: only this catches up.
    this.loadedVersion = this.versions.version();
    const gone = new Set<string>();
    const noticeGone = (parent: string | null) => {
      const now = new Set((this.children().get(parent) ?? []).map((node) => node.id));
      for (const node of before.get(parent) ?? []) {
        if (!now.has(node.id)) {
          gone.add(node.id);
        }
      }
    };
    const openUnder = (parent: string | null) =>
      (this.children().get(parent) ?? [])
        .filter((node) => node.hasChildren && this.opened().has(node.id))
        .map((node) => node.id);
    noticeGone(null);
    let level = openUnder(null);
    while (level.length > 0) {
      const loads = await Promise.all(level.map(async (id) => [id, await this.load(id)] as const));
      const next: string[] = [];
      for (const [id, problem] of loads) {
        if (problem?.code === ProblemCode.notFound) {
          gone.add(id);
          continue;
        }
        if (problem) {
          // One that couldn't be loaded again keeps the children it had.
          if (!this.children().has(id)) {
            continue;
          }
          this.loadedIn.set(id, generation);
        } else {
          noticeGone(id);
        }
        next.push(...openUnder(id));
      }
      level = next;
    }
    // Nodes gone close; children asked for before this began, and not since, are let go.
    this.opened.update((opened) => new Set([...opened].filter((id) => !gone.has(id))));
    this.children.update(
      (children) =>
        new Map(
          [...children].filter(
            ([id]) => id === null || (this.loadedIn.get(id) ?? -1) >= generation,
          ),
        ),
    );
    // The keyboard, on a node gone, goes to the nearest of its ancestors still there.
    let active = this.active();
    while (active !== null && !this.node(active)) {
      active = parentsBefore.get(active) ?? null;
    }
    this.active.set(active);
  }

  /** A loaded node's ancestors, the top one first; null when it isn't loaded. */
  private ancestorsOf(id: string): string[] | null {
    const parents = this.parents();
    if (!parents.has(id)) {
      return null;
    }
    const ancestors: string[] = [];
    for (
      let parent = parents.get(id) ?? null;
      parent !== null;
      parent = parents.get(parent) ?? null
    ) {
      ancestors.unshift(parent);
    }
    return ancestors;
  }

  /**
   * Loads a node's children (null: the top nodes): null when they were loaded, or why they weren't. A load whose
   * answer a later one's outdates gives the later one's outcome.
   */
  private load(parent: string | null): Promise<Problem | null> {
    const ask = ++this.asks;
    const askedIn = this.generation;
    this.asked.set(parent, ask);
    this.pending.update((pending) => withItem(pending, parent));
    const latest = () => this.asked.get(parent) === ask;
    const done = (async (): Promise<Problem | null> => {
      try {
        const answer = await firstValueFrom(
          this.api.get('/api/catalog/tree/children', parent === null ? {} : { query: { parent } }),
        );
        if (!latest()) {
          return await this.latestLoad(parent);
        }
        this.children.update((children) => new Map(children).set(parent, answer.nodes));
        this.loadedIn.set(parent, askedIn);
        if (parent === null) {
          this.rootProblem.set(null);
        }
        return null;
      } catch (error) {
        if (!latest()) {
          return await this.latestLoad(parent);
        }
        const problem = problemOf(error);
        if (parent === null) {
          this.rootProblem.set(problem);
        }
        return problem;
      } finally {
        if (latest()) {
          this.pending.update((pending) => withoutItem(pending, parent));
        }
      }
    })();
    this.loads.set(parent, done);
    return done;
  }

  private latestLoad(parent: string | null): Promise<Problem | null> {
    return this.loads.get(parent) ?? Promise.resolve(null);
  }

  private clearFailureOf(id: string): void {
    if (this.failure()?.node.id === id) {
      this.failure.set(null);
    }
  }
}

function withItem<T>(set: ReadonlySet<T>, item: T): ReadonlySet<T> {
  return set.has(item) ? set : new Set(set).add(item);
}

function withoutItem<T>(set: ReadonlySet<T>, item: T): ReadonlySet<T> {
  if (!set.has(item)) {
    return set;
  }
  const without = new Set(set);
  without.delete(item);
  return without;
}
