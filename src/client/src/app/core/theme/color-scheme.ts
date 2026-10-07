import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { readStored, storageOf, writeStored } from '../browser/stored';

/** The color scheme chosen: the system's (light or dark, as it is set), light, or dark. */
export type ColorSchemeChoice = 'system' | 'light' | 'dark';

const choices: readonly ColorSchemeChoice[] = ['system', 'light', 'dark'];

/**
 * The color scheme the application is shown in. The theme follows the page's `color-scheme` (Material's system
 * colors are `light-dark()` values), so a choice sets it; the system's follows the browser's setting as it
 * changes. The choice is kept in the browser.
 */
@Injectable({ providedIn: 'root' })
export class ColorScheme {
  static readonly storageKey = 'gd.colorScheme';

  private readonly document = inject(DOCUMENT);
  private readonly storage = storageOf(this.document);
  private readonly systemQuery = this.document.defaultView?.matchMedia?.(
    '(prefers-color-scheme: dark)',
  );
  private readonly systemDark = signal(this.systemQuery?.matches ?? false);
  private readonly chosen = signal<ColorSchemeChoice>(this.stored());
  private readonly forced = signal<ColorSchemeChoice | null>(null);

  /** The scheme chosen. */
  readonly choice = this.chosen.asReadonly();

  /**
   * Whether the application is dark, the system's scheme resolved: for what is themed apart from Material (the
   * grid, the editor, charts).
   */
  readonly dark = computed(() => {
    const choice = this.forced() ?? this.chosen();
    return choice === 'dark' || (choice === 'system' && this.systemDark());
  });

  constructor() {
    const changed = (event: MediaQueryListEvent) => this.systemDark.set(event.matches);
    this.systemQuery?.addEventListener('change', changed);
    inject(DestroyRef).onDestroy(() => this.systemQuery?.removeEventListener('change', changed));
    this.apply();
  }

  /** Shows the application in a scheme, and keeps the choice. */
  choose(choice: ColorSchemeChoice): void {
    this.chosen.set(choice);
    writeStored(this.storage, ColorScheme.storageKey, choice);
    this.apply();
  }

  /**
   * Shows the application in a scheme for as long as asked, keeping no choice (an embedded dashboard's
   * `?theme=`); null gives the chosen one back.
   */
  override(choice: ColorSchemeChoice | null): void {
    this.forced.set(choice);
    this.apply();
  }

  private apply(): void {
    const choice = this.forced() ?? this.chosen();
    this.document.documentElement.style.colorScheme = choice === 'system' ? 'light dark' : choice;
  }

  private stored(): ColorSchemeChoice {
    const value = readStored(this.storage, ColorScheme.storageKey);
    return choices.find((choice) => choice === value) ?? 'system';
  }
}

/**
 * An embedded dashboard's color scheme (`/embed/…`): its address's (`?theme=light`, `dark`), else the system's;
 * the application's choice (kept in storage) is nothing to it, and nothing is kept.
 */
export function embedScheme(
  location: Pick<Location, 'pathname' | 'search'>,
  scheme: ColorScheme,
): void {
  if (location.pathname.startsWith('/embed/')) {
    const theme = new URLSearchParams(location.search).get('theme');
    scheme.override(theme === 'light' || theme === 'dark' ? theme : 'system');
  }
}
