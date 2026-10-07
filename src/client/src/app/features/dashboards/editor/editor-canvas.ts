import { LiveAnnouncer } from '@angular/cdk/a11y';
import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  signal,
  untracked,
} from '@angular/core';
import {
  type Cells,
  type Placement,
  effectiveLayout,
  minColumns,
  moveTo,
  resizeTo,
} from '../layout/grid-layout';
import { configOf } from '../model/definition';
import { placementText, withCells } from '../model/definition-ops';
import { WIDGET_KINDS, kindOf } from '../model/widget-registry';
import { DashboardView } from '../view/dashboard-view';
import { WIDGET_EDITING, type WidgetEditing } from '../view/widget-editing';
import { EditorStore } from './editor-store';

/** A drag under way: what it moves or resizes, from where, and the cells before it. */
interface Drag {
  readonly id: string;
  readonly kind: 'move' | 'resize';
  readonly before: Cells;
  /** Where in the widget it was taken, in cells (moves). */
  readonly grab: { readonly x: number; readonly y: number };
  readonly pointer: number;
  readonly target: HTMLElement;
}

let drags = 0;

/**
 * The editor's canvas: the dashboard as viewers see it, at the breakpoint chosen (narrower ones in a frame as wide
 * as they are), its widgets chosen by clicking them, moved by their handles and resized by their corners, by
 * pointer (the drop where the preview showed it; Escape puts it back) or keyboard (Enter or Space on a handle,
 * arrows to move, Shift and arrows to resize, Enter to drop, Escape to put it back, each step said). A move is one
 * step of the history. At a narrower breakpoint not laid out by hand, nothing moves till it is customized.
 */
@Component({
  selector: 'gd-editor-canvas',
  imports: [DashboardView],
  providers: [{ provide: WIDGET_EDITING, useExisting: EditorCanvas }],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.narrower]': '!store.designed()' },
  template: `
    <div class="page" [style.max-width]="width()">
      @if (store.draft().widgets.length === 0) {
        <p class="empty">
          A blank dashboard is a blank page: add a widget (a heading is a text widget).
        </p>
      }
      <gd-dashboard-view [breakpoint]="store.breakpoint().id" />
    </div>
  `,
  styles: `
    :host {
      display: block;
      padding: 16px;
      box-sizing: border-box;
      min-height: 100%;
      background: var(--mat-sys-surface-container-lowest);
    }
    .page {
      margin: 0 auto;
      min-height: 320px;
    }
    :host(.narrower) .page {
      padding: 12px;
      border: 1px dashed var(--mat-sys-outline);
      border-radius: var(--mat-sys-corner-medium);
      background: var(--mat-sys-surface);
    }
    .empty {
      margin: 48px 0;
      text-align: center;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-large);
    }
  `,
})
export class EditorCanvas implements WidgetEditing {
  protected readonly store = inject(EditorStore);
  private readonly kinds = inject(WIDGET_KINDS);
  private readonly announcer = inject(LiveAnnouncer);
  private readonly element = inject(ElementRef<HTMLElement>);
  private readonly document = inject(DOCUMENT);

  readonly selected = this.store.selected.asReadonly();
  readonly movable = this.store.laidOut;
  readonly interacting = this.store.interacting.asReadonly();
  readonly moving = signal<string | null>(null);
  readonly preview = signal<Readonly<Record<string, Placement>> | null>(null);

  private drag: Drag | null = null;
  private frame: number | null = null;
  private pending: PointerEvent | null = null;

  /** How wide the page is shown: whole at the designed breakpoint, else as wide as a screen of the breakpoint. */
  protected readonly width = computed(() => {
    const breakpoints = this.store.draft().layout.breakpoints;
    const shown = this.store.breakpoint();
    const index = breakpoints.indexOf(shown);
    const next = breakpoints[index + 1];
    if (!next) {
      return null;
    }
    const width =
      index === 0
        ? Math.min(next.minWidth - 1, 390)
        : Math.round((shown.minWidth + next.minWidth - 1) / 2);
    return `${Math.max(width, 240)}px`;
  });

  /** The cells at the breakpoint shown, as laid out (what moves start from). */
  private readonly cells = computed<Cells>(() => {
    const definition = this.store.draft();
    return effectiveLayout(
      definition.layout,
      this.store.breakpoint().id,
      definition.widgets.map((w) => w.id),
      this.mins(),
    ).cells;
  });

  private readonly mins = computed(() => {
    const definition = this.store.draft();
    const breakpoint = this.store.breakpoint();
    return Object.fromEntries(
      definition.widgets.map((w) => [
        w.id,
        {
          w: minColumns(
            kindOf(this.kinds, configOf(w))?.minWidthPx ?? 120,
            breakpoint,
            definition.layout.gap,
          ),
          h: 1,
        },
      ]),
    );
  });

  constructor() {
    const move = (event: PointerEvent) => this.pointerMoved(event);
    const up = (event: PointerEvent) => this.pointerUp(event);
    const keys = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && this.drag) {
        event.preventDefault();
        this.cancelDrag();
      }
    };
    afterNextRender(() => {
      this.document.addEventListener('pointermove', move);
      this.document.addEventListener('pointerup', up);
      this.document.addEventListener('pointercancel', up);
      this.document.addEventListener('keydown', keys);
    });
    inject(DestroyRef).onDestroy(() => {
      this.document.removeEventListener('pointermove', move);
      this.document.removeEventListener('pointerup', up);
      this.document.removeEventListener('pointercancel', up);
      this.document.removeEventListener('keydown', keys);
      if (this.frame !== null) {
        cancelAnimationFrame(this.frame);
      }
    });
  }

  nameOf(id: string): string {
    return this.store.nameOf(id);
  }

  select(id: string): void {
    this.store.selected.set(id);
  }

  startMove(id: string, event: PointerEvent): void {
    this.startDrag(id, 'move', event);
  }

  startResize(id: string, event: PointerEvent): void {
    this.startDrag(id, 'resize', event);
  }

  key(id: string, event: KeyboardEvent): void {
    if (!this.movable()) {
      return;
    }
    const moving = this.moving() === id;
    const step: Record<string, [number, number]> = {
      ArrowLeft: [-1, 0],
      ArrowRight: [1, 0],
      ArrowUp: [0, -1],
      ArrowDown: [0, 1],
    };
    if (!moving) {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        this.select(id);
        this.moving.set(id);
        this.preview.set(this.cells());
        this.announcer.announce(
          `Moving ${this.nameOf(id)}: ${placementText(this.cells()[id])}. Arrows move it, Shift and arrows resize it, Enter drops it, Escape puts it back.`,
        );
      }
      return;
    }
    const preview = this.preview() ?? this.cells();
    const cell = preview[id];
    if (event.key in step) {
      event.preventDefault();
      const [dx, dy] = step[event.key];
      const columns = this.store.breakpoint().columns;
      const next = event.shiftKey
        ? resizeTo(preview, id, { w: cell.w + dx, h: cell.h + dy }, columns, this.mins()[id])
        : moveTo(preview, id, { x: cell.x + dx, y: cell.y + dy }, columns);
      this.preview.set(next);
      this.announcer.announce(`${this.nameOf(id)}: ${placementText(next[id])}`);
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.commit(id, preview, `Moved ${this.nameOf(id)}`, `key:${++drags}`);
      this.announcer.announce(`Dropped ${this.nameOf(id)}: ${placementText(preview[id])}`);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      this.moving.set(null);
      this.preview.set(null);
      this.announcer.announce(`Put ${this.nameOf(id)} back: ${placementText(this.cells()[id])}`);
    } else if (event.key === 'Tab') {
      // Leaving the handle drops the widget where it is.
      this.commit(id, preview, `Moved ${this.nameOf(id)}`, `key:${++drags}`);
    }
  }

  private startDrag(id: string, kind: Drag['kind'], event: PointerEvent): void {
    if (!this.movable() || event.button !== 0) {
      return;
    }
    event.preventDefault();
    const target = event.currentTarget as HTMLElement;
    target.setPointerCapture?.(event.pointerId);
    this.select(id);
    const before = this.cells();
    const at = this.cellAt(event);
    const cell = before[id];
    this.drag = {
      id,
      kind,
      before,
      grab: at && cell ? { x: at.x - cell.x, y: at.y - cell.y } : { x: 0, y: 0 },
      pointer: event.pointerId,
      target,
    };
    this.preview.set(before);
  }

  private pointerMoved(event: PointerEvent): void {
    if (!this.drag || event.pointerId !== this.drag.pointer) {
      return;
    }
    this.pending = event;
    // Once a frame, however often the pointer moves.
    this.frame ??= requestAnimationFrame(() => {
      this.frame = null;
      const pending = this.pending;
      if (pending) {
        this.dragTo(pending);
      }
    });
  }

  private dragTo(event: PointerEvent): void {
    const drag = this.drag;
    const at = this.cellAt(event);
    if (!drag || !at) {
      return;
    }
    const cell = drag.before[drag.id];
    const columns = this.store.breakpoint().columns;
    this.preview.set(
      drag.kind === 'move'
        ? moveTo(drag.before, drag.id, { x: at.x - drag.grab.x, y: at.y - drag.grab.y }, columns)
        : resizeTo(
            drag.before,
            drag.id,
            { w: at.x - cell.x + 1, h: at.y - cell.y + 1 },
            columns,
            this.mins()[drag.id],
          ),
    );
  }

  private pointerUp(event: PointerEvent): void {
    const drag = this.drag;
    if (!drag || event.pointerId !== drag.pointer) {
      return;
    }
    if (event.type === 'pointerup') {
      this.dragTo(event);
    }
    this.drag = null;
    this.pending = null;
    drag.target.releasePointerCapture?.(drag.pointer);
    const preview = untracked(this.preview);
    if (event.type !== 'pointerup' || !preview) {
      this.preview.set(null);
      return;
    }
    const verb = drag.kind === 'move' ? 'Moved' : 'Resized';
    this.commit(drag.id, preview, `${verb} ${this.nameOf(drag.id)}`, `${drag.kind}:${++drags}`);
  }

  private cancelDrag(): void {
    const drag = this.drag;
    this.drag = null;
    this.pending = null;
    drag?.target.releasePointerCapture?.(drag.pointer);
    this.preview.set(null);
  }

  /** The cells dropped are the layout's: one step of the history. The keyboard stays on the widget's handle. */
  private commit(id: string, cells: Cells, label: string, key: string): void {
    const breakpoint = this.store.breakpoint().id;
    this.store.apply(label, (definition) => withCells(definition, breakpoint, cells), key);
    this.moving.set(null);
    this.preview.set(null);
    const focused = this.document.activeElement as HTMLElement | null;
    if (focused?.getAttribute('data-gd-move') === id) {
      // The widget's place in the page changed with its cell: the handle may have moved with it.
      requestAnimationFrame(() =>
        (this.element.nativeElement as HTMLElement)
          .querySelector<HTMLElement>(`[data-gd-move="${id}"]`)
          ?.focus(),
      );
    }
  }

  /** The cell under the pointer, in the grid's columns and rows; null off it. */
  private cellAt(event: PointerEvent): { x: number; y: number } | null {
    const grid = (this.element.nativeElement as HTMLElement).querySelector('gd-dashboard-grid');
    if (!grid) {
      return null;
    }
    const box = grid.getBoundingClientRect();
    const { rowHeight, gap } = this.store.draft().layout;
    const columns = this.store.breakpoint().columns;
    const width = (box.width - gap * (columns - 1)) / columns;
    return {
      x: Math.max(
        0,
        Math.min(columns - 1, Math.floor((event.clientX - box.left + gap / 2) / (width + gap))),
      ),
      y: Math.max(0, Math.floor((event.clientY - box.top + gap / 2) / (rowHeight + gap))),
    };
  }
}
