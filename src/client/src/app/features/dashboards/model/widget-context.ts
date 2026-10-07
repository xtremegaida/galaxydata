import { Injectable, type Signal, signal } from '@angular/core';
import type { Problem } from '../../../core/api/problem';
import type { EntityColor, PaletteView } from '../charts/series-colors';
import type { Key, Selection, Widget, WidgetConfig, WidgetData } from './definition';

/** What choosing a slice does: chooses it alone, adds it to (or takes it from) those chosen, leaves it out, or chooses none. */
export type SelectionAction = 'replace' | 'add' | 'exclude' | 'clear';

/**
 * A widget as its frame gives it to what shows it (its kind's view, made apart from the frame): its config, its
 * rows (kept while they are read again), what went wrong, its slices chosen, and what it may ask for. The frame
 * provides one for each widget.
 */
@Injectable()
export class WidgetContext {
  readonly widget = signal<Widget>({ id: '', title: null, config: { kind: 'text', markdown: '' } });
  readonly data = signal<WidgetData | null>(null);
  readonly loading = signal(false);
  readonly problem = signal<Problem | null>(null);
  /** The slices chosen in this widget, which it highlights (they filter the others). */
  readonly selection = signal<Selection | null>(null);
  /** Whether a chart is shown as a table of its rows, which a keyboard reaches. */
  readonly asTable = signal(false);
  /** The page of a table's rows asked for, from its first. */
  readonly offset = signal(0);
  /** Whether the dashboard is being edited (charts don't take clicks then, unless trying). */
  readonly editing = signal(false);
  /** Whether choosing slices does anything: widgets that emit, on a page that lets them. */
  readonly choosing = signal(true);
  /** The palette a chart is drawn with (its own, else the dashboard's); none: the theme's colours. */
  readonly palette = signal<PaletteView | null>(null, {
    equal: (a, b) => a?.id === b?.id && a?.basis === b?.basis,
  });
  /** What a chart coloured, and how (its view sets it): the editor lists them, to give labels their colours. */
  readonly colors = signal<readonly EntityColor[]>([], {
    equal: (a, b) => JSON.stringify(a) === JSON.stringify(b),
  });

  /** What choosing a slice asks of the dashboard; the frame sets it. */
  choose: (key: Key, action: SelectionAction) => void = () => undefined;

  get config(): Signal<WidgetConfig> {
    return this.configOf;
  }

  private readonly configOf = signal<WidgetConfig>({ kind: 'text', markdown: '' });

  /** Sets the widget, and its config with it. */
  show(widget: Widget): void {
    this.widget.set(widget);
    this.configOf.set(widget.config as WidgetConfig);
  }
}
