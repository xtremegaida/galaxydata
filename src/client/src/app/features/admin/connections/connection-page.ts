import { DecimalPipe } from '@angular/common';
import {
  Component,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormField, submit, type ValidationError } from '@angular/forms/signals';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatButtonToggle, MatButtonToggleGroup } from '@angular/material/button-toggle';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatProgressSpinner } from '@angular/material/progress-spinner';
import { MatOption, MatSelect } from '@angular/material/select';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../../../core/api/api-client';
import { POLL_INTERVAL } from '../../../core/api/poll';
import { ProblemCode, problemMessage, problemOf, type Problem } from '../../../core/api/problem';
import { fieldErrors, focusFirstInvalid } from '../../../core/forms/server-errors';
import { Notifier } from '../../../core/notify/notifier';
import { Confirmer } from '../../../core/ui/confirmer';
import { Message } from '../../../core/ui/message';
import { warnBeforeUnload, type HasUnsavedChanges } from '../../../core/ui/unsaved-changes';
import {
  ConnectionDraft,
  type Connection,
  type ConnectionMode,
  type FieldDescriptor,
} from './connection-draft';
import { ConnectionFields } from './connection-fields';
import { reading } from './connection-list';
import { ConnectionSnapshots } from './connection-snapshots';
import { SchemaStatus } from '../../../core/catalog/schema-status';

type TestResult = Schema<'ConnectionTestDto'>;

/**
 * A connection, made or edited: its kind's form (or its connection string), its secrets and options, tried before
 * it is saved; then its schema, read when it is saved and on asking (followed while it is read), with its last
 * readings; and deleting it.
 */
@Component({
  selector: 'gd-connection-page',
  imports: [
    ConnectionFields,
    ConnectionSnapshots,
    DecimalPipe,
    FormField,
    MatAnchor,
    MatButton,
    MatButtonToggle,
    MatButtonToggleGroup,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatInput,
    MatLabel,
    MatOption,
    MatProgressBar,
    MatProgressSpinner,
    MatSelect,
    MatSlideToggle,
    Message,
    RouterLink,
    SchemaStatus,
  ],
  templateUrl: './connection-page.html',
  styleUrls: ['../admin-page.scss', './connection-page.scss'],
})
export class ConnectionPage implements HasUnsavedChanges {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);
  private readonly confirmer = inject(Confirmer);
  private readonly injector = inject(Injector);
  private readonly pollInterval = inject(POLL_INTERVAL);

  /** The connection's id (the route's); none for a new connection. */
  readonly id = input<string>();

  protected readonly kinds = rxResource({ stream: () => this.api.get('/api/connection-kinds') });
  protected readonly connection = rxResource({
    params: () => (this.id() ? Number(this.id()) : undefined),
    stream: ({ params: id }) => this.api.get('/api/connections/{id}', { path: { id } }),
  });

  /** The connection as read (or saved): what the draft starts from. */
  protected readonly stored = computed(() =>
    this.connection.hasValue() ? this.connection.value() : undefined,
  );
  /** The connection as last looked at, while its schema is read. */
  private readonly live = signal<Connection | null>(null);
  /** Counts saves and readings: a look at the schema begun before the last is left aside. */
  private generation = 0;
  /** Looks at the schema that failed in a row: the next waits longer. */
  private readonly failedLooks = signal(0);
  protected readonly current = computed(() => this.live() ?? this.stored());
  protected readonly chosenKind = signal<string | null>(null);
  private readonly revision = signal(0);

  protected readonly kindList = computed(() => (this.kinds.hasValue() ? this.kinds.value() : []));
  protected readonly kind = computed(() => {
    const id = this.stored()?.kind ?? this.chosenKind();
    return this.kindList().find((kind) => kind.id === id) ?? null;
  });

  /** The connection being edited, made afresh from what was read (or for the kind chosen). */
  protected readonly draft = computed(() => {
    this.revision();
    const kind = this.kind();
    const stored = this.stored();
    if (!kind || (this.id() && !stored)) {
      return null;
    }
    return untracked(() => new ConnectionDraft(kind, stored ?? null, this.injector));
  });

  protected readonly loadProblem = computed(() => {
    const error = this.connection.error() ?? this.kinds.error();
    return error ? problemOf(error) : null;
  });
  protected readonly problem = signal<Problem | null>(null);
  /** Errors by field that no field shows. */
  protected readonly unplaced = signal<readonly string[]>([]);
  protected readonly busy = signal(false);
  protected readonly converting = signal(false);
  protected readonly testing = signal(false);
  protected readonly testResult = signal<TestResult | null>(null);
  /** Whether someone else changed the connection since it was read. */
  protected readonly changedElsewhere = computed(() => {
    const live = this.live();
    const stored = this.stored();
    return !!live && !!stored && live.version !== stored.version;
  });
  /** Whether something is being done to the connection (its actions wait). */
  protected readonly working = computed(
    () => this.busy() || this.converting() || (this.draft()?.form().submitting() ?? false),
  );
  protected readonly message = problemMessage;
  protected readonly reading = reading;
  private saved = false;

  constructor() {
    warnBeforeUnload(() => this.hasUnsavedChanges());
    // While the schema is read, look again now and then (longer after looks that failed).
    effect((onCleanup) => {
      const shown = this.current();
      const failed = this.failedLooks();
      if (!shown || !reading(shown)) {
        return;
      }
      const wait = Math.min(this.pollInterval * 2 ** failed, 30_000);
      const timer = setTimeout(() => void this.look(shown.id), wait);
      onCleanup(() => clearTimeout(timer));
    });
  }

  hasUnsavedChanges(): boolean {
    return !this.saved && (this.draft()?.dirty() ?? false);
  }

  protected async choose(kind: string, select: MatSelect): Promise<void> {
    const draft = this.draft();
    if (
      draft?.dirty() &&
      !(await this.confirmer.confirm({
        title: 'Change the kind?',
        message: 'The settings given so far will be lost.',
        confirm: 'Change it',
      }))
    ) {
      select.value = this.chosenKind();
      return;
    }
    this.chosenKind.set(kind);
    this.testResult.set(null);
    this.clearProblems();
  }

  protected async switchMode(
    draft: ConnectionDraft,
    to: ConnectionMode,
    modes: MatButtonToggleGroup,
  ): Promise<void> {
    if (draft.model().mode === to) {
      return;
    }
    this.clearProblems();
    this.converting.set(true);
    try {
      draft.converted(
        await firstValueFrom(
          this.api.post('/api/connection-kinds/{kind}/convert', {
            path: { kind: draft.kind.id },
            body: { connection: draft.input(), to },
          }),
        ),
      );
    } catch (error) {
      modes.value = draft.model().mode;
      // Not a submission, so no field shows its errors: they are all said above the form.
      const problem = problemOf(error);
      this.problem.set(problem);
      this.unplaced.set(Object.values(problem.errors ?? {}).flat());
    } finally {
      this.converting.set(false);
    }
  }

  protected async save(draft: ConnectionDraft, event?: Event): Promise<void> {
    event?.preventDefault();
    await submit(draft.form, {
      action: () => this.store(draft),
      onInvalid: () => focusFirstInvalid(draft.fieldsInOrder()),
    });
  }

  protected async test(draft: ConnectionDraft): Promise<void> {
    this.testResult.set(null);
    await submit(draft.form, {
      action: () => this.tryIt(draft),
      onInvalid: () => focusFirstInvalid(draft.fieldsInOrder()),
    });
  }

  /** Forgets the changes. */
  protected undo(): void {
    this.clearProblems();
    this.testResult.set(null);
    this.revision.update((revision) => revision + 1);
  }

  /** Reads the connection again, forgetting the changes (once the administrator is sure). */
  protected async reload(): Promise<void> {
    if (
      this.draft()?.dirty() &&
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
    this.testResult.set(null);
    this.generation++;
    this.live.set(null);
    this.connection.reload();
  }

  protected async refresh(connection: Connection): Promise<void> {
    await this.act(async () => {
      this.live.set(
        await firstValueFrom(
          this.api.post('/api/connections/{id}/refresh', { path: { id: connection.id } }),
        ),
      );
    });
  }

  protected async delete(connection: Connection): Promise<void> {
    const sure = await this.confirmer.confirm({
      title: `Delete ${connection.alias}?`,
      message: `Queries naming ${connection.alias} stop working, the overlay's items about it are kept but broken, and every user's pending changes to it go.`,
      confirm: 'Delete',
      destructive: true,
    });
    if (!sure) {
      return;
    }
    await this.act(async () => {
      await firstValueFrom(
        this.api.delete('/api/connections/{id}', {
          path: { id: connection.id },
          query: { version: this.stored()?.version ?? connection.version },
        }),
      );
      this.saved = true;
      this.notifier.say(`${connection.alias} is deleted.`);
      await this.router.navigate(['/admin/connections']);
    });
  }

  protected optionChoices(option: FieldDescriptor): { value: string; label: string }[] {
    const choices = option.choices ?? [];
    return choices.some((choice) => choice.value === '')
      ? choices
      : [{ value: '', label: 'Default' }, ...choices];
  }

  private async store(draft: ConnectionDraft): Promise<ValidationError.WithFieldTree[]> {
    this.clearProblems();
    this.testResult.set(null);
    const model = draft.model();
    const displayName = model.displayName.trim() || null;
    try {
      const stored = this.stored();
      if (!stored) {
        const made = await firstValueFrom(
          this.api.post('/api/connections', {
            body: {
              alias: model.alias.trim(),
              kind: draft.kind.id,
              displayName,
              connection: draft.input(),
            },
          }),
        );
        this.saved = true;
        this.notifier.say(`${made.alias} is made, and its schema is being read.`);
        await this.router.navigate(['/admin/connections', made.id], { replaceUrl: true });
      } else {
        const updated = await firstValueFrom(
          this.api.put('/api/connections/{id}', {
            path: { id: stored.id },
            body: { connection: draft.input(), version: stored.version, displayName },
          }),
        );
        this.generation++;
        this.live.set(null);
        this.connection.set(updated);
        this.notifier.say('Saved.');
      }
      return [];
    } catch (error) {
      return this.refusal(draft, problemOf(error));
    }
  }

  private async tryIt(draft: ConnectionDraft): Promise<ValidationError.WithFieldTree[]> {
    this.clearProblems();
    this.testing.set(true);
    try {
      this.testResult.set(
        await firstValueFrom(
          this.api.post('/api/connections/test', {
            body: {
              kind: draft.kind.id,
              connection: draft.input(),
              connectionId: draft.connection?.id ?? null,
            },
          }),
        ),
      );
      return [];
    } catch (error) {
      return this.refusal(draft, problemOf(error));
    } finally {
      this.testing.set(false);
    }
  }

  /** What the server refused: on the fields it was about, the rest above the form. */
  private refusal(draft: ConnectionDraft, problem: Problem): ValidationError.WithFieldTree[] {
    if (problem.code === ProblemCode.aliasTaken) {
      return this.onFields(draft, [
        { kind: 'server', message: problem.detail ?? problem.title, fieldTree: draft.form.alias },
      ]);
    }
    const { errors, others } = fieldErrors(problem, draft.errorFields());
    if (errors.length === 0 || others.length > 0) {
      this.problem.set(problem);
      this.unplaced.set(others);
    }
    return this.onFields(draft, errors);
  }

  private onFields(
    draft: ConnectionDraft,
    errors: ValidationError.WithFieldTree[],
  ): ValidationError.WithFieldTree[] {
    if (errors.length > 0) {
      afterNextRender(() => focusFirstInvalid(draft.fieldsInOrder()), { injector: this.injector });
    }
    return errors;
  }

  /** Does something to the connection, showing what goes wrong. */
  private async act(action: () => Promise<void>): Promise<void> {
    this.clearProblems();
    this.busy.set(true);
    try {
      await action();
    } catch (error) {
      this.problem.set(problemOf(error));
    } finally {
      this.busy.set(false);
    }
  }

  /** Looks at the connection's schema, unless the connection was saved or read since. */
  private async look(id: number): Promise<void> {
    const generation = this.generation;
    try {
      const found = await firstValueFrom(this.api.get('/api/connections/{id}', { path: { id } }));
      if (generation === this.generation) {
        this.failedLooks.set(0);
        this.live.set(found);
      }
    } catch {
      if (generation === this.generation) {
        this.failedLooks.update((failed) => failed + 1);
      }
    }
  }

  private clearProblems(): void {
    this.problem.set(null);
    this.unplaced.set([]);
  }

  protected stale(problem: Problem): boolean {
    return problem.code === ProblemCode.concurrencyConflict;
  }
}
