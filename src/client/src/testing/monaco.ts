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

  /** The line breaks set (Monaco's EndOfLineSequence: 0 for \n, 1 for \r\n); null when left as they were. */
  eol: number | null = null;

  setEOL(eol: number): void {
    this.eol = eol;
  }

  getEOL(): string {
    return this.eol === 1 || (this.eol === null && this.value.includes('\r\n')) ? '\r\n' : '\n';
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

  /** Where the keyboard is: a line and column, from 1. */
  position: { lineNumber: number; column: number } | null = null;
  /** The commands added (by their keys, as Monaco's KeyMod and KeyCode make them). */
  readonly commands = new Map<number, () => void>();

  updateOptions(options: Record<string, unknown>): void {
    Object.assign(this.options, options);
  }

  addCommand(keys: number, handler: () => void): string {
    this.commands.set(keys, handler);
    return String(keys);
  }

  setPosition(position: { lineNumber: number; column: number }): void {
    this.position = position;
  }

  revealPositionInCenter(): void {
    // Nothing to scroll here.
  }

  /** Ctrl+Enter (Cmd+Enter on macOS) pressed in it. */
  submit(): void {
    this.commands.get(FakeMonaco.ctrlCmd | FakeMonaco.enter)?.();
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
  static readonly ctrlCmd = 2048;
  static readonly enter = 3;
  readonly editors: FakeEditor[] = [];
  theme = '';
  fontsMeasured = 0;
  readonly MarkerSeverity = { Error: 8, Warning: 4, Info: 2 };
  readonly KeyMod = { CtrlCmd: FakeMonaco.ctrlCmd };
  readonly KeyCode = { Enter: FakeMonaco.enter };
  /** The languages registered, by id: their highlighting and configuration. */
  readonly languages = {
    registered: new Map<string, { tokens?: unknown; configuration?: unknown }>(),
    register: ({ id }: { id: string }) => {
      this.languages.registered.set(id, {});
    },
    setMonarchTokensProvider: (id: string, tokens: unknown) => {
      this.languages.registered.set(id, { ...this.languages.registered.get(id), tokens });
    },
    setLanguageConfiguration: (id: string, configuration: unknown) => {
      this.languages.registered.set(id, {
        ...this.languages.registered.get(id),
        configuration,
      });
    },
  };
  readonly editor = {
    create: (host: HTMLElement, options: Record<string, unknown>) => {
      const editor = new FakeEditor(host, options);
      this.editors.push(editor);
      return editor;
    },
    createModel: (value: string, language: string) => new FakeModel(value, language),
    EndOfLineSequence: { LF: 0, CRLF: 1 },
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
