import { Component, computed, input } from '@angular/core';
import { MatIcon } from '@angular/material/icon';

export type MessageKind = 'problem' | 'warning' | 'notice' | 'success';

const icons: Readonly<Record<MessageKind, string>> = {
  problem: 'error',
  warning: 'warning',
  notice: 'info',
  success: 'check_circle',
};

/**
 * A message in a page: a problem, a warning, a notice or a success, with what to do about it (an element marked
 * `gdMessageAction`). Live regions (`role="alert"`, `role="status"`) go around it, as the page needs.
 */
@Component({
  selector: 'gd-message',
  imports: [MatIcon],
  host: { '[class]': '"kind-" + kind()' },
  template: `
    <mat-icon>{{ shownIcon() }}</mat-icon>
    <div class="text"><ng-content /></div>
    <ng-content select="[gdMessageAction]" />
  `,
  styles: `
    :host {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      margin: 0 0 16px;
      padding: 12px 16px;
      border-radius: var(--mat-sys-corner-medium);
      font: var(--mat-sys-body-medium);
    }

    mat-icon {
      flex: none;
    }

    .text {
      flex: 1;
      margin: 2px 0;
      overflow-wrap: anywhere;
    }

    :host(.kind-problem) {
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }

    :host(.kind-warning) {
      background: var(--mat-sys-tertiary-container);
      color: var(--mat-sys-on-tertiary-container);
    }

    :host(.kind-notice) {
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }

    :host(.kind-success) {
      background: var(--mat-sys-primary-container);
      color: var(--mat-sys-on-primary-container);
    }
  `,
})
export class Message {
  readonly kind = input<MessageKind>('notice');
  /** A Material Symbols name, in place of the kind's. */
  readonly icon = input<string>();

  protected readonly shownIcon = computed(() => this.icon() ?? icons[this.kind()]);
}
