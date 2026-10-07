/**
 * A `ResizeObserver` for jsdom, which has none (and lays nothing out): elements are given sizes by the test
 * (`resizeTo`), and their observers told.
 */
export class FakeResizeObserver {
  private static readonly observers = new Set<FakeResizeObserver>();
  private readonly elements = new Set<Element>();

  constructor(private readonly callback: ResizeObserverCallback) {
    FakeResizeObserver.observers.add(this);
  }

  /** Puts it in the window, for what is made after; `uninstall` takes it away. */
  static install(): void {
    (window as unknown as { ResizeObserver: unknown }).ResizeObserver = FakeResizeObserver;
  }

  static uninstall(): void {
    delete (window as unknown as { ResizeObserver?: unknown }).ResizeObserver;
    FakeResizeObserver.observers.clear();
  }

  observe(element: Element): void {
    this.elements.add(element);
  }

  unobserve(element: Element): void {
    this.elements.delete(element);
  }

  disconnect(): void {
    this.elements.clear();
    FakeResizeObserver.observers.delete(this);
  }

  /** Tells the observers of `element` that it is now `width` by `height`. */
  static resize(element: Element, width: number, height = 400): void {
    for (const observer of FakeResizeObserver.observers) {
      if (observer.elements.has(element)) {
        const entry = {
          target: element,
          contentRect: { width, height } as DOMRectReadOnly,
          contentBoxSize: [{ inlineSize: width, blockSize: height }],
          borderBoxSize: [{ inlineSize: width, blockSize: height }],
          devicePixelContentBoxSize: [],
        } as unknown as ResizeObserverEntry;
        observer.callback([entry], observer as unknown as ResizeObserver);
      }
    }
  }
}
