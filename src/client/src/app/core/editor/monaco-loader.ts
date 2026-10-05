import { DOCUMENT } from '@angular/common';
import { Injectable, InjectionToken, Injector, effect, inject } from '@angular/core';
import { ColorScheme } from '../theme/color-scheme';
import { registerGdq } from './gdq-language';

/** Monaco's API, as the application loads it. */
export type Monaco = typeof import('./monaco-modules');

/** Imports Monaco's chunk (another in tests). */
export const MONACO_IMPORT = new InjectionToken<() => Promise<Monaco>>('MONACO_IMPORT', {
  providedIn: 'root',
  factory: () => () => import('./monaco-modules'),
});

/**
 * Loads Monaco once, when an editor is first shown: its chunk and its stylesheet (`monaco.css`, a bundle of its own
 * that pages don't load), with the query language registered (`gdq`). Its editors' theme follows the page's colour
 * scheme. A load that fails may be tried again.
 */
@Injectable({ providedIn: 'root' })
export class MonacoLoader {
  private readonly document = inject(DOCUMENT);
  private readonly scheme = inject(ColorScheme);
  private readonly injector = inject(Injector);
  private readonly import = inject(MONACO_IMPORT);
  private loading: Promise<Monaco> | null = null;

  load(): Promise<Monaco> {
    this.loading ??= this.start().catch((error: unknown) => {
      this.loading = null;
      throw error;
    });
    return this.loading;
  }

  private async start(): Promise<Monaco> {
    const [monaco] = await Promise.all([this.import(), this.stylesheet()]);
    registerGdq(monaco);
    effect(() => monaco.editor.setTheme(this.scheme.dark() ? 'vs-dark' : 'vs'), {
      injector: this.injector,
    });
    // The page's code font, once loaded, measured anew (the editor measures its font as it is made).
    void this.document.fonts?.ready.then(() => monaco.editor.remeasureFonts());
    return monaco;
  }

  private stylesheet(): Promise<void> {
    const href = new URL('monaco.css', this.document.baseURI).href;
    const head = this.document.head;
    if (
      [...head.querySelectorAll<HTMLLinkElement>('link[rel=stylesheet]')].some(
        (link) => link.href === href,
      )
    ) {
      return Promise.resolve();
    }
    return new Promise((resolve, reject) => {
      const link = this.document.createElement('link');
      link.rel = 'stylesheet';
      link.href = href;
      link.onload = () => resolve();
      link.onerror = () => {
        link.remove();
        reject(new Error("The editor's styles couldn't be loaded"));
      };
      head.append(link);
    });
  }
}
