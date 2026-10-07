import { DestroyRef, type Signal, inject, signal } from '@angular/core';

/** An element's size, as its box is now. */
export interface ElementSize {
  readonly width: number;
  readonly height: number;
}

/**
 * An element's content size as it changes (a `ResizeObserver`'s), from when it is first laid out; null until then,
 * and where the browser has no observer (jsdom). Made in an injection context: it stops when that ends.
 */
export function elementSize(element: Element): Signal<ElementSize | null> {
  const size = signal<ElementSize | null>(null);
  const view = element.ownerDocument.defaultView as
    (Window & { ResizeObserver?: typeof ResizeObserver }) | null;
  const Observer = view?.ResizeObserver;
  if (!Observer) {
    return size.asReadonly();
  }
  const observer = new Observer((entries) => {
    const entry = entries.at(-1);
    const box = entry?.contentBoxSize?.[0];
    const width = box ? box.inlineSize : (entry?.contentRect.width ?? 0);
    const height = box ? box.blockSize : (entry?.contentRect.height ?? 0);
    const before = size();
    if (!before || before.width !== width || before.height !== height) {
      size.set({ width, height });
    }
  });
  observer.observe(element);
  inject(DestroyRef).onDestroy(() => observer.disconnect());
  return size.asReadonly();
}
