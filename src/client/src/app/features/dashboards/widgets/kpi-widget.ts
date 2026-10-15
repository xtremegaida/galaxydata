import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { formatNumber } from '../charts/chart-values';
import type { KpiConfig } from '../model/definition';
import {
  changeText,
  kpiChange,
  kpiFormat,
  periodText,
  pointText,
  sparkPoints,
  sparkShape,
} from '../model/kpi';
import { WidgetContext } from '../model/widget-context';

const periodNames: Record<string, [string, string]> = {
  day: ['day', 'days'],
  week: ['week', 'weeks'],
  month: ['month', 'months'],
  quarter: ['quarter', 'quarters'],
  year: ['year', 'years'],
};

/**
 * A KPI: its number (its measure's label above it when the widget has no title); its change against the period
 * before its filter's, with an arrow, the % and the amount, coloured as its direction is good or bad; and its
 * sparkline, the latest point marked, whose points are read by pointer or keyboard (arrows, Home, End). What doesn't
 * fit a short tile (the sparkline, then the change) is left out.
 */
@Component({
  selector: 'gd-kpi-widget',
  imports: [MatIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (context.data()) {
      <div class="kpi">
        @if (!titled()) {
          <p class="label">{{ config().measure.label }}</p>
        }
        <p class="value" [attr.title]="exact()">{{ valueText() }}</p>
        @if (change(); as change) {
          <p
            class="change"
            [class.good]="change.tone === 'good'"
            [class.bad]="change.tone === 'bad'"
          >
            <mat-icon aria-hidden="true">{{ change.icon }}</mat-icon>
            <span class="shown" aria-hidden="true">
              @if (change.percent) {
                <span class="percent">{{ change.percent }}</span>
              }
              <span class="amount">{{ change.amount }}</span>
              <span class="vs">vs {{ change.period }}</span>
            </span>
            <span class="hidden">{{ change.words }}</span>
          </p>
        } @else if (nothingBefore(); as nothing) {
          <p class="change vs">{{ nothing }}</p>
        }
        @if (shape(); as shape) {
          <div
            class="trend"
            role="group"
            tabindex="0"
            aria-roledescription="sparkline"
            [attr.aria-label]="summary()"
            (pointermove)="point($event)"
            (pointerleave)="at.set(null)"
            (blur)="at.set(null)"
            (keydown)="key($event)"
          >
            <svg class="line" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
              <path [attr.d]="shape.line" vector-effect="non-scaling-stroke" />
            </svg>
            @if (mark(); as mark) {
              @if (mark.read) {
                <span class="hair" aria-hidden="true" [style.left.%]="mark.x"></span>
                <span
                  class="tip"
                  aria-hidden="true"
                  [style.left.%]="mark.x < 50 ? mark.x : null"
                  [style.right.%]="mark.x < 50 ? null : 100 - mark.x"
                  >{{ readout() }}</span
                >
              }
              @if (mark.y !== null) {
                <span
                  class="dot"
                  aria-hidden="true"
                  [style.left.%]="mark.x"
                  [style.top.%]="mark.y"
                ></span>
              }
            }
            <span class="hidden readout" aria-live="polite">{{ readout() }}</span>
          </div>
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
      min-height: 0;
      container-type: size;
    }
    .kpi {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-width: 0;
    }
    p {
      margin: 0;
    }
    .label {
      font: var(--mat-sys-title-small);
      color: var(--mat-sys-on-surface-variant);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .value {
      font-family: var(--mat-sys-display-small-font);
      font-weight: 600;
      font-size: clamp(1.5rem, 14cqi, 2.75rem);
      line-height: 1.2;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .change {
      display: flex;
      align-items: center;
      gap: 4px;
      font: var(--mat-sys-label-large);
      color: var(--mat-sys-on-surface-variant);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .change mat-icon {
      flex: none;
      width: 20px;
      height: 20px;
      font-size: 20px;
    }
    .change.good {
      color: var(--gd-good);
    }
    .change.bad {
      color: var(--gd-bad);
    }
    .shown {
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
      white-space: nowrap;
    }
    .percent,
    .amount {
      font-weight: 600;
      margin-inline-end: 4px;
    }
    .vs {
      color: var(--mat-sys-on-surface-variant);
      font-weight: 400;
    }
    .trend {
      position: relative;
      flex: 1 1 auto;
      min-height: 20px;
      margin-top: 6px;
      border-radius: 4px;
      touch-action: none;
    }
    .trend:focus-visible {
      outline: 2px solid var(--mat-sys-primary);
      outline-offset: 2px;
    }
    .line {
      position: absolute;
      inset: 0;
      width: 100%;
      height: 100%;
      overflow: visible;
    }
    .line path {
      fill: none;
      stroke: var(--mat-sys-outline);
      stroke-width: 2px;
      stroke-linejoin: round;
      stroke-linecap: round;
    }
    .dot {
      position: absolute;
      width: 8px;
      height: 8px;
      margin: -4px 0 0 -4px;
      border-radius: 50%;
      background: var(--gd-chart-1);
      box-shadow: 0 0 0 2px var(--mat-sys-surface-container-low);
      pointer-events: none;
    }
    .hair {
      position: absolute;
      top: 0;
      bottom: 0;
      width: 1px;
      background: var(--mat-sys-outline-variant);
      pointer-events: none;
    }
    .tip {
      position: absolute;
      top: -4px;
      padding: 0 4px;
      border-radius: 4px;
      font: var(--mat-sys-label-small);
      white-space: nowrap;
      background: var(--mat-sys-inverse-surface);
      color: var(--mat-sys-inverse-on-surface);
      pointer-events: none;
    }
    /* A short tile shows its number first: the sparkline goes, then the change. */
    @container (max-height: 88px) {
      .trend {
        display: none;
      }
    }
    @container (max-height: 56px) {
      .change {
        display: none;
      }
    }
  `,
})
export class KpiWidget {
  protected readonly context = inject(WidgetContext);
  private readonly locale = inject(LOCALE_ID);

  protected readonly config = computed(() => this.context.config() as KpiConfig);
  /** A titled widget's title says what its number is; else its measure's label does. */
  protected readonly titled = computed(() => !!this.context.widget().title?.trim());

  /** The point read (by pointer or keyboard); none: the latest is marked. */
  protected readonly at = signal<number | null>(null);

  private readonly column = computed(() =>
    this.context.data()?.columns.find((c) => c.role === 'measure'),
  );
  private readonly format = computed(() => kpiFormat(this.column()));
  private readonly value = computed(() => this.context.data()?.rows[0]?.[0] ?? null);

  protected readonly valueText = computed(() =>
    formatNumber(this.value(), this.format(), this.locale),
  );

  /** The number in full, when it is shortened. */
  protected readonly exact = computed(() => {
    const format = this.format();
    return format?.compact
      ? formatNumber(this.value(), { ...format, compact: false }, this.locale)
      : null;
  });

  private readonly period = computed(() => {
    const previous = this.context.data()?.previous;
    return previous ? periodText(previous.from, previous.to, this.locale) : null;
  });

  protected readonly change = computed(() => {
    const previous = this.context.data()?.previous;
    const compare = this.config().compare;
    const period = this.period();
    if (!previous || !compare || !period) {
      return null;
    }
    const change = kpiChange(this.value(), previous.value, compare.good);
    return change
      ? { ...changeText(change, this.format(), period, this.locale), tone: change.tone, period }
      : null;
  });

  /** Compared, with nothing in the period before (an average of no rows). */
  protected readonly nothingBefore = computed(() => {
    const previous = this.context.data()?.previous;
    return previous && previous.value === null ? `Nothing to compare in ${this.period()}` : null;
  });

  private readonly points = computed(() => {
    const data = this.context.data();
    const config = this.config();
    return sparkPoints(data?.points, config.sparkline?.bucket, config.measure.aggregate);
  });

  protected readonly shape = computed(() => sparkShape(this.points()));

  /** The sparkline in words: what it is of, how many periods, from and to which values. */
  protected readonly summary = computed(() => {
    const points = this.points();
    const config = this.config();
    const bucket = config.sparkline?.bucket ?? 'day';
    const [one, many] = periodNames[bucket] ?? ['period', 'periods'];
    const valued = points.filter((p) => p.value !== null);
    const first = valued[0];
    const last = valued[valued.length - 1];
    const format = this.format();
    const ends =
      first && last
        ? `, from ${formatNumber(first.value, format, this.locale)} to ${formatNumber(last.value, format, this.locale)}`
        : '';
    const latest = this.context.data()?.truncated ? 'the latest ' : '';
    return `${config.measure.label} by ${one}, ${latest}${points.length} ${points.length === 1 ? one : many}${ends}. Use the arrow keys to read each.`;
  });

  /** The point read, else the latest: its place, and whether it is being read (a hairline marks it then). */
  protected readonly mark = computed(() => {
    const shape = this.shape();
    const i = this.at() ?? shape?.last ?? null;
    return shape && i !== null && i < shape.xs.length
      ? { x: shape.xs[i], y: shape.ys[i], read: this.at() !== null }
      : null;
  });

  /** The point read (by pointer or keyboard), as read: its period and value. */
  protected readonly readout = computed(() => {
    const i = this.at();
    const points = this.points();
    return i === null || !points[i]
      ? ''
      : pointText(points[i], this.config().sparkline?.bucket, this.format(), this.locale);
  });

  /** The point nearest the pointer, read. */
  protected point(event: PointerEvent): void {
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const n = this.points().length;
    if (box.width <= 0 || n === 0) {
      return;
    }
    const share = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width));
    this.at.set(Math.round(share * (n - 1)));
  }

  /** Arrows read the point before or after; Home and End, the first and the last. */
  protected key(event: KeyboardEvent): void {
    const n = this.points().length;
    if (n === 0) {
      return;
    }
    const at = this.at() ?? this.shape()?.last ?? n - 1;
    const next =
      event.key === 'ArrowLeft'
        ? Math.max(0, at - 1)
        : event.key === 'ArrowRight'
          ? Math.min(n - 1, at + 1)
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? n - 1
              : null;
    if (next !== null) {
      event.preventDefault();
      this.at.set(next);
    }
  }
}
