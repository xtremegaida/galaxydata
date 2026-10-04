import { BreakpointObserver } from '@angular/cdk/layout';
import { DOCUMENT } from '@angular/common';
import {
  Component,
  Injector,
  afterNextRender,
  effect,
  inject,
  linkedSignal,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatSidenav, MatSidenavContainer, MatSidenavContent } from '@angular/material/sidenav';
import { ActivatedRoute, NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { readStored, storageOf, writeStored } from '../../core/browser/stored';
import { CatalogTreeStore } from '../../core/catalog/catalog-tree-store';
import { CatalogPanel } from './catalog-panel';

/** Screens this wide keep the catalog beside the page; narrower ones open it over the page. */
export const catalogBeside = '(min-width: 1024px)';

/** How wide the catalog is beside the page, in pixels: at first, and at least and most. */
export const catalogWidths = { initial: 320, min: 200, max: 640, step: 16 } as const;

/**
 * Browsing: the catalog (its search and tree) beside the page of what is chosen in it, the two parted by a splitter
 * that sets the catalog's width (kept for the next time). On narrower screens the catalog opens over the page: at
 * first, and when asked for.
 */
@Component({
  selector: 'gd-browse',
  imports: [
    CatalogPanel,
    MatButton,
    MatIcon,
    MatSidenav,
    MatSidenavContainer,
    MatSidenavContent,
    RouterOutlet,
  ],
  template: `
    <mat-sidenav-container class="container">
      <mat-sidenav
        id="gd-catalog"
        class="catalog"
        [class.over]="!wide()"
        [mode]="wide() ? 'side' : 'over'"
        [opened]="wide() || catalogOpen()"
        [disableClose]="wide()"
        [style.width.px]="wide() ? width() : null"
        (openedChange)="catalogOpen.set($event)"
      >
        <gd-catalog-panel [keepFocus]="wide()" (chosen)="chosen()" />
      </mat-sidenav>
      <mat-sidenav-content class="content">
        @if (wide()) {
          <div
            class="splitter"
            role="separator"
            aria-orientation="vertical"
            aria-label="The catalog's width"
            aria-controls="gd-catalog"
            tabindex="0"
            [class.dragging]="dragging()"
            [attr.aria-valuenow]="width()"
            [attr.aria-valuemin]="widths.min"
            [attr.aria-valuemax]="widths.max"
            [attr.aria-valuetext]="width() + ' pixels'"
            (keydown)="resizeWithKeys($event)"
            (pointerdown)="startDrag($event)"
            (pointermove)="drag($event)"
            (pointerup)="endDrag()"
            (pointercancel)="endDrag()"
          ></div>
        }
        <div class="page">
          @if (!wide()) {
            <div class="catalog-bar">
              <button
                matButton
                type="button"
                aria-controls="gd-catalog"
                [attr.aria-expanded]="catalogOpen()"
                (click)="catalogOpen.set(true)"
              >
                <mat-icon>account_tree</mat-icon>
                Catalog
              </button>
            </div>
          }
          <router-outlet />
        </div>
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }

    .container {
      height: 100%;
    }

    .catalog {
      border-right: 1px solid var(--mat-sys-outline-variant);
      border-radius: 0;

      &.over {
        width: min(360px, 88vw);
      }
    }

    .content {
      display: flex;
      overflow: hidden;
    }

    .splitter {
      flex: none;
      width: 6px;
      margin-left: -3px;
      cursor: col-resize;
      touch-action: none;
      outline: none;

      &:hover,
      &:focus-visible,
      &.dragging {
        background: var(--mat-sys-primary);
      }
    }

    .page {
      flex: 1;
      min-width: 0;
      overflow: auto;
    }

    .catalog-bar {
      padding: 8px 16px 0;
    }
  `,
})
export class BrowseLayout {
  private readonly store = inject(CatalogTreeStore);
  private readonly storage = storageOf(inject(DOCUMENT));
  private readonly container = viewChild.required(MatSidenavContainer);
  private dragStart: { x: number; width: number } | null = null;

  static readonly storageKey = 'gd.catalogWidth';

  protected readonly widths = catalogWidths;
  protected readonly wide = toSignal(
    inject(BreakpointObserver)
      .observe(catalogBeside)
      .pipe(map((state) => state.matches)),
    { initialValue: true },
  );
  protected readonly width = signal(this.storedWidth());
  protected readonly dragging = signal(false);
  /** The entity whose page is open, as the address says. */
  protected readonly entity = entityShown(inject(Router), inject(ActivatedRoute));
  /** Whether the catalog is open over the page (narrow screens): at first on the start of browsing. */
  protected readonly catalogOpen = linkedSignal(() => !this.wide() && this.entity() === null);

  constructor() {
    effect(() => {
      const entity = this.entity();
      untracked(() => void this.store.select(entity));
    });
    // The page beside the catalog makes way as it widens.
    const injector = inject(Injector);
    effect(() => {
      this.width();
      this.wide();
      afterNextRender(() => this.container().updateContentMargins(), { injector });
    });
  }

  /** An entity was chosen in the catalog: over the page, it closes. */
  protected chosen(): void {
    if (!this.wide()) {
      this.catalogOpen.set(false);
    }
  }

  protected resizeWithKeys(event: KeyboardEvent): void {
    const { min, max, step } = catalogWidths;
    const widths: Partial<Record<string, number>> = {
      ArrowLeft: this.width() - step,
      ArrowRight: this.width() + step,
      Home: min,
      End: max,
    };
    const width = widths[event.key];
    if (width === undefined) {
      return;
    }
    event.preventDefault();
    this.resize(width);
  }

  protected startDrag(event: PointerEvent): void {
    if (event.button !== 0) {
      return;
    }
    (event.target as Element).setPointerCapture?.(event.pointerId);
    this.dragStart = { x: event.clientX, width: this.width() };
    this.dragging.set(true);
    event.preventDefault();
  }

  protected drag(event: PointerEvent): void {
    if (this.dragStart) {
      this.width.set(clampWidth(this.dragStart.width + event.clientX - this.dragStart.x));
    }
  }

  protected endDrag(): void {
    if (this.dragStart) {
      this.dragStart = null;
      this.dragging.set(false);
      this.resize(this.width());
    }
  }

  private resize(width: number): void {
    this.width.set(clampWidth(width));
    writeStored(this.storage, BrowseLayout.storageKey, String(this.width()));
  }

  private storedWidth(): number {
    const stored = readStored(this.storage, BrowseLayout.storageKey);
    const width = Number(stored);
    return stored && Number.isFinite(width) ? clampWidth(width) : catalogWidths.initial;
  }
}

/** The entity browsing starts from (the address's first segment), as each navigation leaves it. */
function entityShown(router: Router, route: ActivatedRoute) {
  return toSignal(
    router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      startWith(null),
      map(() => route.snapshot.firstChild?.url[0]?.path ?? null),
    ),
    { initialValue: null },
  );
}

function clampWidth(width: number): number {
  return Math.round(Math.min(Math.max(width, catalogWidths.min), catalogWidths.max));
}
