import { DOCUMENT } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  input,
  model,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MatProgressBar } from '@angular/material/progress-bar';
import { type Monaco, MonacoLoader } from './monaco-loader';

/** Something wrong in the text: from an offset (in UTF-16 units), for a length, and why. */
export interface EditorMarker {
  readonly start: number;
  readonly length: number;
  readonly message: string;
}

type Editor = ReturnType<Monaco['editor']['create']>;
type Model = ReturnType<Monaco['editor']['createModel']>;

/** Whose markers the editor's are, among Monaco's. */
const markerOwner = 'gd';

let editors = 0;

/**
 * A text edited as code, in Monaco (loaded when an editor is first shown): highlighted in its language, with what is
 * wrong in it marked (and said when pointed at, or with F8). The text is two-way: the editor's as it is edited, and
 * the editor's when set from outside (an edit that can be undone). Should Monaco fail to load, the text is edited in
 * a plain text area. Tab inserts a tab, unless Ctrl+M makes it move the keyboard on.
 */
@Component({
  selector: 'gd-code-editor',
  imports: [MatProgressBar],
  template: `
    <div class="frame">
      @if (failed()) {
        <textarea
          class="plain"
          spellcheck="false"
          [attr.aria-label]="label()"
          [attr.aria-describedby]="describedBy()"
          [readOnly]="readOnly()"
          [value]="text()"
          (input)="typed($event)"
          #plain
        ></textarea>
      } @else {
        @if (!ready()) {
          <mat-progress-bar class="loading" mode="indeterminate" aria-label="Loading the editor" />
        }
        <div class="host" #host></div>
      }
    </div>
    @if (ready() && !readOnly()) {
      <p class="keys" [id]="keysId">Tab inserts a tab; {{ tabKeys }} makes it move on, and back.</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 160px;
    }

    .frame {
      position: relative;
      flex: 1;
      min-height: 120px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: var(--mat-sys-corner-extra-small);
      overflow: hidden;
    }

    .keys {
      margin: 4px 0 0;
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }

    .host,
    .plain {
      position: absolute;
      inset: 0;
    }

    .plain {
      box-sizing: border-box;
      width: 100%;
      height: 100%;
      margin: 0;
      padding: 8px;
      border: 0;
      resize: none;
      background: var(--mat-sys-surface);
      color: var(--mat-sys-on-surface);
      font: 13px/1.5 var(--gd-code-font-family);
      white-space: pre;
    }

    .loading {
      position: absolute;
      top: 0;
      z-index: 1;
    }
  `,
})
export class CodeEditor {
  private readonly loader = inject(MonacoLoader);
  private readonly document = inject(DOCUMENT);
  private readonly host = viewChild<ElementRef<HTMLElement>>('host');
  private readonly plain = viewChild<ElementRef<HTMLTextAreaElement>>('plain');

  /** The text, as it is edited. */
  readonly text = model.required<string>();
  /** What the editor is, for screen readers. */
  readonly label = input.required<string>();
  /** Monaco's language: `sql`, `pgsql`. */
  readonly language = input('sql');
  readonly readOnly = input(false);
  /** What is said when a read-only text is typed in. */
  readonly readOnlyMessage = input<string | null>(null);
  readonly markers = input<readonly EditorMarker[]>([]);
  /** The ids of what describes the editor (hints, problems). */
  readonly describedBy = input<string | null>(null);

  protected readonly keysId = `gd-code-editor-${editors++}-keys`;
  /** The keys of Monaco's tab focus mode (Tab moving the keyboard on): on macOS, with Shift. */
  protected readonly tabKeys = /Mac|iPhone|iPad/.test(
    this.document.defaultView?.navigator.platform ?? '',
  )
    ? 'Ctrl+Shift+M'
    : 'Ctrl+M';
  protected readonly ready = signal(false);
  /** Whether Monaco couldn't be loaded: the text is edited as plain text. */
  protected readonly failed = signal(false);
  private monaco: Monaco | null = null;
  private editor: Editor | null = null;
  private textModel: Model | null = null;
  private destroyed = false;

  constructor() {
    afterNextRender(() => void this.start());
    inject(DestroyRef).onDestroy(() => {
      this.destroyed = true;
      this.editor?.dispose();
      this.textModel?.dispose();
    });
    // The text set from outside: the editor's, as an edit (undone as one).
    effect(() => {
      const text = this.text();
      if (!this.ready()) {
        return;
      }
      untracked(() => {
        const model = this.textModel;
        if (model && model.getValue() !== text) {
          model.pushEditOperations([], [{ range: model.getFullModelRange(), text }], () => null);
        }
      });
    });
    effect(() => {
      const options = {
        ariaLabel: this.label(),
        readOnly: this.readOnly(),
        readOnlyMessage: { value: this.readOnlyMessage() ?? "This text can't be edited" },
      };
      if (this.ready()) {
        untracked(() => this.editor?.updateOptions(options));
      }
    });
    effect(() => {
      const language = this.language();
      if (this.ready()) {
        untracked(() => {
          if (this.monaco && this.textModel) {
            this.monaco.editor.setModelLanguage(this.textModel, language);
          }
        });
      }
    });
    effect(() => {
      const markers = this.markers();
      this.text();
      if (this.ready()) {
        untracked(() => this.mark(markers));
      }
    });
    effect(() => {
      // Its keys too, when they apply (Monaco's, in an editor that may be typed in).
      const describedBy =
        [this.describedBy(), this.readOnly() ? null : this.keysId].filter(Boolean).join(' ') ||
        null;
      if (this.ready()) {
        untracked(() => {
          const input = this.input();
          if (describedBy) {
            input?.setAttribute('aria-describedby', describedBy);
          } else {
            input?.removeAttribute('aria-describedby');
          }
        });
      }
    });
  }

  /** Puts the keyboard in the editor. */
  focus(): void {
    if (this.editor) {
      this.editor.focus();
    } else {
      this.plain()?.nativeElement.focus();
    }
  }

  protected typed(event: Event): void {
    this.text.set((event.target as HTMLTextAreaElement).value);
  }

  private async start(): Promise<void> {
    let monaco: Monaco;
    try {
      monaco = await this.loader.load();
    } catch {
      if (!this.destroyed) {
        this.failed.set(true);
      }
      return;
    }
    const host = this.host()?.nativeElement;
    if (this.destroyed || !host) {
      return;
    }
    this.monaco = monaco;
    const model = monaco.editor.createModel(untracked(this.text), untracked(this.language));
    // The page's (an editor in a tab not shown is made outside the page, where styles don't reach).
    const font = getComputedStyle(this.document.documentElement)
      .getPropertyValue('--gd-code-font-family')
      .trim();
    this.editor = monaco.editor.create(host, {
      model,
      ariaLabel: untracked(this.label),
      readOnly: untracked(this.readOnly),
      automaticLayout: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontFamily: font || undefined,
      fontSize: 13,
      // The text area screen readers know (not the edit context, which Monaco calls experimental).
      editContext: false,
      fixedOverflowWidgets: true,
    });
    this.textModel = model;
    model.onDidChangeContent(() => {
      const value = model.getValue();
      if (value !== untracked(this.text)) {
        this.text.set(value);
      }
    });
    this.ready.set(true);
  }

  /** The element the keyboard types in. */
  private input(): HTMLElement | null {
    return this.editor?.getDomNode()?.querySelector<HTMLElement>('textarea') ?? null;
  }

  private mark(markers: readonly EditorMarker[]): void {
    const { monaco, textModel: model } = this;
    if (!monaco || !model) {
      return;
    }
    monaco.editor.setModelMarkers(
      model,
      markerOwner,
      markers.map((marker) => {
        const start = model.getPositionAt(marker.start);
        const end = model.getPositionAt(marker.start + Math.max(marker.length, 1));
        return {
          severity: monaco.MarkerSeverity.Error,
          message: marker.message,
          startLineNumber: start.lineNumber,
          startColumn: start.column,
          endLineNumber: end.lineNumber,
          endColumn: end.column,
        };
      }),
    );
  }
}
