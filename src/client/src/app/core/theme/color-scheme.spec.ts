import { TestBed } from '@angular/core/testing';
import { ColorScheme, embedScheme } from './color-scheme';

/** The browser's dark-scheme query, changed as a test says. */
class SystemScheme extends EventTarget {
  constructor(public matches: boolean) {
    super();
  }

  change(dark: boolean): void {
    this.matches = dark;
    this.dispatchEvent(Object.assign(new Event('change'), { matches: dark }));
  }
}

describe('ColorScheme', () => {
  let system: SystemScheme | undefined;

  function schemeOf(dark = false): ColorScheme {
    system = new SystemScheme(dark);
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: (query: string) => (query === '(prefers-color-scheme: dark)' ? system : undefined),
    });
    return TestBed.inject(ColorScheme);
  }

  const page = () => document.documentElement.style.colorScheme;

  afterEach(() => {
    delete (window as { matchMedia?: unknown }).matchMedia;
    localStorage.clear();
    document.documentElement.style.colorScheme = '';
    vi.restoreAllMocks();
  });

  it('follows the system at first', () => {
    const scheme = schemeOf(true);
    expect(scheme.choice()).toBe('system');
    expect(scheme.dark()).toBe(true);
    expect(page()).toBe('light dark');
  });

  it('follows the system as it changes', () => {
    const scheme = schemeOf(false);
    expect(scheme.dark()).toBe(false);
    system?.change(true);
    expect(scheme.dark()).toBe(true);
  });

  it("shows the scheme chosen, whatever the system's", () => {
    const scheme = schemeOf(false);
    scheme.choose('dark');
    expect(scheme.dark()).toBe(true);
    expect(page()).toBe('dark');
    system?.change(true);
    scheme.choose('light');
    expect(scheme.dark()).toBe(false);
    expect(page()).toBe('light');
  });

  it('keeps the choice in the browser', () => {
    schemeOf().choose('dark');
    TestBed.resetTestingModule();
    document.documentElement.style.colorScheme = '';
    const scheme = schemeOf();
    expect(scheme.choice()).toBe('dark');
    expect(page()).toBe('dark');
  });

  it("takes what it doesn't know as the system's", () => {
    localStorage.setItem(ColorScheme.storageKey, 'sepia');
    expect(schemeOf().choice()).toBe('system');
  });

  it('holds a choice storage refuses', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Full', 'QuotaExceededError');
    });
    const scheme = schemeOf();
    scheme.choose('dark');
    expect(scheme.choice()).toBe('dark');
    expect(page()).toBe('dark');
  });

  it("gives an embedded dashboard its address's scheme, else the system's, whatever the application's", () => {
    localStorage.setItem(ColorScheme.storageKey, 'light');
    const scheme = schemeOf(true);
    embedScheme({ pathname: '/embed/abc', search: '?f.status=open&theme=dark' }, scheme);
    expect([scheme.dark(), page(), localStorage.getItem(ColorScheme.storageKey)]).toEqual([
      true,
      'dark',
      'light',
    ]);
    embedScheme({ pathname: '/embed/abc', search: '?theme=sepia' }, scheme);
    // The system's: dark here, though the application's choice is light.
    expect(scheme.dark()).toBe(true);
    embedScheme({ pathname: '/embed/abc', search: '?theme=light' }, scheme);
    expect(scheme.dark()).toBe(false);
    // Not an embed: the choice holds.
    const other = TestBed.inject(ColorScheme);
    other.override(null);
    embedScheme({ pathname: '/dashboards/1', search: '?theme=dark' }, other);
    expect(other.dark()).toBe(false);
  });

  it('shows a scheme while asked, keeping no choice', () => {
    const scheme = schemeOf(false);
    scheme.override('dark');
    expect([
      scheme.dark(),
      page(),
      scheme.choice(),
      localStorage.getItem(ColorScheme.storageKey),
    ]).toEqual([true, 'dark', 'system', null]);
    scheme.choose('light');
    expect(scheme.dark()).toBe(true);
    scheme.override(null);
    expect([scheme.dark(), page()]).toEqual([false, 'light']);
  });

  it("is light for the system's where the browser can't tell", () => {
    const scheme = TestBed.inject(ColorScheme);
    expect(scheme.dark()).toBe(false);
    expect(page()).toBe('light dark');
  });
});
