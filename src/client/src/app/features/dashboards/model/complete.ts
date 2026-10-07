import type { DataConfig, Measure } from './definition';

/**
 * What a widget of data still needs before it has rows to show (as the editor makes it, a step at a time): its
 * source, its slices' fields, its measures' fields. Null when it has all it needs; the server checks the rest.
 */
export function missingOf(config: DataConfig, sources: readonly { id: string }[]): string | null {
  if (!sources.some((s) => s.id === config.source)) {
    return 'Choose its source.';
  }
  const measures = (list: readonly Measure[]) =>
    list.some((m) => m.aggregate !== 'count' && !m.field?.column)
      ? 'Choose the field of each measure.'
      : null;
  switch (config.kind) {
    case 'pie':
      return !config.dimension.field.column
        ? 'Choose what its slices are.'
        : measures([config.measure]);
    case 'bar':
    case 'line':
      if (!config.dimension.field.column) {
        return config.kind === 'bar'
          ? 'Choose what its bars are.'
          : 'Choose what its line is along.';
      }
      if (config.series && !config.series.field.column) {
        return 'Choose what its series are.';
      }
      return measures(config.measures);
    case 'table':
      if (config.mode === 'raw') {
        return config.columns.length === 0 || config.columns.some((c) => !c.field.column)
          ? 'Choose its columns.'
          : null;
      }
      return config.dimensions.length === 0 || config.dimensions.some((d) => !d.field.column)
        ? 'Choose what its rows are grouped by.'
        : measures(config.measures);
  }
}
