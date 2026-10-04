/** The browser's local storage, when it lets the page have it. */
export function storageOf(document: Document): Storage | undefined {
  try {
    return document.defaultView?.localStorage;
  } catch {
    return undefined;
  }
}

/** A value kept in the browser; null when none is, or storage is refused. */
export function readStored(storage: Storage | undefined, key: string): string | null {
  try {
    return storage?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

/** Keeps a value in the browser; when storage is full or refused, it holds only until the page is left. */
export function writeStored(storage: Storage | undefined, key: string, value: string): void {
  try {
    storage?.setItem(key, value);
  } catch {
    // Storage full or refused.
  }
}
