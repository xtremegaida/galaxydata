import {
  ChangeDetectionStrategy,
  Component,
  type ComponentRef,
  DestroyRef,
  type OutputRef,
  type OutputRefSubscription,
  ViewContainerRef,
  effect,
  inject,
  input,
  model,
  resource,
  untracked,
  viewChild,
} from '@angular/core';
import type { WidgetConfig } from '../../model/definition';
import type { WidgetKind } from '../../model/widget-registry';

/**
 * A widget kind's own settings, as its registry entry's editor component edits them (loaded when first shown): its
 * `config` given and taken back, and its source's `entity`.
 */
@Component({
  selector: 'gd-kind-settings-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<ng-container #slot />`,
  styles: `
    :host {
      display: block;
    }
  `,
})
export class KindSettingsHost {
  readonly kind = input.required<WidgetKind>();
  readonly entity = input<string | null>(null);
  readonly config = model.required<WidgetConfig>();

  private readonly slot = viewChild.required('slot', { read: ViewContainerRef });
  private readonly type = resource({
    params: () => this.kind(),
    loader: ({ params }) => params.editor(),
  });

  constructor() {
    let made: ComponentRef<unknown> | null = null;
    let taken: OutputRefSubscription | null = null;
    effect(() => {
      const type = this.type.hasValue() ? this.type.value() : undefined;
      const slot = this.slot();
      untracked(() => {
        if (made?.componentType === type) {
          return;
        }
        taken?.unsubscribe();
        made?.destroy();
        made = null;
        if (type) {
          made = slot.createComponent(type);
          const instance = made.instance as { config: OutputRef<WidgetConfig> };
          taken = instance.config.subscribe((config) => this.config.set(config));
        }
      });
      this.give(made);
    });
    inject(DestroyRef).onDestroy(() => taken?.unsubscribe());
  }

  /** The settings shown are the widget's (read here, so they follow it). */
  private give(made: ComponentRef<unknown> | null): void {
    const config = this.config();
    const entity = this.entity();
    made?.setInput('config', config);
    made?.setInput('entity', entity);
  }
}
