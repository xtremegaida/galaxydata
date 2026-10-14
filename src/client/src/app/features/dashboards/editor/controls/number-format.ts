import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  model,
} from '@angular/core';
import { MatOption } from '@angular/material/core';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { MatSelect } from '@angular/material/select';
import { MatSlideToggle } from '@angular/material/slide-toggle';
import { formatNumber } from '../../charts/chart-values';
import type { NumberFormat } from '../../model/definition';

type Style = NonNullable<NumberFormat['style']>;
type Unit = NonNullable<NumberFormat['unit']>;

const units: readonly { unit: Unit; label: string; size: number }[] = [
  { unit: 'milliseconds', label: 'Milliseconds', size: 0.001 },
  { unit: 'seconds', label: 'Seconds', size: 1 },
  { unit: 'minutes', label: 'Minutes', size: 60 },
  { unit: 'hours', label: 'Hours', size: 3600 },
  { unit: 'days', label: 'Days', size: 86_400 },
];

/**
 * A format as it is kept: null when it changes nothing (numbers as they are), so a decimal's scale still shows; a
 * style only when it isn't numbers, and a unit only for durations, as the server writes them.
 */
export function keptFormat(format: NumberFormat): NumberFormat | null {
  const { style, unit, ...given } = format;
  const rest = { ...given, prefix: given.prefix || null, suffix: given.suffix || null };
  switch (style) {
    case 'duration':
      return { ...rest, compact: false, style, unit: unit ?? 'seconds' };
    case 'percent':
      return { ...rest, style };
    default:
      return !rest.prefix && !rest.suffix && !rest.compact && rest.decimals === null ? null : rest;
  }
}

/**
 * How a measure's (or a column's) numbers show: as numbers, as percentages (a share of 1, times 100) or as
 * durations (of a unit: 2m 15s); their decimals, text before and after, and large numbers shortened (not
 * durations). An example says how one would show.
 */
@Component({
  selector: 'gd-number-format',
  imports: [MatFormField, MatHint, MatInput, MatLabel, MatOption, MatSelect, MatSlideToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="format" role="group" aria-label="How its numbers show">
      <div class="row">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Show as</mat-label>
          <mat-select [value]="style()" (valueChange)="styled($event)">
            <mat-option value="number">Numbers</mat-option>
            <mat-option value="percent">Percentages</mat-option>
            <mat-option value="duration">Durations</mat-option>
          </mat-select>
          <mat-hint>{{ example() }}</mat-hint>
        </mat-form-field>
        @if (style() === 'duration') {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Of</mat-label>
            <mat-select [value]="current().unit ?? 'seconds'" (valueChange)="set({ unit: $event })">
              @for (choice of units; track choice.unit) {
                <mat-option [value]="choice.unit">{{ choice.label }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        } @else {
          <mat-form-field subscriptSizing="dynamic">
            <mat-label>Decimals</mat-label>
            <input
              matInput
              type="number"
              min="0"
              max="10"
              placeholder="As they are"
              [value]="current().decimals ?? ''"
              (input)="decimals($event)"
            />
          </mat-form-field>
        }
      </div>
      <div class="row">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Before</mat-label>
          <input
            matInput
            maxlength="20"
            [value]="current().prefix ?? ''"
            (input)="set({ prefix: text($event) })"
            autocomplete="off"
          />
        </mat-form-field>
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>After</mat-label>
          <input
            matInput
            maxlength="20"
            [value]="current().suffix ?? ''"
            (input)="set({ suffix: text($event) })"
            autocomplete="off"
          />
        </mat-form-field>
      </div>
      @if (style() !== 'duration') {
        <mat-slide-toggle [checked]="current().compact" (change)="set({ compact: $event.checked })">
          Shorten large numbers (1.2K)
        </mat-slide-toggle>
      }
    </div>
  `,
  styles: `
    .format {
      display: grid;
      gap: 8px;
    }
    .row {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 8px;
    }
  `,
})
export class NumberFormatEditor {
  private readonly locale = inject(LOCALE_ID);
  protected readonly units = units;

  readonly format = model.required<NumberFormat | null>();

  protected readonly current = computed<NumberFormat>(
    () => this.format() ?? { decimals: null, prefix: null, suffix: null, compact: false },
  );
  protected readonly style = computed<Style>(() => this.current().style ?? 'number');

  /** How a number of its kind shows: 1,234.5678, a share of 0.2567, two minutes and fifteen seconds. */
  protected readonly example = computed(() => {
    const format = this.current();
    const value =
      this.style() === 'percent'
        ? 0.2567
        : this.style() === 'duration'
          ? 135.4 / (units.find((u) => u.unit === (format.unit ?? 'seconds'))?.size ?? 1)
          : 1234.5678;
    return `As ${formatNumber(value, format, this.locale)}`;
  });

  /** Another style: a duration's unit (seconds, unless it had one), and its decimals as they are, as it hides them. */
  protected styled(style: Style): void {
    const duration = style === 'duration';
    this.set({
      style,
      unit: duration ? (this.current().unit ?? 'seconds') : null,
      decimals: duration ? null : this.current().decimals,
    });
  }

  protected decimals(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    const decimals = Math.round(Number(text));
    if (text === '') {
      this.set({ decimals: null });
    } else if (decimals >= 0 && decimals <= 10) {
      this.set({ decimals });
    }
  }

  protected text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected set(change: Partial<NumberFormat>): void {
    this.format.set(keptFormat({ ...this.current(), ...change }));
  }
}
