import { InjectionToken, type Type } from '@angular/core';
import type { Listens } from './widget-defaults';
import type { WidgetConfig } from './definition';
import {
  barDefaults,
  lineDefaults,
  pieDefaults,
  tableDefaults,
  textDefaults,
} from './widget-defaults';

/**
 * A kind of widget: what it is called and looks like in the palette, its size when added (in the columns of a
 * designed breakpoint of 12), the least width it is usable at, whether it has rows, what a new one is, and the
 * components that show it and edit it (loaded when needed). The server's half is its `IWidgetKind`.
 */
export interface WidgetKind {
  readonly kind: WidgetConfig['kind'];
  readonly label: string;
  /** A Material Symbols name. */
  readonly icon: string;
  readonly defaultSize: { readonly w: number; readonly h: number };
  /** The least width it is usable at, in pixels: turned into columns at each breakpoint. */
  readonly minWidthPx: number;
  readonly data: boolean;
  defaults(source: string | null, listens?: Listens): WidgetConfig;
  /** The component that shows it, which injects its `WidgetContext`. */
  view(): Promise<Type<unknown>>;
  /** The component that edits its own settings: inputs `config` (a model) and `entity` (its source's). */
  editor(): Promise<Type<unknown>>;
}

const chartView = () => import('../widgets/chart-widget').then((m) => m.ChartWidget);
const settings = () => import('../editor/kinds/kind-settings');

/** The kinds the application has, in the palette's order. */
export const builtInKinds: readonly WidgetKind[] = [
  {
    kind: 'text',
    label: 'Text',
    icon: 'notes',
    defaultSize: { w: 12, h: 2 },
    minWidthPx: 120,
    data: false,
    defaults: () => textDefaults(),
    view: () => import('../widgets/text-widget').then((m) => m.TextWidget),
    editor: () => settings().then((m) => m.TextSettings),
  },
  {
    kind: 'bar',
    label: 'Bar chart',
    icon: 'bar_chart',
    defaultSize: { w: 6, h: 6 },
    minWidthPx: 240,
    data: true,
    defaults: barDefaults,
    view: chartView,
    editor: () => settings().then((m) => m.BarSettings),
  },
  {
    kind: 'line',
    label: 'Line chart',
    icon: 'show_chart',
    defaultSize: { w: 6, h: 6 },
    minWidthPx: 240,
    data: true,
    defaults: lineDefaults,
    view: chartView,
    editor: () => settings().then((m) => m.LineSettings),
  },
  {
    kind: 'pie',
    label: 'Pie chart',
    icon: 'pie_chart',
    defaultSize: { w: 4, h: 6 },
    minWidthPx: 200,
    data: true,
    defaults: pieDefaults,
    view: chartView,
    editor: () => settings().then((m) => m.PieSettings),
  },
  {
    kind: 'table',
    label: 'Table',
    icon: 'table',
    defaultSize: { w: 12, h: 6 },
    minWidthPx: 280,
    data: true,
    defaults: tableDefaults,
    view: () => import('../widgets/table-widget').then((m) => m.TableWidget),
    editor: () => settings().then((m) => m.TableSettings),
  },
];

/** The kinds of widgets there are; a kind the server has and this client doesn't is shown as such. */
export const WIDGET_KINDS = new InjectionToken<readonly WidgetKind[]>('WIDGET_KINDS', {
  providedIn: 'root',
  factory: () => builtInKinds,
});

export function kindOf(kinds: readonly WidgetKind[], config: WidgetConfig): WidgetKind | undefined {
  return kinds.find((kind) => kind.kind === config.kind);
}
