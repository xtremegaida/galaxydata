import {
  type DataConfig,
  type Definition,
  type Widget,
  type WidgetConfig,
  configOf,
  isData,
  reachedFrom,
} from './definition';
import { barDefaults, lineDefaults, pieDefaults, tableDefaults } from './widget-defaults';

/**
 * What a widget's rows depend on, as a definition of its own: the widget; the sources linked to its own, and their
 * links; the filters that apply to it (those with a field: a filter being made reaches nothing yet); and of the
 * widgets it listens to on those sources that something is chosen in (`chosen`; all, when not said), what their
 * slices are of (their dimensions, nothing more). Cells are stacked, breakpoints kept and overrides left out, so
 * moving widgets or changing another's title or colours changes no slice; palettes (the dashboard's, and the
 * widget's) and what a bar chart is coloured by are left out too, as they are its colours, not its rows. The
 * editor's previews send it, and are asked for again when it changes, and only then.
 */
export function sliceOf(
  definition: Definition,
  id: string,
  chosen: Readonly<Record<string, unknown>> | null = null,
): Definition {
  const widget = definition.widgets.find((w) => w.id === id);
  if (!widget) {
    return definition;
  }
  const config = configOf(widget);
  if (!isData(config)) {
    return withWidgets(definition, [widget], [], [], []);
  }
  const reached = reachedFrom(definition, config.source);
  const listened = (other: Widget): boolean => {
    if (other.id === id || (chosen !== null && !(other.id in chosen))) {
      return false;
    }
    const emitting = configOf(other);
    if (!isData(emitting) || !emitting.emits || !reached.has(emitting.source)) {
      return false;
    }
    return (
      config.listens.mode === 'all' ||
      (config.listens.mode === 'chosen' && config.listens.widgets.includes(other.id))
    );
  };
  const emitters = definition.widgets.filter(listened).map((w) => ({
    id: w.id,
    title: null,
    config: emitterOf(configOf(w) as DataConfig),
  }));
  const ids = new Set([id, ...emitters.map((w) => w.id)]);
  const own: Widget = {
    ...widget,
    // Its title isn't its rows': renaming it asks for nothing.
    title: null,
    config: {
      ...uncolored(config),
      listens: { ...config.listens, widgets: config.listens.widgets.filter((x) => ids.has(x)) },
    } as WidgetConfig,
  };
  const filters = definition.filters
    .filter((f) => f.field.column && reached.has(f.field.source) && !f.except.includes(id))
    .map((f) => ({ ...f, except: f.except.filter((x) => ids.has(x)) }));
  const sources = definition.sources.filter((s) => reached.has(s.id));
  const links = definition.links.filter((l) => reached.has(l.from) && reached.has(l.to));
  return withWidgets(
    { ...definition, sources, links, filters },
    [own, ...emitters],
    sources,
    links,
    filters,
  );
}

/** A config without what only colours it: its palette, and what a bar chart is coloured by. */
function uncolored(config: DataConfig): DataConfig {
  const rest: Record<string, unknown> = { ...config };
  delete rest['palette'];
  delete rest['colorBy'];
  return rest as DataConfig;
}

function withWidgets(
  definition: Definition,
  widgets: Widget[],
  sources: Definition['sources'],
  links: Definition['links'],
  filters: Definition['filters'],
): Definition {
  const rest: Definition = { ...definition };
  delete rest.palette;
  return {
    ...rest,
    sources,
    links,
    filters,
    widgets,
    layout: {
      ...definition.layout,
      items: Object.fromEntries(widgets.map((w, i) => [w.id, { x: 0, y: i, w: 1, h: 1 }])),
      overrides: {},
    },
  };
}

/** An emitter as a slice needs it: what its slices are of, its kind's defaults else (none of its own conditions). */
function emitterOf(config: DataConfig): WidgetConfig {
  const base = { emits: true, listens: { mode: 'none' as const, widgets: [] } };
  switch (config.kind) {
    case 'pie':
      return { ...pieDefaults(config.source), ...base, dimension: config.dimension };
    case 'bar':
      return {
        ...barDefaults(config.source),
        ...base,
        dimension: config.dimension,
        series: config.series,
      };
    case 'line':
      return {
        ...lineDefaults(config.source),
        ...base,
        dimension: config.dimension,
        series: config.series,
      };
    case 'table':
      return config.mode === 'grouped'
        ? { ...tableDefaults(config.source), ...base, dimensions: config.dimensions }
        : // Its slices are its entity's keys: its own config does, without conditions.
          { ...config, ...base, conditions: [] };
  }
}
