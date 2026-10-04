import { NgTemplateOutlet } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  untracked,
} from '@angular/core';
import { FormField } from '@angular/forms/signals';
import { MatButton, MatIconButton } from '@angular/material/button';
import {
  MatExpansionPanel,
  MatExpansionPanelHeader,
  MatExpansionPanelTitle,
} from '@angular/material/expansion';
import { MatError, MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import { MatOption, MatSelect } from '@angular/material/select';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import {
  isSecretKeyword,
  type ConnectionDraft,
  type FieldDescriptor,
  type GroupDescriptor,
} from './connection-draft';
import { SecretField } from './secret-field';

/**
 * A connection's settings, field by field, as its kind describes them: in groups (those the kind collapses, such
 * as the advanced settings, folded until opened or holding a value), each field shown as its type is, and only
 * while what it depends on says so.
 */
@Component({
  selector: 'gd-connection-fields',
  imports: [
    FormField,
    MatButton,
    MatError,
    MatExpansionPanel,
    MatExpansionPanelHeader,
    MatExpansionPanelTitle,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatOption,
    MatSelect,
    MatSlideToggle,
    NgTemplateOutlet,
    SecretField,
  ],
  templateUrl: './connection-fields.html',
  styleUrl: './connection-fields.scss',
})
export class ConnectionFields {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly draft = input.required<ConnectionDraft>();

  protected readonly isSecret = isSecretKeyword;

  /**
   * The collapsed groups open at first: those holding a value when the draft is shown (or comes back to the form).
   * Worked out then, not as values change, so a group doesn't fold while its values are typed or removed.
   */
  /** The draft's mode, which changes only when it does (not as the draft's values do). */
  private readonly mode = computed(() => this.draft().model().mode);

  protected readonly openAtFirst = computed(() => {
    const draft = this.draft();
    this.mode();
    return untracked(
      () =>
        new Set(
          draft.kind.groups.filter((group) => this.holdsValues(group)).map((group) => group.key),
        ),
    );
  });

  /** An id for a field's help, for the control it describes. */
  protected helpId(field: FieldDescriptor): string {
    return `gd-help-${this.draft().kind.id}-${this.draft().kind.fields.indexOf(field)}`;
  }

  /** Removes an other setting, and moves focus to adding one (the button that was there is gone). */
  protected removeOther(index: number): void {
    this.draft().removeOther(index);
    afterNextRender(
      () => this.host.nativeElement.querySelector<HTMLElement>('.add-other')?.focus(),
      {
        injector: this.injector,
      },
    );
  }

  /** Whether a group holds a value. */
  private holdsValues(group: GroupDescriptor): boolean {
    const draft = this.draft();
    const model = draft.model();
    return draft
      .fieldsIn(group)
      .some((field) =>
        field.type === 'keyValues'
          ? model.others.length > 0 || draft.otherSecrets().length > 0
          : field.type === 'password'
            ? model.secrets[field.key]?.stored
            : (model.settings[field.key] ?? '') !== '',
      );
  }

  /** What a field shows when empty: its placeholder, or the default the provider takes. */
  protected placeholder(field: FieldDescriptor): string {
    return field.placeholder ?? (field.default ? `${field.default} if empty` : '');
  }

  /** The choices of a select, with the default first when it has none of its own for "not set". */
  protected choices(field: FieldDescriptor): { value: string; label: string }[] {
    const choices = field.choices ?? [];
    if (choices.some((choice) => choice.value === '')) {
      return choices;
    }
    const fallback = choices.find((choice) => choice.value === field.default);
    return [{ value: '', label: fallback ? `Default (${fallback.label})` : 'Default' }, ...choices];
  }

  protected pathHint(field: FieldDescriptor): string {
    const kind = field.type === 'folderPath' ? 'folder' : 'file';
    return field.help ?? `The ${kind}'s full path on the server, in a folder connections may use`;
  }
}
