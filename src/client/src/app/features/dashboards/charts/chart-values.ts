import { cellText, type TypeDto } from '../../browse/grid/grid-columns';
import type { BooleanLabels, Bucket, NumberFormat } from '../model/definition';

/**
 * A value as a chart draws it: a number. Whole numbers of 64 bits and decimals come as text, exact; drawing takes
 * them as numbers (near enough for a mark), while labels and tooltips show them exactly (`formatNumber`). Nothing
 * for null, or text that isn't a number.
 */
export function chartNumber(value: unknown): number | null {
  if (typeof value === 'number') {
    return Number.isFinite(value) ? value : null;
  }
  if (typeof value === 'string' && value.trim() !== '') {
    const number = Number(value);
    return Number.isFinite(number) ? number : null;
  }
  return null;
}

/**
 * A number as a label shows it: in the locale, with the measure's decimals, text before and after, and shortened
 * (1.2K) when asked; a percentage times 100 with % (to one place unless decimals are given); a duration in its two
 * largest parts (`durationText`). Text of a whole number or decimal is formatted as it is, not as a double would
 * round it.
 */
export function formatNumber(
  value: unknown,
  format: NumberFormat | null | undefined,
  locale: string,
): string {
  if (value === null || value === undefined) {
    return '—';
  }
  let text: string;
  switch (format?.style) {
    case 'percent':
      text = `${plainNumber(hundredTimes(value), format, locale, 1)}%`;
      break;
    case 'duration':
      text = durationText(value, format.unit ?? 'seconds', format.decimals, locale);
      break;
    default:
      text = plainNumber(value, format, locale, 20);
  }
  return `${format?.prefix ?? ''}${text}${format?.suffix ?? ''}`;
}

/** A number in the locale: with the format's decimals (or at most `places`), shortened when asked. */
function plainNumber(
  value: unknown,
  format: NumberFormat | null | undefined,
  locale: string,
  places: number,
): string {
  const options: Intl.NumberFormatOptions = {};
  if (format?.decimals !== null && format?.decimals !== undefined) {
    options.minimumFractionDigits = format.decimals;
    options.maximumFractionDigits = format.decimals;
  } else {
    options.maximumFractionDigits = places;
  }
  if (format?.compact) {
    options.notation = 'compact';
    options.maximumFractionDigits = format.decimals ?? 1;
  }
  try {
    // Intl takes a number's text as it is (decimals beyond a double's digits), which TypeScript's types don't say.
    return new Intl.NumberFormat(locale, options).format(
      (typeof value === 'string' ? value : Number(value)) as number,
    );
  } catch {
    return String(value);
  }
}

/** A number times 100: the text of a whole number or decimal exactly (its point moved), a double near enough. */
function hundredTimes(value: unknown): unknown {
  const exact = typeof value === 'string' ? /^(-?)(\d+)(?:\.(\d*))?$/.exec(value.trim()) : null;
  if (exact) {
    const [, sign, whole, fraction = ''] = exact;
    const digits = `${whole}${fraction.padEnd(2, '0')}`;
    const point = whole.length + 2;
    const rest = digits.slice(point);
    return `${sign}${digits.slice(0, point).replace(/^0+(?=\d)/, '')}${rest ? '.' + rest : ''}`;
  }
  // 0.07 × 100 is 7.000000000000001 as a double: its 15 digits are what it means.
  return Number((Number(value) * 100).toPrecision(15));
}

type DurationUnit = NonNullable<NumberFormat['unit']>;

const millisecondsIn: Readonly<Record<DurationUnit, number>> = {
  milliseconds: 1,
  seconds: 1000,
  minutes: 60_000,
  hours: 3_600_000,
  days: 86_400_000,
};

/**
 * A length of time (a number of `unit`s) in its two largest parts, the second left out when it is none: 3d 4h,
 * 1h 5m, 2m 15s, 2m; under a minute in seconds (45s; to one place, unless decimals are given), under a second in
 * milliseconds (850ms; whole, unless decimals are given).
 */
export function durationText(
  value: unknown,
  unit: DurationUnit,
  decimals: number | null | undefined,
  locale: string,
): string {
  const number = Number(value);
  if (value === null || value === '' || !Number.isFinite(number)) {
    return String(value);
  }
  const sign = number < 0 ? '-' : '';
  const ms = Math.abs(number) * millisecondsIn[unit];
  const fixed = (n: number, places: number | null) =>
    new Intl.NumberFormat(
      locale,
      places === null
        ? { maximumFractionDigits: 1 }
        : { minimumFractionDigits: places, maximumFractionDigits: places },
    ).format(n);
  const whole = (n: number) => fixed(n, 0);
  if (ms < 1000) {
    return `${sign}${fixed(ms, decimals ?? 0)}ms`;
  }
  const places = decimals ?? 1;
  if (Math.round((ms / 1000) * 10 ** places) / 10 ** places < 60) {
    return `${sign}${fixed(ms / 1000, decimals ?? null)}s`;
  }
  const parts = (first: number, size: number, big: string, small: string) =>
    `${sign}${whole(Math.floor(first / size))}${big}${first % size ? ` ${first % size}${small}` : ''}`;
  const seconds = Math.round(ms / 1000);
  if (seconds < 3600) {
    return parts(seconds, 60, 'm', 's');
  }
  const minutes = Math.round(ms / 60_000);
  if (minutes < 1440) {
    return parts(minutes, 60, 'h', 'm');
  }
  return parts(Math.round(ms / 3_600_000), 24, 'd', 'h');
}

/** A true/false value as a dashboard shows it: by its label, or yes or no where it has none (or an empty one). */
export function booleanText(value: unknown, labels: BooleanLabels | null | undefined): string {
  const truth = value === true || value === 'true' || value === 1;
  return truth ? labels?.true?.trim() || 'Yes' : labels?.false?.trim() || 'No';
}

const monthsLong = (locale: string) =>
  new Intl.DateTimeFormat(locale, { month: 'long', timeZone: 'UTC' });

/**
 * A dimension's value as a category is labelled: a period as it is read (January 2026, Q1 2026, the week of a
 * Monday), a part that repeats by its name (Monday, January, Q3, 13:00), true and false by the dimension's labels
 * for them (or yes and no), no value as such, the rest as a cell shows it.
 */
export function categoryText(
  value: unknown,
  type: TypeDto,
  bucket: Bucket | null | undefined,
  locale: string,
  booleans: BooleanLabels | null = null,
): string {
  if (value === null || value === undefined) {
    return '(no value)';
  }
  const day =
    typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value)
      ? new Date(`${value}T00:00:00Z`)
      : null;
  switch (bucket) {
    case 'day':
      return day
        ? new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(day)
        : String(value);
    case 'week':
      return day
        ? `Week of ${new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(day)}`
        : String(value);
    case 'month':
      return day
        ? new Intl.DateTimeFormat(locale, {
            month: 'short',
            year: 'numeric',
            timeZone: 'UTC',
          }).format(day)
        : String(value);
    case 'quarter':
      return day
        ? `Q${Math.floor(day.getUTCMonth() / 3) + 1} ${day.getUTCFullYear()}`
        : String(value);
    case 'year':
      return day ? String(day.getUTCFullYear()) : String(value);
    case 'quarterOfYear':
      return `Q${String(value)}`;
    case 'monthOfYear': {
      const month = Number(value);
      return month >= 1 && month <= 12
        ? monthsLong(locale).format(new Date(Date.UTC(2026, month - 1, 1)))
        : String(value);
    }
    case 'dayOfWeek': {
      // 2026-01-05 was a Monday; ISO days of the week run from 1 (Monday) to 7 (Sunday).
      const weekday = Number(value);
      return weekday >= 1 && weekday <= 7
        ? new Intl.DateTimeFormat(locale, { weekday: 'long', timeZone: 'UTC' }).format(
            new Date(Date.UTC(2026, 0, 4 + weekday)),
          )
        : String(value);
    }
    case 'hourOfDay':
      return `${String(value).padStart(2, '0')}:00`;
    default:
      if (
        type.kind === 'int64' ||
        type.kind === 'decimal' ||
        type.kind === 'int16' ||
        type.kind === 'int32' ||
        type.kind === 'double' ||
        type.kind === 'single'
      ) {
        return formatNumber(value, null, locale);
      }
      if (type.kind === 'boolean') {
        return booleanText(value, booleans);
      }
      return cellText(value, type);
  }
}

/** The periods from `first` to `last` (first days, as `yyyy-MM-dd`) of a bucket that is a period; null for any other, or too many. */
export function periodsBetween(
  first: string,
  last: string,
  bucket: Bucket | null | undefined,
  max = 2000,
): string[] | null {
  const step: Record<string, (date: Date) => void> = {
    day: (d) => d.setUTCDate(d.getUTCDate() + 1),
    week: (d) => d.setUTCDate(d.getUTCDate() + 7),
    month: (d) => d.setUTCMonth(d.getUTCMonth() + 1),
    quarter: (d) => d.setUTCMonth(d.getUTCMonth() + 3),
    year: (d) => d.setUTCFullYear(d.getUTCFullYear() + 1),
  };
  const next = bucket ? step[bucket] : undefined;
  if (!next || !/^\d{4}-\d{2}-\d{2}$/.test(first) || !/^\d{4}-\d{2}-\d{2}$/.test(last)) {
    return null;
  }
  const periods: string[] = [];
  const at = new Date(`${first}T00:00:00Z`);
  const end = new Date(`${last}T00:00:00Z`);
  while (at <= end) {
    if (periods.length >= max) {
      return null;
    }
    periods.push(at.toISOString().slice(0, 10));
    next(at);
  }
  return periods;
}

/** Text made safe to put in HTML (tooltips are HTML; categories come from the database). */
export function escapeHtml(text: string): string {
  return text.replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] ?? c,
  );
}
