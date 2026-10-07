import { computed, signal } from '@angular/core';

/** An edit as the history keeps it: what it was called, and what was before it. */
interface Entry<T> {
  readonly label: string;
  readonly before: T;
  /** Edits of the same key soon after one another are one entry: typing, a drag. */
  readonly key: string | null;
  readonly at: number;
}

/**
 * Undo and redo of a value edited a step at a time: at most `size` steps back. Steps with the same key within
 * `coalesce` milliseconds of each other are one (what is typed into a field, a widget dragged).
 */
export class History<T> {
  private readonly past = signal<readonly Entry<T>[]>([]);
  private readonly future = signal<readonly Entry<T>[]>([]);

  /** What undo would undo, and redo redo, by name. */
  readonly undoLabel = computed(() => this.past().at(-1)?.label ?? null);
  readonly redoLabel = computed(() => this.future().at(-1)?.label ?? null);

  constructor(
    private readonly now: () => number = () => Date.now(),
    private readonly size = 100,
    private readonly coalesce = 1000,
  ) {}

  /** Keeps `before` as what an edit changed (unless it joins the one before), and forgets what was undone. */
  record(label: string, before: T, key: string | null = null): void {
    const at = this.now();
    const last = this.past().at(-1);
    this.future.set([]);
    if (key !== null && last?.key === key && at - last.at < this.coalesce) {
      // The same edit going on: the value before it stays the one before its first step.
      this.past.set([...this.past().slice(0, -1), { ...last, label, at }]);
      return;
    }
    this.past.set([...this.past(), { label, before, key, at }].slice(-this.size));
  }

  /** The value before the last edit, `current` kept for redo; null when there is nothing to undo. */
  undo(current: T): { value: T; label: string } | null {
    const last = this.past().at(-1);
    if (!last) {
      return null;
    }
    this.past.set(this.past().slice(0, -1));
    this.future.set([...this.future(), { label: last.label, before: current, key: null, at: 0 }]);
    return { value: last.before, label: last.label };
  }

  /** The value the last undo went back from, `current` kept for undo; null when there is nothing to redo. */
  redo(current: T): { value: T; label: string } | null {
    const next = this.future().at(-1);
    if (!next) {
      return null;
    }
    this.future.set(this.future().slice(0, -1));
    this.past.set([...this.past(), { label: next.label, before: current, key: null, at: 0 }]);
    return { value: next.before, label: next.label };
  }

  /** Nothing to undo or redo: what is saved anew starts the history. */
  clear(): void {
    this.past.set([]);
    this.future.set([]);
  }
}
