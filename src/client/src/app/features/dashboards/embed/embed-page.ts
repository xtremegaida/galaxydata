import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  untracked,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatIcon } from '@angular/material/icon';
import { MatProgressBar } from '@angular/material/progress-bar';
import { ApiClient } from '../../../core/api/api-client';
import { ProblemCode, problemOf } from '../../../core/api/problem';
import { PageTitle } from '../../../core/page-titles';
import { Message } from '../../../core/ui/message';
import { publicPalette } from '../charts/series-colors';
import { PublicHost } from '../state/dashboard-host';
import { DashboardStore } from '../state/dashboard-store';
import { refreshOnTimer } from '../state/refresh';
import { bindUrlState } from '../state/url-state';
import { DashboardView } from '../view/dashboard-view';

/** The most slices chosen in a widget a public dashboard takes (the server's `MaxPublicSelectionKeys`). */
const maxPublicKeys = 25;

/** What a framed page tells its parent of its height, to be as tall as it is. */
export interface SizeMessage {
  readonly type: 'galaxydata.dashboard.size';
  readonly version: 1;
  readonly height: number;
}

/**
 * A public dashboard, by its link, alone (no shell, no session: nothing here asks who is signed in): its name
 * (the page's title, and a heading screen readers find), the filters it shows, a Refresh, its widgets. Framed in
 * another site, it tells its parent its height as it changes (`postMessage`, only that), so the frame can be as
 * tall as the dashboard. What it can't show it says plainly, and never asks anyone to sign in.
 */
@Component({
  selector: 'gd-embed-page',
  imports: [DashboardView, MatButton, MatIcon, MatProgressBar, Message],
  providers: [DashboardStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="embed" tabindex="-1">
      <h1 class="hidden">{{ dashboard.hasValue() ? dashboard.value().name : '' }}</h1>
      @if (dashboard.isLoading() && !dashboard.hasValue()) {
        <mat-progress-bar mode="indeterminate" aria-label="Reading the dashboard" />
      }
      <div role="alert">
        @if (problem(); as problem) {
          <gd-message kind="problem">
            {{ problem.text }}
            @if (problem.again) {
              <button gdMessageAction matButton type="button" (click)="dashboard.reload()">
                Try again
              </button>
            }
          </gd-message>
        }
      </div>
      @if (dashboard.hasValue()) {
        @if (store.definition().refresh.mode === 'manual') {
          <div class="bar">
            <button
              matButton
              type="button"
              class="refresh"
              [disabled]="store.anyLoading()"
              (click)="store.refresh()"
            >
              <mat-icon>refresh</mat-icon>
              Refresh
            </button>
          </div>
        }
        <gd-dashboard-view />
      }
    </main>
  `,
  styles: `
    :host {
      display: block;
    }
    .embed {
      padding: 8px;
      box-sizing: border-box;
      outline: none;
    }
    .bar {
      display: flex;
      justify-content: flex-end;
      margin-bottom: 4px;
    }
    .refresh {
      --mat-button-text-container-height: 32px;
    }
    .hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
    }
  `,
})
export class EmbedPage {
  /** The dashboard's link, from the address. */
  readonly token = input.required<string>();

  private readonly api = inject(ApiClient);
  private readonly title = inject(PageTitle);
  private readonly document = inject(DOCUMENT);
  private readonly element = inject(ElementRef<HTMLElement>);
  protected readonly store = inject(DashboardStore);

  protected readonly dashboard = rxResource({
    params: () => this.token(),
    stream: ({ params: token }) =>
      this.api.get('/api/public/dashboards/{token}', { path: { token } }),
  });

  protected readonly problem = computed<{ text: string; again: boolean } | null>(() => {
    const error = this.dashboard.error();
    if (!error) {
      return null;
    }
    const problem = problemOf(error);
    if (problem.status === 404) {
      return { text: "This dashboard isn't available. It may no longer be public.", again: false };
    }
    if (problem.code === ProblemCode.tooManyRequests) {
      const wait = problem.retryAfter ? ` Try again in ${problem.retryAfter} seconds.` : '';
      return { text: `The dashboard is asked for too often just now.${wait}`, again: true };
    }
    return { text: "The dashboard couldn't be read. Try again in a moment.", again: true };
  });

  constructor() {
    refreshOnTimer(
      this.store,
      computed(() => this.store.definition().refresh),
    );
    effect(() => {
      const shown = this.dashboard.hasValue() ? this.dashboard.value() : undefined;
      const token = this.token();
      if (!shown) {
        return;
      }
      untracked(() => {
        this.title.detail.set(shown.name);
        this.store.definition.set(shown.definition);
        this.store.palettes.set(new Map(shown.palettes.map((p) => [p.id, publicPalette(p)])));
        this.store.host.set(new PublicHost(this.api, token));
      });
    });
    // Refreshed, it reads itself again (as it was, the browser's cache answers): its palettes may have changed, as
    // its rows' colours do at once.
    effect(() => {
      if (this.store.refreshes() > 0) {
        untracked(() => this.dashboard.reload());
      }
    });
    // The address keeps what is chosen; an embed doesn't say what of it was left out (its parent wrote it).
    bindUrlState(this.store, maxPublicKeys);
    this.tellHeight();
    inject(DestroyRef).onDestroy(() => this.title.detail.set(null));
  }

  /**
   * Framed, the page's height goes to its parent as it changes (a frame of the animation at most): to the
   * parent's origin where the browser says it (`ancestorOrigins`), else to any (it is a height only).
   */
  private tellHeight(): void {
    const view = this.document.defaultView;
    if (!view || view.parent === view) {
      return;
    }
    const Observer = (view as Window & { ResizeObserver?: typeof ResizeObserver }).ResizeObserver;
    if (!Observer) {
      return;
    }
    const origin = view.location.ancestorOrigins?.[0] ?? '*';
    let told = -1;
    let frame: number | null = null;
    const tell = () => {
      frame = null;
      const height = Math.ceil(
        (this.element.nativeElement as HTMLElement).getBoundingClientRect().height,
      );
      if (height !== told) {
        told = height;
        const message: SizeMessage = { type: 'galaxydata.dashboard.size', version: 1, height };
        view.parent.postMessage(message, origin);
      }
    };
    const observer = new Observer(() => {
      frame ??= view.requestAnimationFrame(tell);
    });
    afterNextRender(() => observer.observe(this.element.nativeElement as HTMLElement));
    inject(DestroyRef).onDestroy(() => {
      observer.disconnect();
      if (frame !== null) {
        view.cancelAnimationFrame(frame);
      }
    });
  }
}
