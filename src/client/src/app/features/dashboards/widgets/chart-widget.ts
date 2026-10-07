import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  LOCALE_ID,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatIcon } from '@angular/material/icon';
import { MatMenu, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { elementSize } from '../../../core/browser/element-size';
import { type Chart, EChartsLoader } from '../../../core/charts/echarts-loader';
import { ColorScheme } from '../../../core/theme/color-scheme';
import { actionOf, onMac } from '../charts/chart-events';
import { ColorMemory, chartOption } from '../charts/chart-options';
import { type ChartTheme, fallbackTheme, readTheme } from '../charts/chart-theme';
import { type Key, isChart } from '../model/definition';
import { type SelectionAction, WidgetContext } from '../model/widget-context';
import { RowsTable } from './rows-table';

/**
 * A pie, bar or line chart of a widget's rows (ECharts, on a canvas, loaded with the first chart shown). Clicking a
 * slice chooses it (Ctrl or ⌘ adds it, Alt leaves it out); the chart says what it shows to screen readers, and its
 * rows are a table to read and choose from by keyboard ("Show as table", which is also what is shown if the chart
 * can't be loaded). Right-clicking a slice (or pressing on it long) offers the same as a menu: for touch, and where
 * Alt+click is the window manager's.
 */
@Component({
  selector: 'gd-chart-widget',
  imports: [MatIcon, MatMenu, MatMenuItem, MatMenuTrigger, RowsTable],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (asTable() && data(); as data) {
      <gd-rows-table
        [data]="data"
        [config]="dataConfig()!"
        [label]="context.widget().title ?? 'Rows'"
        [selection]="context.selection()"
        [choosing]="context.choosing() && emits()"
        [choose]="choose"
      />
    }
    @if (failed()) {
      <p class="aside" role="status">The chart couldn't be loaded: its rows are shown instead.</p>
    }
    <div #canvas class="canvas" [hidden]="asTable()"></div>
    @if (!asTable() && data()?.rows?.length === 0) {
      <p class="empty" role="status">No rows</p>
    }
    <span
      class="anchor"
      aria-hidden="true"
      [style.left.px]="menuAt().x"
      [style.top.px]="menuAt().y"
      [matMenuTriggerFor]="slice"
    ></span>
    <mat-menu #slice="matMenu">
      <button mat-menu-item type="button" (click)="chooseFromMenu('replace')">
        <mat-icon>filter_alt</mat-icon>
        Choose only this
      </button>
      <button mat-menu-item type="button" (click)="chooseFromMenu('add')">
        <mat-icon>add</mat-icon>
        Add to those chosen
      </button>
      <button mat-menu-item type="button" (click)="chooseFromMenu('exclude')">
        <mat-icon>block</mat-icon>
        Leave out
      </button>
      @if (context.selection()) {
        <button mat-menu-item type="button" (click)="chooseFromMenu('clear')">
          <mat-icon>filter_alt_off</mat-icon>
          Clear what is chosen
        </button>
      }
    </mat-menu>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
      position: relative;
    }
    .empty {
      position: absolute;
      inset: 0;
      margin: 0;
      display: grid;
      place-items: center;
      font: var(--mat-sys-body-medium);
      color: var(--mat-sys-on-surface-variant);
      pointer-events: none;
    }
    .anchor {
      position: absolute;
      width: 0;
      height: 0;
    }
    .canvas {
      flex: 1 1 auto;
      min-height: 0;
      width: 100%;
    }
    .canvas[hidden] {
      display: none;
    }
    gd-rows-table {
      flex: 1 1 auto;
      overflow: auto;
    }
    .aside {
      margin: 0 0 4px;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class ChartWidget {
  protected readonly context = inject(WidgetContext);
  private readonly loader = inject(EChartsLoader);
  private readonly scheme = inject(ColorScheme);
  private readonly locale = inject(LOCALE_ID);
  private readonly document = inject(DOCUMENT);
  private readonly canvas = viewChild.required<ElementRef<HTMLElement>>('canvas');
  private readonly menu = viewChild.required(MatMenuTrigger);
  /** Where the slice's menu opens (in the widget), and the slice it is of. */
  protected readonly menuAt = signal({ x: 0, y: 0 });
  private menuKey: Key | null = null;
  private readonly element = inject(ElementRef<HTMLElement>);
  private readonly size = elementSize(this.element.nativeElement);
  /** A phone's width or less: changes only as the width crosses it, so resizing draws nothing new until then. */
  private readonly narrow = computed(() => (this.size()?.width ?? Infinity) < 420);
  private readonly chart = signal<Chart | null>(null);
  private readonly theme = signal<ChartTheme>(fallbackTheme(false));
  private readonly colors = new ColorMemory();
  private readonly mac = onMac(this.document.defaultView?.navigator);
  private readonly reducedMotion =
    this.document.defaultView?.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  protected readonly failed = signal(false);

  protected readonly data = this.context.data;
  protected readonly dataConfig = computed(() => {
    const config = this.context.config();
    return isChart(config) ? config : null;
  });
  protected readonly emits = computed(() => this.dataConfig()?.emits ?? false);
  protected readonly asTable = computed(() => this.context.asTable() || this.failed());

  protected readonly choose = (key: Key, action: Parameters<WidgetContext['choose']>[1]) =>
    this.context.choose(key, action);

  protected chooseFromMenu(action: SelectionAction): void {
    if (this.menuKey) {
      this.context.choose(this.menuKey, action);
    }
  }

  constructor() {
    const destroy = inject(DestroyRef);
    afterNextRender(() => {
      this.loader.load().then(
        (charts) => {
          if (destroy.destroyed) {
            return;
          }
          const chart = charts.init(this.canvas().nativeElement);
          chart.on('click', (params) => {
            const key = (params.data as { key?: Key | null } | undefined)?.key;
            if (key && this.context.choosing() && this.emits()) {
              this.context.choose(key, actionOf(params.event?.event, this.mac));
            }
          });
          chart.on('contextmenu', (params) => {
            const key = (params.data as { key?: Key | null } | undefined)?.key;
            const event = params.event?.event;
            if (!key || !event || !this.context.choosing() || !this.emits()) {
              return;
            }
            event.preventDefault();
            const box = (this.element.nativeElement as HTMLElement).getBoundingClientRect();
            this.menuKey = key;
            this.menuAt.set({ x: event.clientX - box.left, y: event.clientY - box.top });
            // Placed first, then opened there.
            requestAnimationFrame(() => this.menu().openMenu());
          });
          this.chart.set(chart);
        },
        () => this.failed.set(true),
      );
    });
    destroy.onDestroy(() => this.chart()?.dispose());
    // The theme, read again as the scheme changes (and once the page's fonts are in).
    effect(() => {
      const dark = this.scheme.dark();
      this.theme.set(readTheme(this.element.nativeElement, dark));
    });
    void this.document.fonts?.ready.then(() => {
      if (!destroy.destroyed) {
        this.theme.set(readTheme(this.element.nativeElement, this.scheme.dark()));
      }
    });
    effect(() => {
      const chart = this.chart();
      const config = this.dataConfig();
      const data = this.data();
      if (!chart || !config || !data) {
        return;
      }
      chart.setOption(
        chartOption(config, data, {
          theme: this.theme(),
          locale: this.locale,
          selection: this.context.selection(),
          reducedMotion: this.reducedMotion,
          colors: this.colors,
          narrow: this.narrow(),
        }),
        { notMerge: true },
      );
    });
    // As the widget's size changes (the page's width, the editor's resizing), the chart fits it again.
    effect(() => {
      const size = this.size();
      const chart = this.chart();
      this.asTable();
      if (chart && size) {
        requestAnimationFrame(() => chart.resize());
      }
    });
  }
}
