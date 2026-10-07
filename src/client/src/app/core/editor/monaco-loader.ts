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

/** How long Monaco waits for the page's code font to load, at most (in milliseconds). */
export const CODE_FONT_WAIT = new InjectionToken<number>('CODE_FONT_WAIT', {
  providedIn: 'root',
  factory: () => 3000,
});

/** The page's code font, as CSS names it (`--gd-code-font-family`); empty when the page doesn't. */
export function codeFontFamily(document: Document): string {
  // The page's (an editor in a tab not shown is made outside the page, where styles don't reach).
  return getComputedStyle(document.documentElement)
    .getPropertyValue('--gd-code-font-family')
    .trim();
}

/**
 * Loads Monaco once, when an editor is first shown: its chunk, its stylesheet (`monaco.css`, a bundle of its own
 * that pages don't load) and the page's code font, with the query language registered (`gdq`). Its editors' theme
 * follows the page's colour scheme. A load that fails may be tried again.
 *
 * Monaco measures a font's characters once, as the first editor with it is made, and places the cursor and
 * selections by those widths. A font not loaded yet is measured as the one the browser falls back on, and the text is
 * drawn in it once it loads, so the cursor drifts from the text. So `load` resolves once the code font is loaded (or
 * after `CODE_FONT_WAIT`), and fonts are measured anew whenever more load (the code font's parts for other scripts,
 * or the code font itself after the wait).
 */
@Injectable({ providedIn: 'root' })
export class MonacoLoader {
  private readonly document = inject(DOCUMENT);
  private readonly scheme = inject(ColorScheme);
  private readonly injector = inject(Injector);
  private readonly import = inject(MONACO_IMPORT);
  private readonly fontWait = inject(CODE_FONT_WAIT);
  private loading: Promise<Monaco> | null = null;

  load(): Promise<Monaco> {
    this.loading ??= this.start().catch((error: unknown) => {
      this.loading = null;
      throw error;
    });
    return this.loading;
  }

  private async start(): Promise<Monaco> {
    const [monaco] = await Promise.all([this.import(), this.stylesheet(), this.codeFont()]);
    registerGdq(monaco);
    effect(() => monaco.editor.setTheme(this.scheme.dark() ? 'vs-dark' : 'vs'), {
      injector: this.injector,
    });
    // Each time fonts load (`fonts.ready` would be once, and at once when nothing is loading yet).
    this.document.fonts?.addEventListener('loadingdone', () => monaco.editor.remeasureFonts());
    return monaco;
  }

  /** The page's code font loaded (its part for Latin text), or the wait for it over. */
  private async codeFont(): Promise<void> {
    const fonts = this.document.fonts;
    const family = codeFontFamily(this.document);
    if (!fonts || !family) {
      return;
    }
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await Promise.race([
        fonts.load(`13px ${family}`),
        new Promise<void>((resolve) => (timer = setTimeout(resolve, this.fontWait))),
      ]);
    } catch {
      // A font that can't be loaded: the editor draws with the next, and measures it.
    } finally {
      clearTimeout(timer);
    }
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
