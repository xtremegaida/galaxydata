import type { Schema } from '../../../core/api/api-client';
import type {
  BarConfig,
  Dimension,
  LineConfig,
  Measure,
  PieConfig,
  TableConfig,
  TextConfig,
} from './definition';

export type Listens = Schema<'Listens'>;

const all: Listens = { mode: 'all', widgets: [] };

/** A dimension not yet chosen: its field is to be picked. */
export function blankDimension(): Dimension {
  return { field: { path: [], column: '' }, bucket: null, label: '' };
}

/** Counting rows: a measure that needs no field. */
export function rowCount(): Measure {
  return { aggregate: 'count', field: null, label: 'Rows', format: null };
}

export function textDefaults(): TextConfig {
  return { kind: 'text', markdown: '' };
}

export function barDefaults(source: string | null, listens: Listens = all): BarConfig {
  return {
    kind: 'bar',
    source: source ?? '',
    conditions: [],
    emits: true,
    listens,
    dimension: blankDimension(),
    series: null,
    measures: [rowCount()],
    sort: { by: 'measure', index: 0, descending: true },
    limit: 25,
    orientation: 'vertical',
    stack: 'none',
    labels: false,
    legend: 'bottom',
    xTitle: null,
    yTitle: null,
  };
}

export function lineDefaults(source: string | null, listens: Listens = all): LineConfig {
  return {
    kind: 'line',
    source: source ?? '',
    conditions: [],
    emits: true,
    listens,
    dimension: { ...blankDimension(), bucket: 'month' },
    series: null,
    measures: [rowCount()],
    limit: 1000,
    area: false,
    gaps: 'break',
    labels: false,
    legend: 'bottom',
    xTitle: null,
    yTitle: null,
  };
}

export function pieDefaults(source: string | null, listens: Listens = all): PieConfig {
  return {
    kind: 'pie',
    source: source ?? '',
    conditions: [],
    emits: true,
    listens,
    dimension: blankDimension(),
    measure: rowCount(),
    limit: 10,
    other: true,
    donut: false,
    labels: true,
    legend: 'right',
  };
}

export function tableDefaults(source: string | null, listens: Listens = all): TableConfig {
  return {
    kind: 'table',
    source: source ?? '',
    conditions: [],
    emits: true,
    listens,
    mode: 'grouped',
    dimensions: [blankDimension()],
    measures: [rowCount()],
    columns: [],
    sort: { by: 'measure', index: 0, descending: true },
    pageSize: 50,
  };
}
