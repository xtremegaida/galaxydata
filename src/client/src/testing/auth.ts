import type { Provider } from '@angular/core';
import type { Session, SessionUser } from '../app/core/auth/auth-store';
import { SessionChannel } from '../app/core/auth/session-channel';
import type { UserRole } from '../app/core/auth/roles';
import { PageLoader } from '../app/core/browser/page-loader';

/** No one signed in. */
export const signedOut: Session = { signedIn: false, user: null };

/** A session of a user with a role: Ada (1), an administrator, unless said otherwise. */
export function sessionOf(role: UserRole = 'admin', user: Partial<SessionUser> = {}): Session {
  const mustChangePassword = user.mustChangePassword ?? false;
  return {
    signedIn: true,
    user: {
      id: 1,
      userName: 'ada',
      displayName: 'Ada Lovelace',
      role,
      mustChangePassword,
      permissions: {
        canRead: !mustChangePassword,
        canEditData: !mustChangePassword && role !== 'read',
        canAdmin: !mustChangePassword && role === 'admin',
      },
      ...user,
    },
  };
}

/** Pages loaded anew, kept rather than loaded. */
export class FakePageLoader {
  readonly loads: string[] = [];
  reloads = 0;

  load(url: string): void {
    this.loads.push(url);
  }

  reload(): void {
    this.reloads++;
  }
}

/** The other tabs: what this one told them, and what they say. */
export class FakeSessionChannel {
  readonly announced: (number | null)[] = [];
  private readonly listeners: ((userId: number | null) => void)[] = [];

  announce(userId: number | null): void {
    this.announced.push(userId);
  }

  listen(heard: (userId: number | null) => void): void {
    this.listeners.push(heard);
  }

  /** Another tab says who is signed in there now. */
  hear(userId: number | null): void {
    for (const listener of this.listeners) {
      listener(userId);
    }
  }
}

/** The page loader and the channel to other tabs, as fakes. */
export function fakeBrowserProviders(): Provider[] {
  return [
    { provide: PageLoader, useClass: FakePageLoader },
    { provide: SessionChannel, useClass: FakeSessionChannel },
  ];
}

/** An API problem's body, as the server answers it. */
export function problemBody(
  code: string,
  title: string,
  extra: Record<string, unknown> = {},
): Record<string, unknown> {
  return { code, title, traceId: '00-trace', ...extra };
}
