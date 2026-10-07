import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  input,
} from '@angular/core';
import { elementSize } from '../../../core/browser/element-size';
import { type Definition, type Widget, configOf } from '../model/definition';
import { WIDGET_KINDS, kindOf } from '../model/widget-registry';
import { WIDGET_EDITING } from '../view/widget-editing';
import { WidgetFrame } from '../view/widget-frame';
import {
  type Breakpoint,
  type MinSizes,
  breakpointFor,
  effectiveLayout,
  minColumns,
  readingOrder,
} from './grid-layout';

/** A widget as the grid places it: its cell, as CSS grid writes it. */
export interface PlacedWidget {
  readonly widget: Widget;
  readonly x: number;
  readonly y: number;
  readonly w: number;
  readonly h: number;
}

/**
 * The dashboard's grid: as many columns as the breakpoint its own width reaches (an embedded dashboard's is its
 * frame's), rows of the layout's height, the widgets in their cells as the breakpoint lays them out. A CSS grid,
 * so its height is its widgets', and its order the reading order (tabbing follows what is seen).
 */
@Component({
  selector: 'gd-dashboard-grid',
  imports: [WidgetFrame],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[style.--gd-columns]': 'columns()',
    '[style.--gd-row-height]': 'definition().layout.rowHeight + "px"',
    '[style.--gd-gap]': 'definition().layout.gap + "px"',
    '[attr.data-gd-breakpoint]': 'breakpoint().id',
  },
  template: `
    @for (placed of placed(); track placed.widget.id) {
      <gd-widget-frame
        [widget]="placed.widget"
        [editing]="editing !== null"
        [style.grid-column]="placed.x + 1 + ' / span ' + placed.w"
        [style.grid-row]="placed.y + 1 + ' / span ' + placed.h"
        [attr.data-gd-cell]="placed.x + ',' + placed.y + ',' + placed.w + ',' + placed.h"
      />
    }
  `,
  styles: `
    :host {
      display: grid;
      grid-template-columns: repeat(var(--gd-columns), minmax(0, 1fr));
      grid-auto-rows: var(--gd-row-height);
      gap: var(--gd-gap);
      scrollbar-gutter: stable;
    }
  `,
})
export class DashboardGrid {
  readonly definition = input.required<Definition>();
  /** A breakpoint to show whatever the width (the editor's); else the one the width reaches. */
  readonly forced = input<string | null>(null);

  private readonly kinds = inject(WIDGET_KINDS);
  /** The editor's, on its canvas. */
  protected readonly editing = inject(WIDGET_EDITING, { optional: true });
  private readonly size = elementSize(inject(ElementRef<HTMLElement>).nativeElement);

  readonly breakpoint = computed<Breakpoint>(() => {
    const breakpoints = this.definition().layout.breakpoints;
    const forced = this.forced();
    return (
      breakpoints.find((b) => b.id === forced) ??
      breakpointFor(this.size()?.width ?? null, breakpoints)
    );
  });

  readonly columns = computed(() => this.breakpoint().columns);

  /** Widgets' least sizes at the breakpoint, from their kinds' least widths. */
  readonly mins = computed<MinSizes>(() => {
    const breakpoint = this.breakpoint();
    const gap = this.definition().layout.gap;
    return Object.fromEntries(
      this.definition().widgets.map((w) => [
        w.id,
        {
          w: minColumns(kindOf(this.kinds, configOf(w))?.minWidthPx ?? 120, breakpoint, gap),
          h: 1,
        },
      ]),
    );
  });

  readonly layout = computed(() =>
    effectiveLayout(
      this.definition().layout,
      this.breakpoint().id,
      this.definition().widgets.map((w) => w.id),
      this.mins(),
    ),
  );

  readonly placed = computed<PlacedWidget[]>(() => {
    const laidOut = this.layout().cells;
    // While the editor moves a widget, its cells are the drop's; the order stays the layout's, so the widget's
    // place in the page (and the keyboard's focus on it) holds till it is dropped.
    const cells = this.editing?.preview() ?? laidOut;
    const widgets = new Map(this.definition().widgets.map((w) => [w.id, w]));
    return readingOrder(laidOut)
      .filter((id) => widgets.has(id) && id in cells)
      .map((id) => ({ widget: widgets.get(id)!, ...cells[id] }));
  });
}
