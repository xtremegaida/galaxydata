import { Component, input } from '@angular/core';

let nextId = 0;

/** The page of signing in or changing the password: a card with the application's mark, a heading, and a form. */
@Component({
  selector: 'gd-auth-layout',
  template: `
    <main class="page" tabindex="-1">
      <section class="card" [attr.aria-labelledby]="headingId">
        <header class="header">
          <img class="mark" src="favicon.svg" alt="" width="40" height="40" />
          <h1 class="heading" [id]="headingId">{{ heading() }}</h1>
        </header>
        <ng-content />
      </section>
    </main>
  `,
  styles: `
    .page {
      outline: none;
      display: grid;
      place-items: center;
      min-height: 100%;
      padding: 24px 16px;
      box-sizing: border-box;
    }

    .card {
      width: 100%;
      max-width: 400px;
      padding: 32px 24px 24px;
      box-sizing: border-box;
      border-radius: var(--mat-sys-corner-extra-large);
      background: var(--mat-sys-surface-container-low);
      box-shadow: var(--mat-sys-level1);
    }

    .header {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 12px;
      margin-bottom: 24px;
      text-align: center;
    }

    .heading {
      margin: 0;
      font: var(--mat-sys-headline-small);
    }
  `,
})
export class AuthLayout {
  readonly heading = input.required<string>();

  protected readonly headingId = `gd-auth-heading-${nextId++}`;
}
