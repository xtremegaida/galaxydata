import { Injectable, effect, inject, signal, untracked } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { TitleStrategy, type RouterStateSnapshot } from '@angular/router';

/**
 * What the page shown says of itself, before its route's title: the name of what it shows when only it knows (a
 * saved query's), for as long as it shows it.
 */
@Injectable({ providedIn: 'root' })
export class PageTitle {
  readonly detail = signal<string | null>(null);
}

/** Pages' titles, with the application's name after: "Sign in · GalaxyData", "Monthly totals · Query · GalaxyData". */
@Injectable()
export class PageTitles extends TitleStrategy {
  private readonly title = inject(Title);
  private readonly page = inject(PageTitle);
  /** The route's title, as the last navigation built it. */
  private routeTitle: string | undefined;

  constructor() {
    super();
    effect(() => {
      const detail = this.page.detail();
      untracked(() => this.show(detail));
    });
  }

  override updateTitle(snapshot: RouterStateSnapshot): void {
    this.routeTitle = this.buildTitle(snapshot);
    this.show(this.page.detail());
  }

  private show(detail: string | null): void {
    const parts = [detail, this.routeTitle, 'GalaxyData'].filter((part) => !!part);
    this.title.setTitle(parts.join(' · '));
  }
}
