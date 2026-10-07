import { categoryText, formatNumber } from '../charts/chart-values';
import type {
  Bucket,
  ConditionValue,
  DataConfig,
  Filter,
  Key,
  Selection,
  Widget,
} from './definition';
import { configOf, isData } from './definition';

type RelativeRange = NonNullable<ConditionValue['relative']>;

const unitNames: Record<RelativeRange['unit'], [string, string]> = {
  day: ['day', 'days'],
  week: ['week', 'weeks'],
  month: ['month', 'months'],
  quarter: ['quarter', 'quarters'],
  year: ['year', 'years'],
};

/** A value as a viewer reads it: text as it is, numbers in the locale, no value said so. */
export function valueText(value: unknown, locale: string, bucket: Bucket | null = null): string {
  if (value === null || value === undefined) {
    return '(no value)';
  }
  if (bucket) {
    return categoryText(value, { kind: 'string', nullable: true, text: 'string?' }, bucket, locale);
  }
  if (typeof value === 'number') {
    return formatNumber(value, null, locale);
  }
  if (typeof value === 'boolean') {
    return value ? 'yes' : 'no';
  }
  return String(value);
}

/** A period relative to today, as said: "last 3 months", "this quarter", "previous month", "year to date". */
export function relativeText(range: RelativeRange): string {
  const [one, many] = unitNames[range.unit];
  switch (range.mode) {
    case 'last':
      return range.count === 1 ? `last ${one}` : `last ${range.count} ${many}`;
    case 'this':
      return range.unit === 'day' ? 'today' : `this ${one}`;
    case 'previous':
      return range.count === 1
        ? range.unit === 'day'
          ? 'yesterday'
          : `previous ${one}`
        : `previous ${range.count} ${many}`;
    case 'toDate':
      return `${one} to date`;
  }
}

/** What a filter's value keeps, briefly: "open, shipped", "not EMEA", "10 to 100", "last 7 days", "contains Ada". */
export function filterValueText(value: ConditionValue | null, locale: string): string {
  if (!value) {
    return 'any';
  }
  const one = (v: unknown) => valueText(v, locale);
  switch (value.op) {
    case 'in':
      return (value.values ?? []).map(one).join(', ');
    case 'notIn':
      return `not ${(value.values ?? []).map(one).join(', ')}`;
    case 'between':
      return `${one(value.value)} to ${one(value.valueTo)}`;
    case 'ge':
      return `from ${one(value.value)}`;
    case 'gt':
      return `above ${one(value.value)}`;
    case 'le':
      return `up to ${one(value.value)}`;
    case 'lt':
      return `below ${one(value.value)}`;
    case 'relative':
      return value.relative ? relativeText(value.relative) : 'any time';
    case 'contains':
      return `contains ${one(value.value)}`;
    case 'startsWith':
      return `starts with ${one(value.value)}`;
    case 'eq':
      return one(value.value);
    default:
      return value.op;
  }
}

/** The value of a filter that holds: the viewer's (null: cleared), or else the dashboard's. */
export function filterValueOf(
  filter: Filter,
  set: Readonly<Record<string, ConditionValue | null>>,
): ConditionValue | null {
  return filter.id in set ? set[filter.id] : filter.value;
}

/** What the parts of a widget's slices are: their labels and periods (a chart's category and series, a table's columns). */
export function keyParts(config: DataConfig): { label: string; bucket: Bucket | null }[] {
  const part = (d: { label: string; bucket?: Bucket | null }) => ({
    label: d.label,
    bucket: d.bucket ?? null,
  });
  switch (config.kind) {
    case 'pie':
      return [part(config.dimension)];
    case 'bar':
    case 'line':
      return config.series
        ? [part(config.dimension), part(config.series)]
        : [part(config.dimension)];
    case 'table':
      return config.mode === 'grouped' ? config.dimensions.map(part) : [];
  }
}

/** A widget's slices chosen, as a chip says them: what they are of, and which ("open, shipped"; "not EMEA"). */
export function selectionText(
  widget: Widget,
  selection: Selection,
  locale: string,
): { name: string; values: string } {
  const config = configOf(widget);
  const parts = isData(config) ? keyParts(config) : [];
  const name =
    parts.length > 0 && parts.every((p) => p.label)
      ? parts.map((p) => p.label).join(' · ')
      : (widget.title ?? widget.id);
  const keyText = (key: Key) =>
    key.map((value, i) => valueText(value, locale, parts[i]?.bucket ?? null)).join(' · ');
  const values = selection.keys.map(keyText).join(', ');
  return { name, values: selection.mode === 'exclude' ? `not ${values}` : values };
}
