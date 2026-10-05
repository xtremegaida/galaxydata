import { DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import {
  FormField,
  FormRoot,
  form,
  maxLength,
  readonly,
  required,
  validate,
  type ReadonlyFieldTree,
} from '@angular/forms/signals';
import { MatAutocomplete, MatAutocompleteTrigger } from '@angular/material/autocomplete';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatOption } from '@angular/material/core';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { RouterLink } from '@angular/router';
import { Message } from '../../../core/ui/message';
import { EntityLookup, EntitySearch } from './catalog-lookups';
import { OverlayCheckPanel } from './overlay-check';
import { OverlayItemPage } from './overlay-item-page';
import {
  trimmed,
  type NavigationOverride,
  type NavigationOverrideInput,
  type OverlayItemKind,
} from './overlay-items';

interface OverrideModel {
  entity: string;
  navigation: string;
  renameTo: string;
  hidden: boolean;
}

/**
 * A navigation renamed or hidden, made or edited: the entity it is on, the navigation (by the name the convention
 * gives it, suggested from the entity's), its new name and whether it is hidden; tried as it is edited (the entity's
 * navigations as it makes them).
 */
@Component({
  selector: 'gd-navigation-page',
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
    MatInput,
    MatLabel,
    MatOption,
    MatProgressBar,
    MatSlideToggle,
    Message,
    OverlayCheckPanel,
    RouterLink,
  ],
  templateUrl: './navigation-page.html',
  styleUrls: ['../admin-page.scss', './overlay-page.scss'],
})
export class NavigationPage extends OverlayItemPage<
  NavigationOverride,
  OverrideModel,
  NavigationOverrideInput
> {
  /** For a new override, the entity and the navigation (`?entity=&navigation=`, as an entity's page links to it). */
  readonly entity = input<string>();
  readonly navigation = input<string>();

  protected readonly kind: OverlayItemKind = 'navigation';
  protected readonly bodyField = 'navigation';
  protected readonly deleteMessage =
    'The navigation is named, and shown, as the convention has it again.';

  protected readonly form = form(
    this.model,
    (path) => {
      readonly(path, () => this.saving());
      required(path.entity, { message: 'Name the entity the navigation is on' });
      maxLength(path.entity, 400, { message: 'A path has 400 characters at most' });
      required(path.navigation, { message: 'Name the navigation, as the convention names it' });
      maxLength(path.navigation, 200, { message: 'A name has 200 characters at most' });
      validate(path.renameTo, ({ value, valueOf }) =>
        !value().trim() && !valueOf(path.hidden)
          ? { kind: 'required', message: 'Rename the navigation, or hide it' }
          : null,
      );
      maxLength(path.renameTo, 200, { message: 'A name has 200 characters at most' });
    },
    {
      submission: {
        action: () => this.save(),
        onInvalid: () => this.focusFirstWrong(),
      },
    },
  );

  protected readonly entitySearch = new EntitySearch(() => this.model().entity, this.waits.lookup);
  protected readonly entityLookup = new EntityLookup(() => this.model().entity, this.waits.lookup);

  /**
   * The entity's navigations, to choose from (those holding what is typed), by the names the convention gives them:
   * the catalog's are as overrides make them, so the one this override renames is named as it is without it.
   */
  protected readonly navigationChoices = computed(() => {
    const typed = this.model().navigation.trim().toLowerCase();
    const stored = this.stored();
    const renamed =
      stored?.renameTo && stored.entity === this.model().entity.trim() ? stored : null;
    return (this.entityLookup.entity()?.navigations ?? [])
      .map((navigation) =>
        renamed && navigation.name === renamed.renameTo
          ? { ...navigation, name: renamed.navigation }
          : navigation,
      )
      .filter((navigation) => !typed || navigation.name.toLowerCase().includes(typed));
  });

  /** The entity's navigations as the override would make them. */
  protected readonly made = computed(() => this.shownCheck()?.entity?.navigations ?? null);

  protected modelOf(override: NavigationOverride | undefined): OverrideModel {
    return {
      entity: override?.entity ?? this.given(this.entity()) ?? '',
      navigation: override?.navigation ?? this.given(this.navigation()) ?? '',
      renameTo: override?.renameTo ?? '',
      hidden: override?.hidden ?? false,
    };
  }

  protected inputOf(model: OverrideModel): NavigationOverrideInput {
    return {
      entity: model.entity.trim(),
      navigation: model.navigation.trim(),
      renameTo: trimmed(model.renameTo),
      hidden: model.hidden,
    };
  }

  protected needs(model: OverrideModel): string | null {
    if (!model.entity.trim() || !model.navigation.trim()) {
      return 'Name the entity and its navigation, and it is tried as you go.';
    }
    return !model.renameTo.trim() && !model.hidden
      ? 'Rename the navigation or hide it, and it is tried as you go.'
      : null;
  }

  protected errorFields(): Readonly<Record<string, ReadonlyFieldTree<unknown>>> {
    return {
      entity: this.form.entity,
      navigation: this.form.navigation,
      renameTo: this.form.renameTo,
      hidden: this.form.hidden,
    };
  }

  protected fieldsInOrder(): ReadonlyFieldTree<unknown>[] {
    return [this.form.entity, this.form.navigation, this.form.renameTo];
  }

  protected takenField(): ReadonlyFieldTree<unknown> | null {
    return this.form.navigation;
  }

  protected read(id: number) {
    return this.api.get('/api/overlay/navigations/{id}', { path: { id } });
  }

  protected create(input: NavigationOverrideInput) {
    return this.api.post('/api/overlay/navigations', { body: input });
  }

  protected update(id: number, input: NavigationOverrideInput, version: number) {
    return this.api.put('/api/overlay/navigations/{id}', {
      path: { id },
      body: { navigation: input, version },
    });
  }

  protected remove(id: number, version: number) {
    return this.api.delete('/api/overlay/navigations/{id}', { path: { id }, query: { version } });
  }

  protected tryIt(input: NavigationOverrideInput, id: number | null) {
    return this.api.post('/api/overlay/navigations/validate', {
      query: { id: id ?? undefined },
      body: input,
    });
  }
}
