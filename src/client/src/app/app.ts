import { Component, computed, inject } from '@angular/core';
import { MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatMenu, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { MatToolbar } from '@angular/material/toolbar';
import { MatTooltip } from '@angular/material/tooltip';
import { RouterOutlet } from '@angular/router';
import { ColorScheme, type ColorSchemeChoice } from './core/theme/color-scheme';

interface SchemeOption {
  readonly choice: ColorSchemeChoice;
  readonly label: string;
  readonly icon: string;
}

@Component({
  selector: 'gd-root',
  imports: [
    RouterOutlet,
    MatToolbar,
    MatIconButton,
    MatIcon,
    MatMenu,
    MatMenuItem,
    MatMenuTrigger,
    MatTooltip,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
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
