import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  viewChild,
} from '@angular/core';
import { elementSize } from '../../../core/browser/element-size';
import { WidgetContext } from '../model/widget-context';

/**
 * How many lines of `lineHeight` pixels fit in `height`: at least one (clipped, if not even that fits); null when
 * either isn't known (not laid out yet, or no layout at all), and then nothing is clamped.
 */
export function linesThatFit(height: number | null, lineHeight: number): number | null {
  if (height == null || !(height > 0) || !(lineHeight > 0)) {
    return null;
  }
  // A pixel's leeway: heights laid out as fractions shouldn't lose a line that fits.
  return Math.max(1, Math.floor((height + 1) / lineHeight));
}

/**
 * A heading: its text at its size (large, medium and small are the page's second, third and fourth levels, as the
 * page's hidden first is the dashboard's name), aligned as chosen, centred in the widget's height. It wraps onto as
 * many lines as fit and ends with an ellipsis when it has more: it never scrolls.
 */
@Component({
  selector: 'gd-heading-widget',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[style.text-align]': 'align()' },
  template: `
    @if (text(); as text) {
      @switch (size()) {
        @case ('large') {
          <h2 #heading class="heading large">{{ text }}</h2>
        }
        @case ('medium') {
          <h3 #heading class="heading medium">{{ text }}</h3>
        }
        @default {
          <h4 #heading class="heading small">{{ text }}</h4>
        }
      }
    } @else if (context.editing()) {
      <p class="empty">Write the heading in the widget's settings.</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      justify-content: safe center;
      height: 100%;
      overflow: hidden;
    }
    .heading {
      margin: 0;
      display: -webkit-box;
      -webkit-box-orient: vertical;
      -webkit-line-clamp: var(--gd-lines, none);
      overflow: hidden;
      overflow-wrap: anywhere;
    }
    .large {
      font: var(--mat-sys-headline-medium);
    }
    .medium {
      font: var(--mat-sys-headline-small);
    }
    .small {
      font: var(--mat-sys-title-large);
    }
    .empty {
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class HeadingWidget {
  protected readonly context = inject(WidgetContext);
  private readonly height = elementSize(inject(ElementRef<HTMLElement>).nativeElement);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('heading');

  private readonly config = computed(() => {
    const config = this.context.config();
    return config.kind === 'heading' ? config : null;
  });
  protected readonly text = computed(() => this.config()?.text.trim() ?? '');
  protected readonly size = computed(() => this.config()?.size ?? 'large');
  protected readonly align = computed(() => this.config()?.align ?? 'start');

  constructor() {
    // The lines that fit, by the heading's line height (its size's), measured once it is rendered: again as its
    // element (a size's level) or its height changes.
    afterRenderEffect(() => {
      const heading = this.heading()?.nativeElement;
      const height = this.height()?.height ?? null;
      if (!heading) {
        return;
      }
      const style = heading.ownerDocument.defaultView?.getComputedStyle(heading);
      const lines = linesThatFit(height, Number.parseFloat(style?.lineHeight ?? ''));
      if (lines == null) {
        heading.style.removeProperty('--gd-lines');
      } else {
        heading.style.setProperty('--gd-lines', String(lines));
      }
    });
  }
}
