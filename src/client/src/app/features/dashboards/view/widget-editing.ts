import { InjectionToken, type Signal } from '@angular/core';
import type { Placement } from '../layout/grid-layout';

/**
 * What the editor does with the widgets on its canvas, which their frames and the grid ask of it (the view never
 * knows the editor): which is chosen, which is being moved, the cells while a widget is dragged, and choosing,
 * moving and resizing by pointer and keyboard. Only the editor provides it.
 */
export interface WidgetEditing {
  /** The widget chosen, whose frame is marked. */
  readonly selected: Signal<string | null>;
  /** Whether widgets can be moved and resized at the breakpoint shown (the designed one, or one laid out by hand). */
  readonly movable: Signal<boolean>;
  /** Whether clicking slices chooses them, to try the dashboard out ("Interact"); else a click chooses the widget. */
  readonly interacting: Signal<boolean>;
  /** The widget moved by keyboard, if any. */
  readonly moving: Signal<string | null>;
  /** The cells while a widget is moved or resized (its drop's), shown in place of the layout's; null otherwise. */
  readonly preview: Signal<Readonly<Record<string, Placement>> | null>;
  /** A widget's name, as moves are said by. */
  nameOf(id: string): string;
  select(id: string): void;
  startMove(id: string, event: PointerEvent): void;
  startResize(id: string, event: PointerEvent): void;
  /** A key pressed on a widget's move handle. */
  key(id: string, event: KeyboardEvent): void;
}

export const WIDGET_EDITING = new InjectionToken<WidgetEditing>('WIDGET_EDITING');
