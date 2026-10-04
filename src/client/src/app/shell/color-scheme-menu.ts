import { Component, computed, inject } from '@angular/core';
import { MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatMenu, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { MatTooltip } from '@angular/material/tooltip';
import { ColorScheme, type ColorSchemeChoice } from '../core/theme/color-scheme';

interface SchemeOption {
  readonly choice: ColorSchemeChoice;
  readonly label: string;
  readonly icon: string;
}

/** Choosing the color scheme: the system's, light or dark. */
@Component({
  selector: 'gd-color-scheme-menu',
  imports: [MatIconButton, MatIcon, MatMenu, MatMenuItem, MatMenuTrigger, MatTooltip],
  template: `
    <button
      matIconButton
      [matMenuTriggerFor]="menu"
      aria-label="Color scheme"
      matTooltip="Color scheme: {{ scheme().label }}"
    >
      <mat-icon>{{ scheme().icon }}</mat-icon>
    </button>
    <mat-menu #menu="matMenu">
      @for (option of schemes; track option.choice) {
        <button
          mat-menu-item
          role="menuitemradio"
          [attr.aria-checked]="option.choice === colorScheme.choice()"
          (click)="colorScheme.choose(option.choice)"
        >
          <mat-icon>{{ option.icon }}</mat-icon>
          <span>{{ option.label }}</span>
        </button>
      }
    </mat-menu>
  `,
})
export class ColorSchemeMenu {
  protected readonly colorScheme = inject(ColorScheme);

  protected readonly schemes: readonly SchemeOption[] = [
    { choice: 'system', label: 'System', icon: 'brightness_auto' },
    { choice: 'light', label: 'Light', icon: 'light_mode' },
    { choice: 'dark', label: 'Dark', icon: 'dark_mode' },
  ];

  protected readonly scheme = computed(
    () =>
      this.schemes.find((option) => option.choice === this.colorScheme.choice()) ?? this.schemes[0],
  );
}
