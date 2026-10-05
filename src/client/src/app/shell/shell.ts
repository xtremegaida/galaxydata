import { BreakpointObserver } from '@angular/cdk/layout';
import {
  Component,
  ElementRef,
  computed,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatBadge } from '@angular/material/badge';
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
import { MatTooltip } from '@angular/material/tooltip';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { AuthStore } from '../core/auth/auth-store';
import { PendingChanges } from '../core/changes/pending-changes';
import { ChangesDrawer } from '../features/changes/changes-drawer';
import { ColorSchemeMenu } from './color-scheme-menu';
import { navItemsFor } from './nav-items';
import { UserMenu } from './user-menu';

/** Screens this wide keep the navigation beside the page; narrower ones open it over the page. */
export const wideScreen = '(min-width: 960px)';

/**
 * The application around its pages: the bar at the top (with a way past it), the navigation, the page, and for those
 * who change data, their pending changes (a drawer, opened from the bar, which counts them).
 */
@Component({
  selector: 'gd-shell',
  imports: [
    ChangesDrawer,
    ColorSchemeMenu,
    MatBadge,
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
    MatTooltip,
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
  private readonly changes = inject(PendingChanges);
  private readonly changesButton = viewChild<unknown, ElementRef<HTMLElement>>('changesButton', {
    read: ElementRef,
  });

  protected readonly sections = computed(() => navItemsFor(this.auth.permissions()));
  protected readonly wide = toSignal(
    inject(BreakpointObserver)
      .observe(wideScreen)
      .pipe(map((state) => state.matches)),
    { initialValue: true },
  );
  /** Whether the navigation is open: beside the page at first, over it only when asked for. */
  protected readonly navOpen = linkedSignal(() => this.wide());
  /** Whether the user has pending changes (changes data), and the drawer that lists them is open. */
  protected readonly changesEnabled = this.changes.enabled;
  protected readonly changesOpen = signal(false);
  protected readonly changesCount = this.changes.count;
  protected readonly changesLabel = computed(() => {
    const count = this.changesCount();
    return count === 0
      ? 'Pending changes: none'
      : `Pending changes: ${count} ${count === 1 ? 'row' : 'rows'}`;
  });

  /**
   * The drawer of changes closes: the keyboard, when it was in it (or lost as it closes), goes back to its
   * button.
   */
  protected closeChanges(): void {
    if (!this.changesOpen()) {
      return;
    }
    this.changesOpen.set(false);
    const button = this.changesButton()?.nativeElement;
    const active = button?.ownerDocument.activeElement;
    if (
      button &&
      (!active || active === button.ownerDocument.body || active.closest('#gd-changes') !== null)
    ) {
      button.focus();
    }
  }

  /** A page chosen: the navigation over the page closes. */
  protected chosen(): void {
    if (!this.wide()) {
      this.navOpen.set(false);
    }
  }
}
