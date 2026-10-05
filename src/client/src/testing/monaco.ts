import type { Provider } from '@angular/core';
import { type Monaco, MonacoLoader } from '../app/core/editor/monaco-loader';

/** A marker as Monaco's editor takes it. */
export interface FakeMarker {
  readonly severity: number;
  readonly message: string;
  readonly startLineNumber: number;
  readonly startColumn: number;
  readonly endLineNumber: number;
  readonly endColumn: number;
}

/** A text model, as far as the application's editors use one. */
export class FakeModel {
  markers: readonly FakeMarker[] = [];
  disposed = false;
  /** The edits made from outside (each one undone at once). */
  readonly edits: string[] = [];
  private readonly listeners: (() => void)[] = [];

  constructor(
    private value: string,
    public language: string,
  ) {}

  getValue(): string {
    return this.value;
  }

  getFullModelRange() {
    return { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 };
  }

  pushEditOperations(_: unknown, edits: readonly { readonly text: string }[]): null {
    this.value = edits[0].text;
    this.edits.push(this.value);
    this.changed();
    return null;
  }

  getPositionAt(offset: number): { lineNumber: number; column: number } {
    const lines = this.value.slice(0, offset).split(/\r?\n/);
    return { lineNumber: lines.length, column: (lines.at(-1)?.length ?? 0) + 1 };
  }

  onDidChangeContent(listener: () => void): { dispose(): void } {
    this.listeners.push(listener);
    return { dispose: () => undefined };
  }

  dispose(): void {
    this.disposed = true;
  }

  /** The user types: the text becomes `text`. */
  type(text: string): void {
    this.value = text;
    this.changed();
  }

  private changed(): void {
    this.listeners.forEach((listener) => listener());
  }
}

/** An editor, as far as the application uses one: its options, and a text area to type in. */
export class FakeEditor {
  readonly options: Record<string, unknown>;
  disposed = false;
  readonly element: HTMLElement;
  readonly input: HTMLTextAreaElement;

  constructor(host: HTMLElement, options: Record<string, unknown>) {
    this.options = { ...options };
    this.element = host.ownerDocument.createElement('div');
    this.input = host.ownerDocument.createElement('textarea');
    this.element.append(this.input);
    host.append(this.element);
  }

  get model(): FakeModel {
    return this.options['model'] as FakeModel;
  }

  updateOptions(options: Record<string, unknown>): void {
    Object.assign(this.options, options);
  }

  getDomNode(): HTMLElement {
    return this.element;
  }

  focus(): void {
    this.input.focus();
  }

  dispose(): void {
    this.disposed = true;
    this.element.remove();
  }
}

/** Monaco, as far as the application uses it: the editors and models it made, and its theme. */
export class FakeMonaco {
  readonly editors: FakeEditor[] = [];
  theme = '';
  fontsMeasured = 0;
  readonly MarkerSeverity = { Error: 8 };
  readonly editor = {
    create: (host: HTMLElement, options: Record<string, unknown>) => {
      const editor = new FakeEditor(host, options);
      this.editors.push(editor);
      return editor;
    },
    createModel: (value: string, language: string) => new FakeModel(value, language),
    setModelMarkers: (model: FakeModel, _owner: string, markers: FakeMarker[]) => {
      model.markers = markers;
    },
    setModelLanguage: (model: FakeModel, language: string) => {
      model.language = language;
    },
    setTheme: (theme: string) => {
      this.theme = theme;
    },
    remeasureFonts: () => {
      this.fontsMeasured++;
    },
  };

  /** The editor made last. */
  get last(): FakeEditor {
    const editor = this.editors.at(-1);
    if (!editor) {
      throw new Error('No editor was made');
    }
    return editor;
  }

  /** The editors not disposed of. */
  get live(): FakeEditor[] {
    return this.editors.filter((editor) => !editor.disposed);
  }

  asMonaco(): Monaco {
    return this as unknown as Monaco;
  }
}

/** The editors on a fake Monaco (or none: it fails to load, and they are text areas). */
export function fakeMonacoProviders(monaco: FakeMonaco | null = new FakeMonaco()): Provider[] {
  return [
    {
      provide: MonacoLoader,
      useValue: {
        load: () =>
          monaco ? Promise.resolve(monaco.asMonaco()) : Promise.reject(new Error('Offline')),
      },
    },
  ];
}
