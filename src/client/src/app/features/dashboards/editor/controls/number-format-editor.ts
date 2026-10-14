import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  input,
  model,
} from '@angular/core';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { formatNumber } from '../../charts/chart-values';
import type { NumberFormat } from '../../model/definition';

const blank: NumberFormat = { decimals: null, prefix: null, suffix: null, compact: false };

/** A format that changes nothing is none, as the server writes a measure without one. */
export function formatOrNone(format: NumberFormat): NumberFormat | null {
  return format.decimals === null && !format.prefix && !format.suffix && !format.compact
    ? null
    : format;
}

/**
 * How a measure's numbers show: their decimals (none: as they are, 0 to 10), text before and after them (20
 * characters each, spaces kept), and large ones shortened (1.2K); with an example. Nothing set is no format.
 */
@Component({
  selector: 'gd-number-format-editor',
  imports: [MatFormField, MatInput, MatLabel, MatSlideToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset class="format">
      <legend>{{ label() }}</legend>
      <div class="row">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Decimals</mat-label>
          <input
            matInput
            type="number"
            min="0"
            max="10"
            placeholder="As they are"
            [value]="format()?.decimals ?? ''"
            (change)="decimals($event)"
          />
        </mat-form-field>
        <mat-slide-toggle
          [checked]="!!format()?.compact"
          (change)="set({ compact: $event.checked })"
        >
          Shortened (1.2K)
        </mat-slide-toggle>
      </div>
      <div class="row">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Before</mat-label>
          <input
            matInput
            maxlength="20"
            autocomplete="off"
            placeholder="R "
            [value]="format()?.prefix ?? ''"
            (input)="set({ prefix: text($event) })"
          />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>After</mat-label>
          <input
            matInput
            maxlength="20"
            autocomplete="off"
            placeholder=" ms"
            [value]="format()?.suffix ?? ''"
            (input)="set({ suffix: text($event) })"
          />
        </mat-form-field>
      </div>
      <p class="example">1234567.891 shows as {{ example() }}</p>
    </fieldset>
  `,
  styles: `
    .format {
      display: grid;
      gap: 8px;
      margin: 0;
      padding: 8px 12px 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: var(--mat-sys-corner-small);
    }
    legend {
      font: var(--mat-sys-label-large);
      padding: 0 4px;
    }
    .row {
      display: grid;
      grid-template-columns: 1fr 1fr;
      align-items: center;
      gap: 8px;
    }
    .example {
      margin: 0;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class NumberFormatEditor {
  private readonly locale = inject(LOCALE_ID);

  readonly label = input('Numbers');
  readonly format = model<NumberFormat | null>(null);

  protected readonly example = computed(() =>
    formatNumber('1234567.891', this.format(), this.locale),
  );

  protected set(change: Partial<NumberFormat>): void {
    this.format.set(formatOrNone({ ...(this.format() ?? blank), ...change }));
  }

  protected decimals(event: Event): void {
    const text = (event.target as HTMLInputElement).value.trim();
    if (text === '') {
      this.set({ decimals: null });
      return;
    }
    const decimals = Math.round(Number(text));
    if (decimals >= 0 && decimals <= 10) {
      this.set({ decimals });
    }
  }

  protected text(event: Event): string | null {
    return (event.target as HTMLInputElement).value || null;
  }
}
