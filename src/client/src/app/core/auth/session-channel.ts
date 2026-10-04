import { DestroyRef, Injectable, inject } from '@angular/core';

/** What a tab says when who is signed in changes there: the user's id, or null when no one is. */
interface Announcement {
  readonly userId: number | null;
}

/**
 * Tells the application's other tabs when who is signed in changes, and hears when it changes in theirs. Tabs share
 * the session's cookie, so a tab still showing the user before would act as the one now signed in.
 */
@Injectable({ providedIn: 'root' })
export class SessionChannel {
  private readonly channel =
    typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel('gd.session');

  constructor() {
    inject(DestroyRef).onDestroy(() => this.channel?.close());
  }

  /** Tells the other tabs who is signed in now. */
  announce(userId: number | null): void {
    this.channel?.postMessage({ userId } satisfies Announcement);
  }

  /** Hears who another tab says is signed in now. */
  listen(heard: (userId: number | null) => void): void {
    this.channel?.addEventListener('message', (event: MessageEvent<Announcement | null>) =>
      heard(event.data?.userId ?? null),
    );
  }
}
