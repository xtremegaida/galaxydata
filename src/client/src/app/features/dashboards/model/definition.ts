import type { Schema } from '../../../core/api/api-client';

/** What a dashboard shows: as the API reads and writes it (see `Dashboards/Definition.cs`). */
export type Definition = Schema<'DashboardDefinition'>;
export type Widget = Schema<'DashboardWidget'>;
export type Source = Schema<'DashboardSource'>;
export type Filter = Schema<'DashboardFilter'>;
export type Link = Schema<'DashboardLink'>;
export type Layout = Schema<'DashboardLayout'>;
export type Breakpoint = Schema<'Breakpoint'>;
export type Placement = Schema<'Placement'>;
export type ConditionValue = Schema<'ConditionValue'>;
export type FieldRef = Schema<'FieldRef'>;
export type Dimension = Schema<'Dimension'>;
export type Measure = Schema<'Measure'>;
export type NumberFormat = Schema<'NumberFormat'>;
export type Bucket = Schema<'Bucket'>;
export type LegendPosition = Schema<'LegendPosition'>;

export type TextConfig = Schema<'WidgetConfigTextConfig'> & { kind: 'text' };
export type PieConfig = Schema<'WidgetConfigPieConfig'> & { kind: 'pie' };
export type BarConfig = Schema<'WidgetConfigBarConfig'> & { kind: 'bar' };
export type LineConfig = Schema<'WidgetConfigLineConfig'> & { kind: 'line' };
export type TableConfig = Schema<'WidgetConfigTableConfig'> & { kind: 'table' };

/** What a widget shows, by its kind. */
export type WidgetConfig = TextConfig | PieConfig | BarConfig | LineConfig | TableConfig;

/** The kinds of widgets of data: they have a source, queries and rows. */
export type DataConfig = PieConfig | BarConfig | LineConfig | TableConfig;

export type ChartConfig = PieConfig | BarConfig | LineConfig;

/** A widget's rows, as the server gives them. */
export type WidgetData = Schema<'WidgetDataDto'>;
export type WidgetColumn = Schema<'WidgetColumnDto'>;

/** What a viewer has set: filters' values and widgets' selections. */
export type DashboardState = Schema<'DashboardState'>;
export type DashboardIssue = Schema<'DashboardIssue'>;
export type Selection = Schema<'SelectionState'>;

/** A slice's key: the values of its dimensions (as `ValueCodec` writes them). */
export type Key = readonly unknown[];

/** A widget's config, of the kind it says. */
export function configOf(widget: Widget): WidgetConfig {
  return widget.config as WidgetConfig;
}

export function isData(config: WidgetConfig): config is DataConfig {
  return config.kind !== 'text';
}

export function isChart(config: WidgetConfig): config is ChartConfig {
  return config.kind === 'pie' || config.kind === 'bar' || config.kind === 'line';
}

/** A widget's sources' ids, and whether it is one of data, by id. */
export function widgetsById(definition: Definition): ReadonlyMap<string, Widget> {
  return new Map(definition.widgets.map((widget) => [widget.id, widget]));
}

/** A key as text: the same for equal keys, whatever their values' types. */
export function keyText(key: Key): string {
  return JSON.stringify(key);
}

/** The ids a new item of a list may have: the first of `prefix`, `prefix-2`, … the list hasn't. */
export function newId(prefix: string, taken: Iterable<string>): string {
  const used = new Set(taken);
  const base =
    prefix
      .toLowerCase()
      .replace(/[^a-z0-9-]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 26) || 'item';
  if (!used.has(base)) {
    return base;
  }
  for (let i = 2; ; i++) {
    const id = `${base}-${i}`;
    if (!used.has(id)) {
      return id;
    }
  }
}

/** The sources linked to `source`, through any others, itself among them. */
export function reachedFrom(definition: Definition, source: string): Set<string> {
  const reached = new Set([source]);
  const next = [source];
  while (next.length > 0) {
    const at = next.pop()!;
    for (const link of definition.links) {
      const other = link.from === at ? link.to : link.to === at ? link.from : null;
      if (other !== null && !reached.has(other)) {
        reached.add(other);
        next.push(other);
      }
    }
  }
  return reached;
}
