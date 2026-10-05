import { HttpErrorResponse } from '@angular/common/http';
import {
  DestroyRef,
  Directive,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
  untracked,
  viewChildren,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import type { ReadonlyFieldTree, ValidationError } from '@angular/forms/signals';
import { MatAutocompleteTrigger } from '@angular/material/autocomplete';
import { Router } from '@angular/router';
import { firstValueFrom, map, throwError, type Observable } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { ProblemCode, problemMessage, problemOf, type Problem } from '../../../core/api/problem';
import { followCatalog } from '../../../core/catalog/catalog-changes';
import { fieldErrors, focusFirstInvalid } from '../../../core/forms/server-errors';
import { Notifier } from '../../../core/notify/notifier';
import { PageTitle } from '../../../core/page-titles';
import { Confirmer } from '../../../core/ui/confirmer';
import { warnBeforeUnload, type HasUnsavedChanges } from '../../../core/ui/unsaved-changes';
import {
  OVERLAY_WAITS,
  itemName,
  itemTitle,
  kindNouns,
  kindPaths,
  type OverlayCheck,
  type OverlayIssue,
  type OverlayItem,
  type OverlayItemKind,
} from './overlay-items';

/** What every stored item has. */
interface Stored {
  readonly id: number;
  readonly version: number;
  readonly issues: readonly OverlayIssue[];
}

/** What trying an item gave, for the input (as text) it was tried with. */
interface Tried {
  readonly text: string;
  readonly check: OverlayCheck;
}

/**
 * What the overlay's item pages share: the item read (none for a new one), edited in a form of the kind's, tried
 * against the catalog as editing pauses (in place of the item as saved), saved (made, or to the version read),
 * deleted, its changes undone, read again. A kind says how its item is read, sent and tried, and which fields the
 * server's errors go on.
 *
 * The page stays as the address goes to another item of its kind (a link to an item it would break): what it said
 * of one item goes, and what was asked for one item is left aside when it answers.
 */
@Directive()
export abstract class OverlayItemPage<
  TItem extends Stored & OverlayItem,
  TModel,
  TInput,
> implements HasUnsavedChanges {
  protected readonly api = inject(ApiClient);
  protected readonly router = inject(Router);
  protected readonly injector = inject(Injector);
  protected readonly waits = inject(OVERLAY_WAITS);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly notifier = inject(Notifier);
  private readonly confirmer = inject(Confirmer);
  private readonly pageTitle = inject(PageTitle);
  private readonly triggers = viewChildren(MatAutocompleteTrigger);

  /** The item's id (the route's); none for a new item. */
  readonly id = input<string>();

  /** What the address gives a new item to start with (`?entity=`); an item read is as it was saved. */
  protected given(value: string | undefined): string | undefined {
    return this.id() === undefined ? value : undefined;
  }

  /** The kind of item. */
  protected abstract readonly kind: OverlayItemKind;
  /** The field of an update's body that has the item (`relation`): the server names an update's errors under it. */
  protected abstract readonly bodyField: string;
  /** What deleting it does, said before it is deleted. */
  protected abstract readonly deleteMessage: string;

  protected abstract read(id: number): Observable<TItem>;
  protected abstract create(input: TInput): Observable<TItem>;
  protected abstract update(id: number, input: TInput, version: number): Observable<TItem>;
  protected abstract remove(id: number, version: number): Observable<unknown>;
  protected abstract tryIt(input: TInput, id: number | null): Observable<OverlayCheck>;
  /**
   * What the form edits: the item's, or a new item's (started with what the address gives). It is called as the form
   * is made, among the page's fields: it may use the page's inputs, which the pages declare first, but no other field.
   */
  protected abstract modelOf(item: TItem | undefined): TModel;
  /** What is sent for what the form holds: names trimmed, blanks left out or null. */
  protected abstract inputOf(model: TModel): TInput;
  /** What the item needs before it can be tried ("Name the entity…"), or null when it can be. */
  protected abstract needs(model: TModel): string | null;
  /** The form's fields by the names the API gives their errors (`from`, `fromColumns[0]`). */
  protected abstract errorFields(): Readonly<Record<string, ReadonlyFieldTree<unknown>>>;
  /** The form's fields, in their order on the page: the first wrong one is focused. */
  protected abstract fieldsInOrder(): ReadonlyFieldTree<unknown>[];
  /** The field an item for the same thing (`overlay-item-exists`) is said on; none to say it above the form. */
  protected abstract takenField(): ReadonlyFieldTree<unknown> | null;

  /** The item's number; NaN when the address's id isn't one (none is asked for). */
  private readonly number = computed(() => {
    const id = this.id();
    return id === undefined ? null : /^\d{1,9}$/.test(id) ? Number(id) : NaN;
  });
  protected readonly item = rxResource({
    params: () => {
      const id = this.number();
      return id === null ? undefined : { id };
    },
    stream: ({ params: { id } }) =>
      Number.isNaN(id)
        ? throwError(
            () =>
              new HttpErrorResponse({
                status: 404,
                error: {
                  code: ProblemCode.notFound,
                  title: `There is no such ${kindNouns[this.kind]}`,
                  detail: `'${this.id()}' isn't the number of one`,
                },
              }),
          )
        : this.read(id),
  });
  /** The item as read, or saved. */
  protected readonly stored = computed(() =>
    this.item.hasValue() ? this.item.value() : undefined,
  );
  protected readonly loadProblem = computed(() => {
    const error = this.item.error();
    return error ? problemOf(error) : null;
  });
  /** Whether the page has what it edits: a new item, or the item read. */
  protected readonly ready = computed(() => !this.id() || this.stored() !== undefined);
  protected readonly heading = computed(() => {
    const stored = this.stored();
    return stored ? itemName(this.kind, stored) : `New ${kindNouns[this.kind]}`;
  });

  /** What is edited: the item as read, until changed. */
  protected readonly model = linkedSignal(() => this.modelOf(this.stored()));

  protected readonly problem = signal<Problem | null>(null);
  /** Errors by field that no field shows. */
  protected readonly unplaced = signal<readonly string[]>([]);
  protected readonly busy = signal(false);
  /** Whether the item is being saved: what is edited meanwhile would be lost, so it can't be (`readonly`). */
  protected readonly saving = signal(false);
  /** Whether something is being done to the item: its actions wait. */
  protected readonly working = computed(() => this.busy() || this.saving());
  protected readonly message = problemMessage;
  private saved = false;
  private destroyed = false;
  /** Counts the items shown: what was asked for one before is left aside. */
  private shows = 0;

  /** What would be sent, as text: what is tried. */
  private readonly inputText = computed(() => JSON.stringify(this.inputOf(this.model())));
  /** Whether there are changes not saved (a key column added, not named yet, too). */
  protected readonly dirty = computed(
    () =>
      this.ready() && JSON.stringify(this.model()) !== JSON.stringify(this.modelOf(this.stored())),
  );

  /** What the item needs before it can be tried. */
  protected readonly needed = computed(() => (this.ready() ? this.needs(this.model()) : null));
  /** What is to be tried, once editing pauses: none while the item can't be. */
  private readonly toTry = computed(() =>
    this.ready() && this.needed() === null ? this.inputText() : null,
  );
  /** What was to be tried when editing last paused, and the item it was to be tried in place of. */
  private readonly settled = signal<{ readonly text: string; readonly id: number | null } | null>(
    null,
  );
  private readonly followChecks = followCatalog(() => this.check.reload());
  protected readonly check = rxResource({
    params: () => this.settled() ?? undefined,
    stream: ({ params }) =>
      this.tryIt(JSON.parse(params.text) as TInput, params.id).pipe(
        this.followChecks(),
        map((check): Tried => ({ text: params.text, check })),
      ),
  });
  /** What trying it gave last: kept while it is tried again, so what is said doesn't flicker. */
  private readonly lastTried = linkedSignal<Tried | undefined, Tried | null>({
    source: () => (this.check.hasValue() ? this.check.value() : undefined),
    computation: (tried, previous) => tried ?? previous?.value ?? null,
  });
  /** Why trying the item as it is now failed. */
  protected readonly checkProblem = computed(() => {
    const error = this.check.error();
    return error && this.settled()?.text === this.toTry() ? problemOf(error) : null;
  });
  /** What trying it gave, shown while it is tried again; none while it can't be tried, or trying it failed. */
  protected readonly shownCheck = computed(() =>
    this.toTry() === null || this.checkProblem() ? null : (this.lastTried()?.check ?? null),
  );
  /** The item as it was when what is shown was tried. */
  protected readonly triedInput = computed(() => {
    const tried = this.lastTried();
    return tried ? (JSON.parse(tried.text) as TInput) : null;
  });
  /** Whether what is shown was tried with the item as it is now. */
  protected readonly checkedNow = computed(
    () => this.toTry() !== null && this.lastTried()?.text === this.toTry(),
  );
  protected readonly checking = computed(
    () => this.toTry() !== null && !this.checkedNow() && this.checkProblem() === null,
  );
  /** Why the item can't be tried as it is (what the server refuses in it, by field), when it says. */
  protected readonly checkReasons = computed(() =>
    Object.entries(this.checkProblem()?.errors ?? {}).flatMap(([field, messages]) =>
      messages.map((message) => this.unplacedMessage(field, message)),
    ),
  );

  constructor() {
    warnBeforeUnload(() => this.hasUnsavedChanges());
    // Another item shown (or the first): what was said of the one before goes.
    effect(() => {
      this.id();
      untracked(() => {
        this.shows++;
        this.saved = false;
        this.busy.set(false);
        this.saving.set(false);
        this.clearProblems();
        this.shown();
      });
    });
    // The item is tried as editing pauses; as read (or saved), at once. What is tried says the item it goes in place
    // of as it was then: another being read (none yet) isn't tried in place of none.
    effect((onCleanup) => {
      const text = this.toTry();
      const id = this.stored()?.id ?? null;
      if (text === null) {
        this.settled.set(null);
        return;
      }
      const wait = untracked(() => this.dirty()) ? this.waits.check : 0;
      const timer = setTimeout(() => this.settled.set({ text, id }), wait);
      onCleanup(() => clearTimeout(timer));
    });
    // A saved item's name is the page's, before its kind's.
    effect(() => {
      const stored = this.stored();
      untracked(() => this.pageTitle.detail.set(stored ? itemName(this.kind, stored) : null));
    });
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.pageTitle.detail.set(null);
    });
  }

  hasUnsavedChanges(): boolean {
    return !this.saved && this.dirty();
  }

  /** Another item is shown (the first too): a kind forgets what it held of the one before. */
  protected shown(): void {
    // Nothing, by default.
  }

  /** Forgets the changes. */
  protected undo(): void {
    this.clearProblems();
    this.model.set(this.modelOf(this.stored()));
  }

  /** Reads the item again, forgetting the changes (once the administrator is sure). */
  protected async reload(): Promise<void> {
    if (
      this.dirty() &&
      !(await this.confirmer.confirm({
        title: 'Read it again?',
        message: 'The changes made here will be lost.',
        confirm: 'Read it again',
        destructive: true,
      }))
    ) {
      return;
    }
    this.clearProblems();
    this.item.reload();
    // The button is gone with its message: the keyboard goes to the page's heading.
    this.keepKeyboard('h1');
  }

  /** Tries the item again, after trying it failed. */
  protected tryAgain(): void {
    this.check.reload();
  }

  protected async delete(): Promise<void> {
    const stored = this.stored();
    if (!stored) {
      return;
    }
    const title = itemTitle(this.kind, stored, stored.id);
    const sure = await this.confirmer.confirm({
      title: `Delete ${title.charAt(0).toLowerCase()}${title.slice(1)}?`,
      message: this.deleteMessage,
      confirm: 'Delete',
      destructive: true,
    });
    if (!sure) {
      return;
    }
    const shows = this.shows;
    this.clearProblems();
    this.busy.set(true);
    try {
      await firstValueFrom(this.remove(stored.id, stored.version));
      if (this.movedOn(shows)) {
        return;
      }
      this.saved = true;
      this.notifier.say('Deleted.');
      await this.router.navigate(['/admin/overlay']);
    } catch (error) {
      if (!this.movedOn(shows)) {
        this.problem.set(problemOf(error));
      }
    } finally {
      if (!this.movedOn(shows)) {
        this.busy.set(false);
      }
    }
  }

  /** Saves the item: makes it, or saves it to the version read. What the server refuses goes on its fields. */
  protected async save(): Promise<ValidationError.WithFieldTree[]> {
    const stored = this.stored();
    if (!this.ready()) {
      return [];
    }
    const shows = this.shows;
    this.clearProblems();
    this.saving.set(true);
    const input = this.inputOf(this.model());
    try {
      if (!stored) {
        const made = await firstValueFrom(this.create(input));
        if (this.movedOn(shows)) {
          return [];
        }
        this.saved = true;
        this.notifier.say('Saved.');
        await this.router.navigate(['/admin/overlay', kindPaths[this.kind], made.id], {
          replaceUrl: true,
        });
      } else {
        const updated = await firstValueFrom(this.update(stored.id, input, stored.version));
        if (this.movedOn(shows)) {
          return [];
        }
        this.item.set(updated);
        this.notifier.say('Saved.');
      }
      return [];
    } catch (error) {
      return this.movedOn(shows) ? [] : this.refusal(problemOf(error), stored !== undefined);
    } finally {
      if (!this.movedOn(shows)) {
        this.saving.set(false);
      }
    }
  }

  /** Whether a problem is that someone else changed the item since it was read. */
  protected stale(problem: Problem): boolean {
    return problem.code === ProblemCode.concurrencyConflict;
  }

  /** A message of the server's about a field the form hasn't, said above it: as it is, unless a kind says more. */
  protected unplacedMessage(_field: string, message: string): string {
    return message;
  }

  /** Puts the keyboard on the first of the form's fields that is wrong (its suggestions closed, not over its error). */
  protected focusFirstWrong(): void {
    focusFirstInvalid(this.fieldsInOrder());
    this.closeSuggestions();
  }

  /** Closes what the fields suggest. */
  protected closeSuggestions(): void {
    for (const trigger of this.triggers()) {
      trigger.closePanel();
    }
  }

  /** Puts the keyboard, once the page shows what was done, on the first element `selector` finds in it. */
  protected keepKeyboard(selector: string): void {
    afterNextRender(
      () => {
        this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus();
        this.closeSuggestions();
      },
      { injector: this.injector },
    );
  }

  /** Whether the page went on to another item, or away, since `shows`. */
  private movedOn(shows: number): boolean {
    return this.destroyed || shows !== this.shows;
  }

  /** What the server refused: on the fields it was about, the rest above the form. */
  private refusal(problem: Problem, update: boolean): ValidationError.WithFieldTree[] {
    const taken = problem.code === ProblemCode.overlayItemExists ? this.takenField() : null;
    if (taken) {
      return this.onFields([
        { kind: 'server', message: problem.detail ?? problem.title, fieldTree: taken },
      ]);
    }
    // An update's fields are named under its item (`relation.from`).
    const prefix = `${this.bodyField}.`;
    const named: Problem =
      update && problem.errors
        ? {
            ...problem,
            errors: Object.fromEntries(
              Object.entries(problem.errors).map(([field, messages]) => [
                field.startsWith(prefix) ? field.slice(prefix.length) : field,
                messages,
              ]),
            ),
          }
        : problem;
    const { errors, others } = fieldErrors(named, this.errorFields(), (field, message) =>
      this.unplacedMessage(field, message),
    );
    if (errors.length === 0 || others.length > 0) {
      this.problem.set(problem);
      this.unplaced.set(others);
    }
    return this.onFields(errors);
  }

  private onFields(errors: ValidationError.WithFieldTree[]): ValidationError.WithFieldTree[] {
    if (errors.length > 0) {
      afterNextRender(() => this.focusFirstWrong(), { injector: this.injector });
    }
    return errors;
  }

  private clearProblems(): void {
    this.problem.set(null);
    this.unplaced.set([]);
  }
}
