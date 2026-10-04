import { Component, computed, inject } from '@angular/core';
import { AuthStore } from '../../core/auth/auth-store';

/** The start: whom the application greets, and what they may do in it. */
@Component({
  selector: 'gd-start',
  template: `
    <section class="start">
      <h1 class="heading">Welcome, {{ name() }}</h1>
      <p class="summary">{{ summary() }}</p>
    </section>
  `,
  styles: `
    .start {
      max-width: 720px;
      padding: 32px 24px;
    }

    .heading {
      margin: 0 0 8px;
      font: var(--mat-sys-headline-medium);
    }

    .summary {
      margin: 0;
      font: var(--mat-sys-body-large);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class Start {
  private readonly auth = inject(AuthStore);

  protected readonly name = computed(() => {
    const user = this.auth.user();
    return user?.displayName || user?.userName || '';
  });

  protected readonly summary = computed(() => {
    const permissions = this.auth.permissions();
    if (permissions.canAdmin) {
      return 'You may browse, query and change the data, and look after its users, connections and catalog.';
    }
    if (permissions.canEditData) {
      return 'You may browse, query and change the data.';
    }
    return 'You may browse and query the data.';
  });
}
