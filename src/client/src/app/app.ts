import { DOCUMENT } from '@angular/common';
import { Component, Injector, afterNextRender, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterOutlet, type Route } from '@angular/router';
import { filter } from 'rxjs';

/** The application: its pages, the shell's or those of signing in. */
@Component({
  selector: 'gd-root',
  imports: [RouterOutlet],
  template: '<router-outlet />',
  styles: `
    :host {
      display: block;
      height: 100%;
    }
  `,
})
export class App {
  constructor() {
    // Another page opened: focus goes to it, as it would to a page loaded, for keyboards and screen readers. A
    // page's own changes of address (its filters, its rows) leave focus where it is.
    const router = inject(Router);
    const document = inject(DOCUMENT);
    const injector = inject(Injector);
    let shown: Route | null | undefined;
    router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        let route = router.routerState.snapshot.root;
        while (route.firstChild) {
          route = route.firstChild;
        }
        if (shown !== undefined && route.routeConfig !== shown) {
          afterNextRender(
            () => document.querySelector<HTMLElement>('main')?.focus({ preventScroll: true }),
            { injector },
          );
        }
        shown = route.routeConfig;
      });
  }
}
