import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiClient, type Schema } from '../api/api-client';
import { problemOf, type Problem } from '../api/problem';
import { PageLoader } from '../browser/page-loader';
import { navigationTarget, pathOf, returnTo, returnUrlOf, safeReturnUrl } from './return-url';
import { SessionChannel } from './session-channel';

export type Session = Schema<'SessionDto'>;
export type SessionUser = Schema<'SessionUserDto'>;
export type Permissions = Schema<'PermissionsDto'>;

const signedOut: Session = { signedIn: false, user: null };

const noPermissions: Permissions = { canRead: false, canEditData: false, canAdmin: false };

/** How long a tab coming into view goes without asking again who is signed in. */
const recheckAfter = 30_000;

/**
 * Who is signed in, and what they may do: the session the server keeps (in its cookie), as the client knows it.
 * Signing in and out and changing the password go through here, and so does the server's word that the session
 * has ended, or that the password must be changed first.
 *
 * The application holds in memory what the user it was signed in as could see. When someone else is signed in
 * (here, in another tab, or found asking the server), or the same user may now see less, the application is loaded
 * anew, so none of it stays: another user starts at the start, the same one where they were going. Signing out
 * loads it anew too. The same user signing in again after their session ended goes on where they were.
 */
@Injectable({ providedIn: 'root' })
export class AuthStore {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly page = inject(PageLoader);
  private readonly channel = inject(SessionChannel);
  private readonly state = signal<Session | undefined>(undefined);
  private readonly noticeState = signal<string | null>(null);
  private readonly problemState = signal<Problem | null>(null);
  private loading: Promise<Session> | null = null;
  private epochValue = 0;
  private lastAsked = 0;
  /** The user whose session ended here, until someone signs in: what memory holds is theirs. */
  private endedUser: SessionUser | null = null;

  /** The session; undefined until the server has been asked. */
  readonly session = this.state.asReadonly();
  readonly user = computed(() => this.state()?.user ?? null);
  readonly signedIn = computed(() => this.state()?.signedIn === true);
  readonly permissions = computed(() => this.user()?.permissions ?? noPermissions);
  /** Why the user is asked to sign in when they were signed in: their session ended. */
  readonly notice = this.noticeState.asReadonly();
  /** What asking for the session met, when it failed. */
  readonly problem = this.problemState.asReadonly();

  constructor() {
    this.channel.listen((userId) => this.heard(userId));
    // A tab coming back (from the browser's back-forward cache, or into view) may have missed what another said.
    const document = inject(DOCUMENT);
    const view = document.defaultView;
    const shown = (event: PageTransitionEvent) => {
      if (event.persisted) {
        this.recheck(0);
      }
    };
    const visible = () => {
      if (document.visibilityState === 'visible') {
        this.recheck(recheckAfter);
      }
    };
    view?.addEventListener('pageshow', shown);
    document.addEventListener('visibilitychange', visible);
    inject(DestroyRef).onDestroy(() => {
      view?.removeEventListener('pageshow', shown);
      document.removeEventListener('visibilitychange', visible);
    });
  }

  /**
   * The session's number, which changes whenever who is signed in, or their session, does: what a request sent
   * under an older number meets is about a session that is gone.
   */
  get epoch(): number {
    return this.epochValue;
  }

  /** The session as known, asking the server the first time. */
  ensure(): Promise<Session> {
    const known = this.state();
    return known ? Promise.resolve(known) : this.load();
  }

  /**
   * Asks the server who is signed in, which also gives the anti-forgery token that requests changing anything
   * need. When someone else is signed in than was known, it is as if they had signed in here. When the server
   * can't be asked, `problem` says why, and what was known stays (no one signed in, the first time). An answer to a
   * question asked before the session changed is left aside.
   */
  load(): Promise<Session> {
    if (this.loading) {
      return this.loading;
    }
    const epoch = this.epochValue;
    this.lastAsked = Date.now();
    const loading: Promise<Session> = firstValueFrom(this.api.get('/api/auth/session'))
      .then(
        (session) => {
          if (epoch === this.epochValue) {
            this.problemState.set(null);
            this.found(session);
          }
          return this.state() ?? session;
        },
        (error: unknown) => {
          if (epoch === this.epochValue) {
            this.problemState.set(problemOf(error));
          }
          return this.state() ?? (this.state.set(signedOut), signedOut);
        },
      )
      .finally(() => {
        if (this.loading === loading) {
          this.loading = null;
        }
      });
    this.loading = loading;
    return loading;
  }

  /** Signs in, and goes to `returnUrl` (or the start). Rejects with the server's refusal. */
  async signIn(userName: string, password: string, returnUrl?: string | null): Promise<void> {
    const session = await firstValueFrom(
      this.api.post('/api/auth/sign-in', { body: { userName, password } }),
    );
    this.channel.announce(session.user?.id ?? null);
    await this.became(session, returnUrl ?? null);
  }

  /**
   * Changes the user's password (their other sessions end; this one goes on), and goes to `returnUrl` (or the
   * start). Rejects with the server's refusal.
   */
  async changePassword(
    currentPassword: string,
    newPassword: string,
    returnUrl?: string | null,
  ): Promise<void> {
    const session = await firstValueFrom(
      this.api.post('/api/auth/change-password', { body: { currentPassword, newPassword } }),
    );
    this.changed();
    this.state.set(session);
    this.channel.announce(session.user?.id ?? null);
    await this.router.navigateByUrl(safeReturnUrl(returnUrl));
  }

  /** Signs out, and loads the application anew at the sign-in page. Rejects when the server can't be told. */
  async signOut(): Promise<void> {
    await firstValueFrom(this.api.post('/api/auth/sign-out'));
    this.channel.announce(null);
    this.page.load('/sign-in');
  }

  /** The server says no one is signed in (any more): the user signs in again, to come back where they were. */
  ended(): void {
    const user = this.user();
    if (user) {
      this.endedUser = user;
      this.noticeState.set('Your session has ended. Sign in again to go on.');
    }
    this.problemState.set(null);
    this.changed();
    this.state.set(signedOut);
    const target = navigationTarget(this.router);
    if (pathOf(target) !== '/sign-in') {
      void this.router.navigate(['/sign-in'], { queryParams: returnTo(target) });
    }
  }

  /** The server says the user's password must be changed before anything else. */
  passwordChangeRequired(): void {
    this.changed();
    this.state.update((session) =>
      session?.user
        ? {
            ...session,
            user: { ...session.user, mustChangePassword: true, permissions: noPermissions },
          }
        : session,
    );
    const target = navigationTarget(this.router);
    if (pathOf(target) !== '/change-password') {
      void this.router.navigate(['/change-password'], { queryParams: returnTo(target) });
    }
  }

  /** What asking for the session found. */
  private found(session: Session): void {
    const known = this.state();
    const user = this.user();
    if (known === undefined) {
      this.state.set(session);
    } else if (session.user === null) {
      if (user) {
        this.ended();
      } else {
        this.state.set(session);
      }
    } else if (user?.id === session.user.id) {
      this.state.set(session);
    } else {
      // Someone signed in elsewhere: as if here, back from the sign-in page to where it was going.
      const url = navigationTarget(this.router);
      void this.became(session, pathOf(url) === '/sign-in' ? returnUrlOf(url) : url);
    }
  }

  /**
   * Who is signed in now. The application goes on to `returnUrl` (or the start), unless what it holds in memory
   * is another user's, or more than the user may now see: then it is loaded anew.
   */
  private async became(session: Session, returnUrl: string | null): Promise<void> {
    const holder = this.user() ?? this.endedUser;
    const user = session.user;
    this.changed();
    this.endedUser = null;
    this.noticeState.set(null);
    this.problemState.set(null);
    const url = safeReturnUrl(returnUrl);
    if (holder && holder.id !== user?.id) {
      this.page.load('/');
      return;
    }
    if (holder && user && fewer(holder.permissions, user.permissions)) {
      this.page.load(url);
      return;
    }
    this.state.set(session);
    await this.router.navigateByUrl(url);
  }

  /** What another tab says: who is signed in there now. */
  private heard(userId: number | null): void {
    const holder = this.user() ?? this.endedUser;
    if (holder === null) {
      // This tab holds no one's: it goes on where it was going, as whoever it now is.
      if (userId !== null) {
        this.page.reload();
      }
    } else if (userId === null) {
      this.page.load('/sign-in');
    } else if (userId !== holder.id) {
      this.page.load('/');
    } else {
      // The same user, signed in again or with a new password there: as they are now.
      void this.load();
    }
  }

  /** Asks again who is signed in, when the session is known and was last asked for long enough ago. */
  private recheck(after: number): void {
    if (this.state() !== undefined && Date.now() - this.lastAsked >= after) {
      void this.load();
    }
  }

  /** The session changes: answers to what was asked before are about another. */
  private changed(): void {
    this.epochValue++;
    this.loading = null;
  }
}

/** Whether a user may do less than before. */
function fewer(before: Permissions, after: Permissions): boolean {
  return (Object.keys(before) as (keyof Permissions)[]).some((key) => before[key] && !after[key]);
}
