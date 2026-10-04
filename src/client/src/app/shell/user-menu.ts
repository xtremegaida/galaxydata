import { Component, computed, inject } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { MatDivider } from '@angular/material/divider';
import { MatIcon } from '@angular/material/icon';
import { MatMenu, MatMenuItem, MatMenuTrigger } from '@angular/material/menu';
import { Router } from '@angular/router';
import { problemOf } from '../core/api/problem';
import { AuthStore } from '../core/auth/auth-store';
import { returnTo } from '../core/auth/return-url';
import { roleLabels } from '../core/auth/roles';
import { Notifier } from '../core/notify/notifier';

/** Who is signed in, with changing the password and signing out. */
@Component({
  selector: 'gd-user-menu',
  imports: [MatButton, MatDivider, MatIcon, MatMenu, MatMenuItem, MatMenuTrigger],
  template: `
    <button
      matButton
      class="trigger"
      [matMenuTriggerFor]="menu"
      [attr.aria-label]="'Account: ' + name()"
    >
      <mat-icon>account_circle</mat-icon>
      <span class="name">{{ name() }}</span>
    </button>
    <mat-menu #menu="matMenu" xPosition="before">
      <div class="who">
        <div class="display-name">{{ name() }}</div>
        <div class="detail">{{ user()?.userName }} · {{ role() }}</div>
      </div>
      <mat-divider />
      <button mat-menu-item (click)="changePassword()">
        <mat-icon>password</mat-icon>
        <span>Change password</span>
      </button>
      <button mat-menu-item (click)="signOut()">
        <mat-icon>logout</mat-icon>
        <span>Sign out</span>
      </button>
    </mat-menu>
  `,
  styles: `
    .trigger {
      color: inherit;
    }

    .name {
      max-width: 16em;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    @media (max-width: 599.98px) {
      .name {
        display: none;
      }
    }

    .who {
      padding: 8px 16px 12px;
    }

    .display-name {
      font: var(--mat-sys-title-small);
    }

    .detail {
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class UserMenu {
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);

  protected readonly user = this.auth.user;
  protected readonly name = computed(() => {
    const user = this.user();
    return user?.displayName || user?.userName || '';
  });
  protected readonly role = computed(() => {
    const role = this.user()?.role;
    return role ? roleLabels[role] : '';
  });

  protected changePassword(): void {
    void this.router.navigate(['/change-password'], { queryParams: returnTo(this.router.url) });
  }

  protected async signOut(): Promise<void> {
    try {
      await this.auth.signOut();
    } catch (error) {
      this.notifier.problem(problemOf(error));
    }
  }
}
