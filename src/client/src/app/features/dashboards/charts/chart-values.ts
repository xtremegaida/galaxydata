import { cellText, type TypeDto } from '../../browse/grid/grid-columns';
import type { Bucket, NumberFormat } from '../model/definition';

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
 * (1.2K) when asked. Text of a whole number or decimal is formatted as it is, not as a double would round it.
 */
export function formatNumber(
  value: unknown,
  format: NumberFormat | null | undefined,
  locale: string,
): string {
  if (value === null || value === undefined) {
    return '—';
  }
  const options: Intl.NumberFormatOptions = {};
  if (format?.decimals !== null && format?.decimals !== undefined) {
    options.minimumFractionDigits = format.decimals;
    options.maximumFractionDigits = format.decimals;
  } else {
    options.maximumFractionDigits = 20;
  }
  if (format?.compact) {
    options.notation = 'compact';
    options.maximumFractionDigits = format.decimals ?? 1;
  }
  let text: string;
  try {
    // Intl takes a number's text as it is (decimals beyond a double's digits), which TypeScript's types don't say.
    text = new Intl.NumberFormat(locale, options).format(
      (typeof value === 'string' ? value : Number(value)) as number,
    );
  } catch {
    text = String(value);
  }
  return `${format?.prefix ?? ''}${text}${format?.suffix ?? ''}`;
}

const monthsLong = (locale: string) =>
  new Intl.DateTimeFormat(locale, { month: 'long', timeZone: 'UTC' });

/**
 * A dimension's value as a category is labelled: a period as it is read (January 2026, Q1 2026, the week of a
 * Monday), a part that repeats by its name (Monday, January, Q3, 13:00), no value as such, the rest as a cell shows it.
 */
export function categoryText(
  value: unknown,
  type: TypeDto,
  bucket: Bucket | null | undefined,
  locale: string,
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
