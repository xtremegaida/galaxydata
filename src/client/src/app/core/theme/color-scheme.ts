import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';

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

  /** The scheme chosen. */
  readonly choice = this.chosen.asReadonly();

  /**
   * Whether the application is dark, the system's scheme resolved: for what is themed apart from Material (the
   * grid, the editor).
   */
  readonly dark = computed(() => {
    const choice = this.chosen();
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
    try {
      this.storage?.setItem(ColorScheme.storageKey, choice);
    } catch {
      // Storage full or refused: the choice holds until the page is left.
    }
    this.apply();
  }

  private apply(): void {
    const choice = this.chosen();
    this.document.documentElement.style.colorScheme = choice === 'system' ? 'light dark' : choice;
  }

  private stored(): ColorSchemeChoice {
    let value: string | null = null;
    try {
      value = this.storage?.getItem(ColorScheme.storageKey) ?? null;
    } catch {
      // Storage refused: the system's.
    }
    return choices.find((choice) => choice === value) ?? 'system';
  }
}

function storageOf(document: Document): Storage | undefined {
  try {
    return document.defaultView?.localStorage;
  } catch {
    return undefined;
  }
}
