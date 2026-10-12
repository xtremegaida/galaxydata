import { TestBed } from '@angular/core/testing';
import { FakeResizeObserver } from '../../../../testing/resize';
import type { HeadingConfig } from '../model/definition';
import { WidgetContext } from '../model/widget-context';
import { HeadingWidget, linesThatFit } from './heading-widget';

describe('a heading', () => {
  beforeEach(() => {
    FakeResizeObserver.install();
    TestBed.configureTestingModule({ providers: [WidgetContext] });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    FakeResizeObserver.uninstall();
    TestBed.resetTestingModule();
  });

  /** A heading widget showing `config`. */
  function show(config: Partial<HeadingConfig>, editing = false) {
    const context = TestBed.inject(WidgetContext);
    context.editing.set(editing);
    const shown = (more: Partial<HeadingConfig>) =>
      context.show({
        id: 'heading',
        title: null,
        config: { kind: 'heading', text: 'Sales', size: 'large', align: 'start', ...more },
      });
    shown(config);
    const fixture = TestBed.createComponent(HeadingWidget);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return {
      host,
      update(more: Partial<HeadingConfig>) {
        shown({ ...config, ...more });
        fixture.detectChanges();
      },
      resize(height: number) {
        FakeResizeObserver.resize(host, 600, height);
        fixture.detectChanges();
      },
    };
  }

  it('fits as many lines as its height holds, one at least', () => {
    expect(linesThatFit(40, 36)).toBe(1);
    expect(linesThatFit(20, 36)).toBe(1);
    expect(linesThatFit(80, 36)).toBe(2);
    expect(linesThatFit(71.5, 36)).toBe(2);
    expect(linesThatFit(null, 36)).toBeNull();
    expect(linesThatFit(0, 36)).toBeNull();
    expect(linesThatFit(40, Number.NaN)).toBeNull();
  });

  it("is the page's heading of its size's level, aligned as chosen", () => {
    const { host, update } = show({ text: '  Sales overview ' });
    expect(host.querySelector('h2.large')?.textContent).toBe('Sales overview');
    expect(host.style.textAlign).toBe('start');
    update({ size: 'medium', align: 'center' });
    expect([host.querySelector('h2'), host.querySelector('h3')?.textContent]).toEqual([
      null,
      'Sales overview',
    ]);
    expect(host.style.textAlign).toBe('center');
    update({ size: 'small', align: 'end' });
    expect(host.querySelector('h4')?.textContent).toBe('Sales overview');
    expect(host.style.textAlign).toBe('end');
  });

  it('wraps onto the lines that fit, and no more, measured once it is there', () => {
    // jsdom lays nothing out: its headings are given the line height a page gives them.
    const computed = window.getComputedStyle.bind(window);
    vi.spyOn(window, 'getComputedStyle').mockImplementation((element, pseudo) => {
      const style = computed(element, pseudo);
      return element.classList.contains('heading')
        ? ({ ...style, lineHeight: '36px' } as CSSStyleDeclaration)
        : style;
    });
    const { host, resize, update } = show({ text: '' }, true);
    const lines = () =>
      host.querySelector<HTMLElement>('.heading')!.style.getPropertyValue('--gd-lines');
    // Its height is known before it has a heading to measure, as a page lays it out.
    resize(40);
    update({ text: 'Sales' });
    expect(lines()).toBe('1');
    resize(80);
    expect(lines()).toBe('2');
    // Another size is another element, measured in turn.
    update({ text: 'Sales', size: 'small' });
    expect([host.querySelector('h4') !== null, lines()]).toEqual([true, '2']);
  });

  it('says what to do while it has no text, only to its editor', () => {
    expect(show({ text: ' ' }).host.textContent?.trim()).toBe('');
    TestBed.resetTestingModule();
    FakeResizeObserver.install();
    TestBed.configureTestingModule({ providers: [WidgetContext] });
    const { host } = show({ text: '' }, true);
    expect(host.querySelector('h2, h3, h4')).toBeNull();
    expect(host.textContent?.trim()).toBe("Write the heading in the widget's settings.");
  });
});
