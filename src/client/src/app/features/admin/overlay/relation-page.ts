import { DatePipe } from '@angular/common';
import { Component, computed } from '@angular/core';
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
import { MatOption } from '@angular/material/core';
import { MatAnchor, MatButton, MatIconButton } from '@angular/material/button';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { Message } from '../../../core/ui/message';
import { EntityLookup, EntitySearch, suggestions } from './catalog-lookups';
import { OverlayCheckPanel } from './overlay-check';
import { OverlayItemPage } from './overlay-item-page';
import { trimmed, type OverlayItemKind, type Relation, type RelationInput } from './overlay-items';

/** A column of the entity a relation is from, and the column of the one it leads to that it meets. */
interface ColumnPair {
  from: string;
  to: string;
}

interface RelationModel {
  from: string;
  to: string;
  pairs: ColumnPair[];
  name: string;
  inverseName: string;
  description: string;
}

/**
 * A relation the databases don't declare, made or edited: the entity whose rows refer to another's, the entity they
 * lead to, the columns that meet (suggested from each entity's, and the key it leads to), and its navigations'
 * names; tried as it is edited (the navigations it makes, what is wrong).
 */
@Component({
  selector: 'gd-relation-page',
  imports: [
    DatePipe,
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
    RouterLink,
  ],
  templateUrl: './relation-page.html',
  styleUrls: ['../admin-page.scss', './overlay-page.scss'],
})
export class RelationPage extends OverlayItemPage<Relation, RelationModel, RelationInput> {
  protected readonly kind: OverlayItemKind = 'relation';
  protected readonly bodyField = 'relation';
  protected readonly deleteMessage =
    'Its navigations go from the catalog, and queries that use them stop working.';

  protected readonly form = form(
    this.model,
    (path) => {
      readonly(path, () => this.saving());
      required(path.from, { message: 'Name the entity whose rows refer to the others' });
      maxLength(path.from, 400, { message: 'A path has 400 characters at most' });
      required(path.to, { message: 'Name the entity they refer to' });
      maxLength(path.to, 400, { message: 'A path has 400 characters at most' });
      applyEach(path.pairs, (pair) => {
        // Spaces alone are no name either.
        required(pair.from, { message: 'Name the column' });
        validate(pair.from, ({ value }) =>
          value() && !value().trim() ? { kind: 'required', message: 'Name the column' } : null,
        );
        maxLength(pair.from, 200, { message: "A column's name has 200 characters at most" });
        required(pair.to, { message: 'Name the column it meets' });
        validate(pair.to, ({ value }) =>
          value() && !value().trim()
            ? { kind: 'required', message: 'Name the column it meets' }
            : null,
        );
        maxLength(pair.to, 200, { message: "A column's name has 200 characters at most" });
      });
      maxLength(path.name, 200, { message: 'A name has 200 characters at most' });
      maxLength(path.inverseName, 200, { message: 'A name has 200 characters at most' });
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

  protected readonly fromSearch = new EntitySearch(() => this.model().from, this.waits.lookup);
  protected readonly toSearch = new EntitySearch(() => this.model().to, this.waits.lookup);
  protected readonly fromEntity = new EntityLookup(() => this.model().from, this.waits.lookup);
  protected readonly toEntity = new EntityLookup(() => this.model().to, this.waits.lookup);
  protected readonly suggest = suggestions;

  /** The key of the entity it leads to, when the columns it meets aren't it yet. */
  protected readonly keyToMeet = computed(() => {
    const key = this.toEntity.key();
    const to = this.model().pairs.map((pair) => pair.to.trim());
    return key && (key.length !== to.length || key.some((column, at) => column !== to[at]))
      ? key
      : null;
  });

  /** The navigations the relation makes, as the catalog would name them. */
  protected readonly navigations = computed(() => this.shownCheck()?.navigations ?? null);

  /** Adds a pair of columns, the keyboard in it. */
  protected addPair(): void {
    const at = this.model().pairs.length;
    this.model.update((model) => ({ ...model, pairs: [...model.pairs, { from: '', to: '' }] }));
    this.keepKeyboard(`[data-at="from-${at}"]`);
  }

  /** Removes a pair of columns, the keyboard going to the pair in its place (or the one before). */
  protected removePair(index: number): void {
    this.model.update((model) => ({
      ...model,
      pairs: model.pairs.filter((_, at) => at !== index),
    }));
    this.keepKeyboard(`[data-at="from-${Math.min(index, this.model().pairs.length - 1)}"]`);
  }

  /**
   * Meets the key of the entity it leads to: a pair for each of its columns, the columns it is from kept. The keyboard
   * goes to the first column to name.
   */
  protected meetKey(key: readonly string[]): void {
    this.model.update((model) => ({
      ...model,
      pairs: key.map((to, at) => ({ from: model.pairs[at]?.from ?? '', to })),
    }));
    const blank = this.model().pairs.findIndex((pair) => !pair.from.trim());
    this.keepKeyboard(`[data-at="from-${Math.max(blank, 0)}"]`);
  }

  protected modelOf(relation: Relation | undefined): RelationModel {
    const count = Math.max(relation?.fromColumns.length ?? 0, relation?.toColumns.length ?? 0, 1);
    return {
      from: relation?.from ?? '',
      to: relation?.to ?? '',
      pairs: Array.from({ length: count }, (_, at) => ({
        from: relation?.fromColumns[at] ?? '',
        to: relation?.toColumns[at] ?? '',
      })),
      name: relation?.name ?? '',
      inverseName: relation?.inverseName ?? '',
      description: relation?.description ?? '',
    };
  }

  protected inputOf(model: RelationModel): RelationInput {
    return {
      from: model.from.trim(),
      fromColumns: model.pairs.map((pair) => pair.from.trim()),
      to: model.to.trim(),
      toColumns: model.pairs.map((pair) => pair.to.trim()),
      name: trimmed(model.name),
      inverseName: trimmed(model.inverseName),
      description: trimmed(model.description),
    };
  }

  protected needs(model: RelationModel): string | null {
    if (!model.from.trim() || !model.to.trim()) {
      return 'Name the entities it relates, and it is tried as you go.';
    }
    if (model.pairs.some((pair) => !pair.from.trim() || !pair.to.trim())) {
      return 'Name each column, and the column it meets, and it is tried as you go.';
    }
    return null;
  }

  protected errorFields(): Readonly<Record<string, ReadonlyFieldTree<unknown>>> {
    const fields: Record<string, ReadonlyFieldTree<unknown>> = {
      from: this.form.from,
      to: this.form.to,
      name: this.form.name,
      inverseName: this.form.inverseName,
      description: this.form.description,
    };
    this.model().pairs.forEach((_, at) => {
      fields[`fromColumns[${at}]`] = this.form.pairs[at].from;
      fields[`toColumns[${at}]`] = this.form.pairs[at].to;
    });
    // What is wrong with the columns as a whole (as many on each side) goes on the first pair.
    fields['fromColumns'] = fields['fromColumns[0]'];
    fields['toColumns'] = fields['toColumns[0]'];
    return fields;
  }

  protected fieldsInOrder(): ReadonlyFieldTree<unknown>[] {
    return [
      this.form.from,
      this.form.to,
      ...this.model().pairs.flatMap((_, at) => [this.form.pairs[at].from, this.form.pairs[at].to]),
      this.form.name,
      this.form.inverseName,
      this.form.description,
    ];
  }

  protected takenField(): ReadonlyFieldTree<unknown> | null {
    return null;
  }

  protected read(id: number) {
    return this.api.get('/api/overlay/relations/{id}', { path: { id } });
  }

  protected create(input: RelationInput) {
    return this.api.post('/api/overlay/relations', { body: input });
  }

  protected update(id: number, input: RelationInput, version: number) {
    return this.api.put('/api/overlay/relations/{id}', {
      path: { id },
      body: { relation: input, version },
    });
  }

  protected remove(id: number, version: number) {
    return this.api.delete('/api/overlay/relations/{id}', { path: { id }, query: { version } });
  }

  protected tryIt(input: RelationInput, id: number | null) {
    return this.api.post('/api/overlay/relations/validate', {
      query: { id: id ?? undefined },
      body: input,
    });
  }
}
