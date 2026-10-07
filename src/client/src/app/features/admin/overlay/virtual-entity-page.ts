import { DatePipe } from '@angular/common';
import { Component, computed, signal, viewChild } from '@angular/core';
import {
  FormField,
  FormRoot,
  applyEach,
  form,
  maxLength,
  readonly,
  required,
  validate,
  type ReadonlyFieldTree,
} from '@angular/forms/signals';
import { MatAutocomplete, MatAutocompleteTrigger } from '@angular/material/autocomplete';
import { MatAnchor, MatButton, MatIconButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { CodeEditor, type EditorMarker } from '../../../core/editor/code-editor';
import { gdqLanguageId } from '../../../core/editor/gdq-language';
import { queryUrlTree } from '../../../core/query/query-url';
import { Message } from '../../../core/ui/message';
import { EntityStructure } from '../../browse/entity-structure';
import { positionOf } from '../../query/query-messages';
import { QueryResults, type FollowedLink, type QueryRun } from '../../query/query-results';
import { suggestions } from '../../../core/catalog/catalog-lookups';
import { OverlayCheckPanel } from './overlay-check';
import { OverlayItemPage } from './overlay-item-page';
import {
  namesOf,
  severityIcons,
  severityWords,
  trimmed,
  type OverlayItemKind,
  type VirtualEntity,
  type VirtualEntityInput,
} from './overlay-items';

interface VirtualEntityModel {
  name: string;
  query: string;
  key: string[];
  description: string;
}

let runs = 0;

/**
 * A virtual entity, made or edited: its name (with a namespace), its query (in the query language's editor, what is
 * wrong marked in it), the key declared for it (suggested from its query's columns), its description; tried as it
 * is edited (the entity it makes, its query's diagnostics), and its rows shown on asking.
 */
@Component({
  selector: 'gd-virtual-entity-page',
  imports: [
    CodeEditor,
    DatePipe,
    EntityStructure,
    FormField,
    FormRoot,
    MatAnchor,
    MatAutocomplete,
    MatAutocompleteTrigger,
    MatButton,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatOption,
    MatProgressBar,
    Message,
    OverlayCheckPanel,
    QueryResults,
    RouterLink,
  ],
  templateUrl: './virtual-entity-page.html',
  styleUrls: ['../admin-page.scss', './overlay-page.scss'],
})
export class VirtualEntityPage extends OverlayItemPage<
  VirtualEntity,
  VirtualEntityModel,
  VirtualEntityInput
> {
  protected readonly kind: OverlayItemKind = 'virtualEntity';
  protected readonly bodyField = 'virtualEntity';
  protected readonly deleteMessage =
    'Queries that use it stop working, and the overlay’s items about it are kept, but broken.';
  protected readonly languageId = gdqLanguageId;
  protected readonly icons = severityIcons;
  protected readonly words = severityWords;
  protected readonly suggest = suggestions;

  private readonly editor = viewChild.required(CodeEditor);

  protected readonly form = form(
    this.model,
    (path) => {
      readonly(path, () => this.saving());
      required(path.name, { message: 'Name it, with a namespace: reports.big_orders' });
      maxLength(path.name, 400, { message: 'A name has 400 characters at most' });
      required(path.query, { message: 'Write the query whose rows it has' });
      maxLength(path.query, 100_000, { message: 'A query has 100,000 characters at most' });
      applyEach(path.key, (column) => {
        // Spaces alone are no name either.
        required(column, { message: 'Name the column, or remove it' });
        validate(column, ({ value }) =>
          value() && !value().trim()
            ? { kind: 'required', message: 'Name the column, or remove it' }
            : null,
        );
        maxLength(column, 200, { message: "A column's name has 200 characters at most" });
      });
      maxLength(path.description, 1000, {
        message: 'A description has 1000 characters at most',
      });
    },
    {
      submission: {
        action: () => this.save(),
        onInvalid: () => this.focusFirstWrong(),
      },
    },
  );

  /** The entity the query makes, as tried. */
  protected readonly made = computed(() => this.shownCheck()?.entity ?? null);
  /** The columns of the entity it makes, to suggest for its key. */
  protected readonly columns = computed(
    () => this.made()?.columns.map((column) => column.name) ?? [],
  );

  /** What is wrong with the query as it is now (tried with it, whatever its name), each where it is. */
  protected readonly diagnostics = computed(() => {
    const query = this.model().query;
    const check = this.shownCheck();
    return check && this.triedInput()?.query === query
      ? (check.diagnostics ?? []).map((diagnostic) => ({
          diagnostic,
          ...positionOf(query, diagnostic.start),
        }))
      : [];
  });
  protected readonly markers = computed<EditorMarker[]>(() =>
    this.diagnostics().map(({ diagnostic }) => ({
      start: diagnostic.start,
      length: diagnostic.end - diagnostic.start,
      message: `${diagnostic.message} (${diagnostic.code})`,
      severity: diagnostic.severity,
    })),
  );

  /** The query whose rows are shown, on asking. */
  protected readonly run = signal<QueryRun | null>(null);

  protected setQuery(query: string): void {
    this.model.update((model) => ({ ...model, query }));
  }

  /** Shows the rows of the query as it is now. */
  protected showRows(): void {
    const query = this.model().query;
    if (query.trim()) {
      this.run.set({ text: query, parameters: [], serial: ++runs });
    }
  }

  /** Shows them again, for the query as it is now: the button goes, and the keyboard goes to their heading. */
  protected showAgain(): void {
    this.showRows();
    this.keepKeyboard('#gd-virtual-entity-rows');
  }

  /** Another virtual entity shown: the rows shown were the one before's. */
  protected override shown(): void {
    this.run.set(null);
  }

  /** Puts the editor's keyboard where a diagnostic is. */
  protected goTo(offset: number): void {
    this.editor().reveal(offset);
  }

  /** Follows a link of the rows shown: to browse rows, or to the query of a group's rows. */
  protected async follow(link: FollowedLink): Promise<void> {
    await this.router.navigateByUrl(
      link.kind === 'browse'
        ? link.url
        : queryUrlTree({ id: null, text: link.text, values: link.values, run: true }),
    );
  }

  /** Adds a key column, the keyboard in it. */
  protected addKeyColumn(): void {
    const at = this.model().key.length;
    this.model.update((model) => ({ ...model, key: [...model.key, ''] }));
    this.keepKeyboard(`[data-at="key-${at}"]`);
  }

  /** Removes a key column, the keyboard going to the one in its place (or before; or to adding one). */
  protected removeKeyColumn(index: number): void {
    this.model.update((model) => ({ ...model, key: model.key.filter((_, at) => at !== index) }));
    const left = this.model().key.length;
    this.keepKeyboard(
      left > 0 ? `[data-at="key-${Math.min(index, left - 1)}"]` : '[data-at="add-key"]',
    );
  }

  protected modelOf(entity: VirtualEntity | undefined): VirtualEntityModel {
    return {
      name: entity?.name ?? '',
      query: entity?.query ?? '',
      key: [...(entity?.key ?? [])],
      description: entity?.description ?? '',
    };
  }

  protected inputOf(model: VirtualEntityModel): VirtualEntityInput {
    return {
      name: model.name.trim(),
      query: model.query,
      key: namesOf(model.key),
      description: trimmed(model.description),
    };
  }

  protected needs(model: VirtualEntityModel): string | null {
    return !model.name.trim() || !model.query.trim()
      ? 'Name it and write its query, and it is tried as you go.'
      : null;
  }

  protected errorFields(): Readonly<Record<string, ReadonlyFieldTree<unknown>>> {
    const fields: Record<string, ReadonlyFieldTree<unknown>> = {
      name: this.form.name,
      query: this.form.query,
      description: this.form.description,
    };
    this.model().key.forEach((_, at) => (fields[`key[${at}]`] = this.form.key[at]));
    return fields;
  }

  protected fieldsInOrder(): ReadonlyFieldTree<unknown>[] {
    return [
      this.form.name,
      ...this.model().key.map((_, at) => this.form.key[at]),
      this.form.description,
    ];
  }

  /** The query's editor isn't a field of the form: it takes the keyboard when the query is the first wrong. */
  protected override focusFirstWrong(): void {
    if (!this.form.name().invalid() && this.form.query().invalid()) {
      this.editor().focus();
    } else {
      super.focusFirstWrong();
    }
  }

  protected takenField(): ReadonlyFieldTree<unknown> | null {
    return this.form.name;
  }

  protected read(id: number) {
    return this.api.get('/api/overlay/virtual-entities/{id}', { path: { id } });
  }

  protected create(input: VirtualEntityInput) {
    return this.api.post('/api/overlay/virtual-entities', { body: input });
  }

  protected update(id: number, input: VirtualEntityInput, version: number) {
    return this.api.put('/api/overlay/virtual-entities/{id}', {
      path: { id },
      body: { virtualEntity: input, version },
    });
  }

  protected remove(id: number, version: number) {
    return this.api.delete('/api/overlay/virtual-entities/{id}', {
      path: { id },
      query: { version },
    });
  }

  protected tryIt(input: VirtualEntityInput, id: number | null) {
    return this.api.post('/api/overlay/virtual-entities/validate', {
      query: { id: id ?? undefined },
      body: input,
    });
  }
}
