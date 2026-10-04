import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  input,
  signal,
} from '@angular/core';
import { FormField, type FieldTree } from '@angular/forms/signals';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatError, MatFormField, MatHint, MatLabel, MatSuffix } from '@angular/material/form-field';
import { MatIcon } from '@angular/material/icon';
import { MatInput } from '@angular/material/input';
import type { SecretModel } from './connection-draft';

/**
 * A secret's control: secrets are never sent back, so one stored is kept, changed (set) or cleared; one not stored
 * is set by typing it.
 */
@Component({
  selector: 'gd-secret-field',
  imports: [
    FormField,
    MatButton,
    MatError,
    MatFormField,
    MatHint,
    MatIcon,
    MatIconButton,
    MatInput,
    MatLabel,
    MatSuffix,
  ],
  template: `
    @let state = secret()().value();
    @if (state.stored && state.action === 'keep') {
      <div class="stored" role="group" [attr.aria-label]="label()">
        <mat-icon>lock</mat-icon>
        <span class="text">{{ label() }}: stored</span>
        <button matButton type="button" (click)="act('set')">Change</button>
        <button matButton type="button" (click)="act('clear')">Clear</button>
      </div>
    } @else if (state.stored && state.action === 'clear') {
      <div class="stored" role="group" [attr.aria-label]="label()">
        <mat-icon>lock_open</mat-icon>
        <span class="text">{{ label() }}: cleared when saved</span>
        <button matButton type="button" (click)="act('keep')">Keep it</button>
      </div>
    } @else {
      <mat-form-field class="field">
        <mat-label>{{ label() }}</mat-label>
        <input
          matInput
          [formField]="secret().text"
          [type]="shown() ? 'text' : 'password'"
          autocomplete="off"
          spellcheck="false"
        />
        <button
          matIconButton
          matSuffix
          type="button"
          [attr.aria-label]="'Show the ' + label().toLowerCase()"
          [attr.aria-pressed]="shown()"
          (click)="shown.set(!shown())"
        >
          <mat-icon>{{ shown() ? 'visibility_off' : 'visibility' }}</mat-icon>
        </button>
        <mat-hint>
          @if (state.stored) {
            Replaces the one stored.
            <button class="link" type="button" (click)="act('keep')">Keep it</button>
          } @else {
            {{ help() }}
          }
        </mat-hint>
        <mat-error>{{ secret().text().errors()[0]?.message }}</mat-error>
      </mat-form-field>
    }
  `,
  styles: `
    .field {
      width: 100%;
    }

    .stored {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      min-height: 56px;
      margin-bottom: 20px;
      padding: 0 8px 0 16px;
      border-radius: var(--mat-sys-corner-extra-small);
      background: var(--mat-sys-surface-container-highest);
    }

    .text {
      flex: 1;
    }

    .link {
      padding: 0;
      border: 0;
      background: none;
      color: var(--mat-sys-primary);
      font: inherit;
      text-decoration: underline;
      cursor: pointer;
    }
  `,
})
export class SecretField {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly secret = input.required<FieldTree<SecretModel>>();
  readonly label = input.required<string>();
  readonly help = input<string | null | undefined>('');

  protected readonly shown = signal(false);

  /**
   * Keeps, changes or clears the secret (one not stored is set, by typing it), and moves focus to what takes the
   * place of the button: the input, or the other view's first button.
   */
  protected act(action: SecretModel['action']): void {
    const stored = this.secret()().value().stored;
    const next = stored ? action : 'set';
    this.secret()().value.update((secret) => ({ ...secret, action: next, text: '' }));
    afterNextRender(
      () =>
        this.host.nativeElement
          .querySelector<HTMLElement>(next === 'set' ? 'input' : '.stored button')
          ?.focus(),
      { injector: this.injector },
    );
  }
}
