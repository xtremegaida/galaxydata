import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  model,
} from '@angular/core';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';

const hex = /^#[0-9a-f]{6}$/i;

/** A colour: chosen with the browser's picker, or written as #rrggbb (taken once it is one). */
@Component({
  selector: 'gd-color-field',
  imports: [MatFormField, MatHint, MatInput, MatLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <input
      type="color"
      class="picker"
      [value]="value()"
      [disabled]="disabled()"
      [attr.aria-label]="label() + ' (picker)'"
      (input)="picked($event)"
    />
    <mat-form-field subscriptSizing="dynamic" class="hex">
      <mat-label>{{ label() }}</mat-label>
      <input
        matInput
        maxlength="7"
        autocomplete="off"
        spellcheck="false"
        [value]="text()"
        [disabled]="disabled()"
        (input)="typed($event)"
        (blur)="text.set(value())"
      />
      @if (problem(); as problem) {
        <mat-hint class="problem" role="alert">{{ problem }}</mat-hint>
      }
    </mat-form-field>
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: 8px;
    }
    .picker {
      width: 40px;
      height: 40px;
      padding: 0;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 8px;
      background: none;
      cursor: pointer;
    }
    .hex {
      width: 9rem;
    }
    .problem {
      color: var(--mat-sys-error);
    }
  `,
})
export class ColorField {
  readonly label = input('Colour');
  readonly value = model('#000000');
  readonly disabled = input(false);
  /** What the server said is wrong with it. */
  readonly refused = input<string | null>(null);

  /** What is written, the colour's own till something else is. */
  protected readonly text = linkedSignal(() => this.value());

  protected readonly problem = computed(() =>
    hex.test(this.text()) ? this.refused() : 'Write it as #rrggbb',
  );

  protected typed(event: Event): void {
    const text = (event.target as HTMLInputElement).value.trim();
    this.text.set(text);
    if (hex.test(text)) {
      this.value.set(text.toLowerCase());
    }
  }

  protected picked(event: Event): void {
    this.value.set((event.target as HTMLInputElement).value.toLowerCase());
  }
}
