import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, computed, inject, linkedSignal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButton, MatIconButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import {
  MatListItem,
  MatListItemIcon,
  MatListItemTitle,
  MatListSubheaderCssMatStyler,
  MatNavList,
} from '@angular/material/list';
import { MatSidenav, MatSidenavContainer, MatSidenavContent } from '@angular/material/sidenav';
import { MatToolbar } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { AuthStore } from '../core/auth/auth-store';
import { ColorSchemeMenu } from './color-scheme-menu';
import { navItemsFor } from './nav-items';
import { UserMenu } from './user-menu';

/** Screens this wide keep the navigation beside the page; narrower ones open it over the page. */
export const wideScreen = '(min-width: 960px)';

/** The application around its pages: the bar at the top (with a way past it), the navigation, and the page. */
@Component({
  selector: 'gd-shell',
  imports: [
    ColorSchemeMenu,
    MatButton,
    MatIcon,
    MatIconButton,
    MatListItem,
    MatListItemIcon,
    MatListItemTitle,
    MatListSubheaderCssMatStyler,
    MatNavList,
    MatSidenav,
    MatSidenavContainer,
    MatSidenavContent,
    MatToolbar,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    UserMenu,
  ],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  private readonly auth = inject(AuthStore);

  protected readonly sections = computed(() => navItemsFor(this.auth.permissions()));
  protected readonly wide = toSignal(
    inject(BreakpointObserver)
      .observe(wideScreen)
      .pipe(map((state) => state.matches)),
    { initialValue: true },
  );
  /** Whether the navigation is open: beside the page at first, over it only when asked for. */
  protected readonly navOpen = linkedSignal(() => this.wide());

  /** A page chosen: the navigation over the page closes. */
  protected chosen(): void {
    if (!this.wide()) {
      this.navOpen.set(false);
    }
  }
}
