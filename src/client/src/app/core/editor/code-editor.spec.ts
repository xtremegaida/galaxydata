import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { settle } from '../../../testing/http';
import { FakeMonaco, fakeMonacoProviders } from '../../../testing/monaco';
import { ColorScheme } from '../theme/color-scheme';
import { CodeEditor, type EditorMarker } from './code-editor';
import { MONACO_IMPORT, MonacoLoader } from './monaco-loader';

@Component({
  imports: [CodeEditor],
  template: `
    @if (shown()) {
      <gd-code-editor
        [(text)]="text"
        label="The script for shop"
        [language]="language()"
        [readOnly]="readOnly()"
        readOnlyMessage="Only administrators edit it"
        [markers]="markers()"
        describedBy="hint"
      />
    }
  `,
})
class Host {
  readonly shown = signal(true);
  readonly text = signal('UPDATE a SET b = 1;\nDROP TABLE a;\n');
  readonly language = signal('sql');
  readonly readOnly = signal(false);
  readonly markers = signal<EditorMarker[]>([]);
}

@Component({
  imports: [CodeEditor],
  template: `
    <gd-code-editor
      [(text)]="text"
      label="The query"
      submitHint="runs the query"
      (submitted)="submitted = submitted + 1"
    />
  `,
})
class Submitting {
  readonly text = signal('shop.orders\r\n.take(1)');
  submitted = 0;
}

describe('CodeEditor', () => {
  async function opened(monaco: FakeMonaco | null) {
    TestBed.configureTestingModule({ providers: fakeMonacoProviders(monaco) });
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();
    return {
      fixture,
      host: fixture.componentInstance,
      element: fixture.nativeElement as HTMLElement,
    };
  }

  it('edits the text in Monaco: as typed, and as set (an edit undone at once)', async () => {
    const monaco = new FakeMonaco();
    const { fixture, host, element } = await opened(monaco);
    const editor = monaco.last;
    expect(editor.model.getValue()).toBe(host.text());
    expect(editor.model.language).toBe('sql');
    expect(editor.options).toMatchObject({
      ariaLabel: 'The script for shop',
      readOnly: false,
      readOnlyMessage: { value: 'Only administrators edit it' },
      editContext: false,
    });
    // Described by what it is given, and its keys.
    const keys = element.querySelector('.keys')!;
    expect(keys.textContent).toBe('Tab inserts a tab; Ctrl+M makes it move on, and back.');
    expect(element.querySelector('gd-code-editor textarea')?.getAttribute('aria-describedby')).toBe(
      `hint ${keys.id}`,
    );
    expect(element.querySelector('mat-progress-bar')).toBeNull();

    editor.model.type('UPDATE a SET b = 2;\n');
    expect(host.text()).toBe('UPDATE a SET b = 2;\n');
    expect(editor.model.edits).toEqual([]);

    host.text.set('UPDATE a SET b = 3;\n');
    fixture.detectChanges();
    await settle();
    expect(editor.model.getValue()).toBe('UPDATE a SET b = 3;\n');
    expect(editor.model.edits).toEqual(['UPDATE a SET b = 3;\n']);
  });

  it('marks what is wrong in the text, where it is', async () => {
    const monaco = new FakeMonaco();
    const { fixture, host } = await opened(monaco);
    host.markers.set([{ start: 20, length: 13, message: 'Only changes to data run' }]);
    fixture.detectChanges();
    await settle();
    expect(monaco.last.model.markers).toEqual([
      {
        severity: 8,
        message: 'Only changes to data run',
        startLineNumber: 2,
        startColumn: 1,
        endLineNumber: 2,
        endColumn: 14,
      },
    ]);
    // What has no length is marked on a character.
    host.markers.set([{ start: 0, length: 0, message: 'Here' }]);
    fixture.detectChanges();
    await settle();
    expect(monaco.last.model.markers[0]).toMatchObject({ startColumn: 1, endColumn: 2 });
    host.markers.set([]);
    fixture.detectChanges();
    await settle();
    expect(monaco.last.model.markers).toEqual([]);
  });

  it('follows its options, and goes with what showed it', async () => {
    const monaco = new FakeMonaco();
    const { fixture, host } = await opened(monaco);
    host.readOnly.set(true);
    host.language.set('pgsql');
    fixture.detectChanges();
    await settle();
    expect(monaco.last.options['readOnly']).toBe(true);
    expect(monaco.last.model.language).toBe('pgsql');
    // Read-only, its keys don't apply.
    expect(fixture.nativeElement.querySelector('.keys')).toBeNull();
    expect(monaco.last.input.getAttribute('aria-describedby')).toBe('hint');

    const model = monaco.last.model;
    host.shown.set(false);
    fixture.detectChanges();
    expect(monaco.last.disposed).toBe(true);
    expect(model.disposed).toBe(true);
  });

  it("takes the page's code font, and says macOS's keys there", async () => {
    document.documentElement.style.setProperty('--gd-code-font-family', 'Fira Code');
    const platform = vi.spyOn(navigator, 'platform', 'get').mockReturnValue('MacIntel');
    try {
      const monaco = new FakeMonaco();
      const { element } = await opened(monaco);
      expect(monaco.last.options['fontFamily']).toBe('Fira Code');
      expect(element.querySelector('.keys')?.textContent).toContain(
        'Ctrl+Shift+M makes it move on',
      );
    } finally {
      platform.mockRestore();
      document.documentElement.style.removeProperty('--gd-code-font-family');
    }
  });

  it('marks how wrong each thing is: errors, warnings and what is only said', async () => {
    const monaco = new FakeMonaco();
    const { fixture, host } = await opened(monaco);
    host.markers.set([
      { start: 0, length: 1, message: 'Wrong' },
      { start: 1, length: 1, message: 'Odd', severity: 'warning' },
      { start: 2, length: 1, message: 'Said', severity: 'info' },
      { start: 3, length: 1, message: 'Wrong too', severity: 'error' },
    ]);
    fixture.detectChanges();
    await settle();
    expect(monaco.last.model.markers.map((marker) => marker.severity)).toEqual([8, 4, 2, 8]);
  });

  it('puts the keyboard where it is asked to, in sight', async () => {
    const monaco = new FakeMonaco();
    const { fixture, element } = await opened(monaco);
    const editor = fixture.debugElement.query(By.directive(CodeEditor))
      .componentInstance as CodeEditor;
    editor.reveal(22);
    expect(monaco.last.position).toEqual({ lineNumber: 2, column: 3 });
    expect(document.activeElement).toBe(monaco.last.input);
    expect(element.contains(document.activeElement)).toBe(true);
  });

  it("makes line breaks line feeds, unless the text given has Windows' own", async () => {
    const monaco = new FakeMonaco();
    await opened(monaco);
    expect(monaco.last.model.eol).toBe(0);

    TestBed.resetTestingModule();
    const other = new FakeMonaco();
    TestBed.configureTestingModule({ providers: fakeMonacoProviders(other) });
    const fixture = TestBed.createComponent(Submitting);
    fixture.detectChanges();
    await settle();
    expect(other.last.model.eol).toBeNull();
  });

  it('takes the line breaks of a text set later, so it is the same text', async () => {
    const monaco = new FakeMonaco();
    const { fixture, host } = await opened(monaco);
    expect(monaco.last.model.eol).toBe(0);
    host.text.set('UPDATE a SET b = 1;\r\nDROP TABLE a;\r\n');
    fixture.detectChanges();
    await settle();
    expect(monaco.last.model.eol).toBe(1);
    expect(host.text()).toBe('UPDATE a SET b = 1;\r\nDROP TABLE a;\r\n');
    host.text.set('one line');
    fixture.detectChanges();
    await settle();
    expect(monaco.last.model.eol).toBe(1);
    host.text.set('a\nb');
    fixture.detectChanges();
    await settle();
    expect(monaco.last.model.eol).toBe(0);
  });

  it('submits the text with Ctrl+Enter when it says what that does', async () => {
    const monaco = new FakeMonaco();
    TestBed.configureTestingModule({ providers: fakeMonacoProviders(monaco) });
    const fixture = TestBed.createComponent(Submitting);
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.keys')?.textContent).toBe(
      'Tab inserts a tab; Ctrl+M makes it move on, and back. Ctrl+Enter runs the query.',
    );
    monaco.last.submit();
    expect(fixture.componentInstance.submitted).toBe(1);

    // Without saying it, Ctrl+Enter is Monaco's.
    const others = new FakeMonaco();
    TestBed.resetTestingModule();
    await opened(others);
    expect(others.last.commands.size).toBe(0);
  });

  it('submits a plain text area with Ctrl+Enter too, Cmd+Enter on macOS', async () => {
    const platform = vi.spyOn(navigator, 'platform', 'get').mockReturnValue('Win32');
    try {
      TestBed.configureTestingModule({ providers: fakeMonacoProviders(null) });
      const fixture = TestBed.createComponent(Submitting);
      fixture.detectChanges();
      await settle();
      fixture.detectChanges();
      const area = (fixture.nativeElement as HTMLElement).querySelector('textarea')!;
      const pressed = (init: KeyboardEventInit) => {
        const event = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true, ...init });
        area.dispatchEvent(event);
        return event.defaultPrevented;
      };
      expect(pressed({})).toBe(false);
      expect(pressed({ metaKey: true })).toBe(false);
      expect(pressed({ ctrlKey: true })).toBe(true);
      expect(fixture.componentInstance.submitted).toBe(1);
    } finally {
      platform.mockRestore();
    }
  });

  it("edits the text as plain text when Monaco can't be loaded", async () => {
    const { fixture, host, element } = await opened(null);
    const area = element.querySelector<HTMLTextAreaElement>('textarea.plain')!;
    expect(area.value).toBe(host.text());
    expect(area.getAttribute('aria-label')).toBe('The script for shop');
    expect(area.getAttribute('aria-describedby')).toBe('hint');
    // Tab moves on in a text area.
    expect(element.querySelector('.keys')).toBeNull();
    area.value = 'UPDATE a SET b = 4;';
    area.dispatchEvent(new Event('input'));
    expect(host.text()).toBe('UPDATE a SET b = 4;');
    host.readOnly.set(true);
    fixture.detectChanges();
    expect(area.readOnly).toBe(true);
  });
});

describe('MonacoLoader', () => {
  const dark = signal(false);
  let imports = 0;
  let monaco: FakeMonaco;

  beforeEach(() => {
    imports = 0;
    monaco = new FakeMonaco();
    TestBed.configureTestingModule({
      providers: [
        { provide: ColorScheme, useValue: { dark } },
        {
          provide: MONACO_IMPORT,
          useValue: () => {
            imports++;
            return Promise.resolve(monaco.asMonaco());
          },
        },
      ],
    });
  });

  afterEach(() =>
    document.head.querySelectorAll('link[href$="monaco.css"]').forEach((link) => link.remove()),
  );

  function stylesheet(): HTMLLinkElement | null {
    return document.head.querySelector<HTMLLinkElement>('link[rel=stylesheet][href$="monaco.css"]');
  }

  it('loads Monaco once, with its stylesheet and the query language; its theme follows the colour scheme', async () => {
    const loader = TestBed.inject(MonacoLoader);
    const first = loader.load();
    const second = loader.load();
    expect(first).toBe(second);
    const link = stylesheet()!;
    expect(link.href).toBe(new URL('monaco.css', document.baseURI).href);
    link.dispatchEvent(new Event('load'));
    expect(await first).toBe(monaco.asMonaco());
    expect(imports).toBe(1);
    expect([...monaco.languages.registered.keys()]).toEqual(['gdq']);
    TestBed.tick();
    expect(monaco.theme).toBe('vs');
    dark.set(true);
    TestBed.tick();
    expect(monaco.theme).toBe('vs-dark');
    dark.set(false);
  });

  it('links its stylesheet once, though Monaco is loaded again after it failed', async () => {
    let failing = true;
    TestBed.overrideProvider(MONACO_IMPORT, {
      useValue: () =>
        failing ? Promise.reject(new Error('Offline')) : Promise.resolve(monaco.asMonaco()),
    });
    const loader = TestBed.inject(MonacoLoader);
    const first = loader.load();
    stylesheet()!.dispatchEvent(new Event('load'));
    await expect(first).rejects.toThrow('Offline');
    failing = false;
    expect(await loader.load()).toBe(monaco.asMonaco());
    expect(document.head.querySelectorAll('link[href$="monaco.css"]').length).toBe(1);
  });

  it('may be loaded again after a load failed', async () => {
    const loader = TestBed.inject(MonacoLoader);
    const failing = loader.load();
    stylesheet()!.dispatchEvent(new Event('error'));
    await expect(failing).rejects.toThrow("The editor's styles couldn't be loaded");
    expect(stylesheet()).toBeNull();

    const again = loader.load();
    expect(again).not.toBe(failing);
    stylesheet()!.dispatchEvent(new Event('load'));
    await again;
  });
});
