import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { WidgetContext } from '../model/widget-context';
import { markdownHtml } from './markdown';

/** A text widget: its Markdown, as headings, notes, lists and links. */
@Component({
  selector: 'gd-text-widget',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (html(); as html) {
      <div class="markdown" [innerHTML]="html"></div>
    } @else if (context.editing()) {
      <p class="empty">Write the text in the widget's settings.</p>
    }
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
      overflow: auto;
    }
    .markdown {
      font: var(--mat-sys-body-medium);
      ::ng-deep {
        h2 {
          font: var(--mat-sys-headline-small);
          margin: 0 0 8px;
        }
        h3 {
          font: var(--mat-sys-title-large);
          margin: 0 0 8px;
        }
        h4,
        h5,
        h6 {
          font: var(--mat-sys-title-medium);
          margin: 0 0 6px;
        }
        p {
          margin: 0 0 8px;
        }
        a {
          color: var(--mat-sys-primary);
        }
        img {
          max-width: 100%;
        }
        code {
          font-family: var(--gd-code-font-family);
        }
      }
    }
    .empty {
      margin: 0;
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class TextWidget {
  protected readonly context = inject(WidgetContext);

  protected readonly html = computed(() => {
    const config = this.context.config();
    const text = config.kind === 'text' ? config.markdown.trim() : '';
    return text ? markdownHtml(text) : null;
  });
}
