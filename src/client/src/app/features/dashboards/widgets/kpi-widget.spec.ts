import { TestBed } from '@angular/core/testing';
import { rowsOf } from '../../../../testing/dashboards';
import type { KpiConfig, WidgetData } from '../model/definition';
import { WidgetContext } from '../model/widget-context';
import { kpiDefaults } from '../model/widget-defaults';
import { KpiWidget } from './kpi-widget';

const plain = (text: string | null | undefined) => (text ?? '').replace(/\s+/g, ' ').trim();

describe('a KPI', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [WidgetContext] }));

  afterEach(() => TestBed.resetTestingModule());

  const sessions: KpiConfig = {
    ...kpiDefaults('sessions'),
    measure: {
      aggregate: 'count',
      field: null,
      label: 'Sessions',
      format: { decimals: null, prefix: null, suffix: null, compact: true },
    },
    compare: { filter: 'period', good: 'up' },
    sparkline: { field: { path: [], column: 'first_seen' }, bucket: 'day', label: 'Day' },
  };

  /** Its answer: the number, and what else is given. */
  function answer(value: string | null, extra: Partial<WidgetData> = {}): WidgetData {
    const counted = rowsOf('Sessions', []).columns[1];
    return rowsOf('Sessions', [], {
      columns: [{ ...counted, label: 'Sessions', format: sessions.measure.format }],
      rows: [[value]],
      ...extra,
    });
  }

  /** A KPI widget showing `config`, titled or not, with its answer. */
  function show(config: KpiConfig, data: WidgetData, title: string | null = null) {
    const context = TestBed.inject(WidgetContext);
    context.show({ id: 'sessions', title, config });
    context.data.set(data);
    const fixture = TestBed.createComponent(KpiWidget);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { host, fixture, context };
  }

  it('shows its number in its format, under its measure when the widget has no title', () => {
    const { host } = show({ ...sessions, compare: null, sparkline: null }, answer('748123'));
    expect(plain(host.querySelector('.label')?.textContent)).toBe('Sessions');
    expect(plain(host.querySelector('.value')?.textContent)).toBe('748.1K');
    expect(host.querySelector('.value')?.getAttribute('title')).toBe('748,123');
    expect(host.querySelector('.change')).toBeNull();
    expect(host.querySelector('.trend')).toBeNull();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [WidgetContext] });
    const titled = show(
      { ...sessions, compare: null, sparkline: null },
      answer('748123'),
      'Visits',
    );
    expect(titled.host.querySelector('.label')).toBeNull();
  });

  it('says how it changed against the period before, coloured as that way is good or bad', () => {
    const previous = { value: '666800', from: '2026-08-15', to: '2026-09-13' };
    const { host } = show({ ...sessions, sparkline: null }, answer('748000', { previous }));
    const change = host.querySelector('.change')!;
    expect(change.classList.contains('good')).toBe(true);
    expect(plain(change.querySelector('mat-icon')?.textContent)).toBe('trending_up');
    const shown = [...change.querySelectorAll('.shown > span')].map((s) => plain(s.textContent));
    // The year is said once it isn't this one.
    expect(shown).toEqual([
      '+12.2%',
      '+81.2K',
      expect.stringMatching(/^vs Aug 15(, 2026)? – Sep 13(, 2026)?$/),
    ]);
    expect(change.querySelector('.shown')?.getAttribute('aria-hidden')).toBe('true');
    // Said in words: up or down, and whether that is for the better, not by colour alone.
    expect(plain(change.querySelector('.hidden')?.textContent)).toMatch(
      /^Up 12\.2% \(81\.2K\) on Aug 15(, 2026)? – Sep 13(, 2026)?, for the better$/,
    );
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [WidgetContext] });
    const fewer = show(
      { ...sessions, compare: { filter: 'period', good: 'down' }, sparkline: null },
      answer('748000', { previous }),
    );
    expect(fewer.host.querySelector('.change')!.classList.contains('bad')).toBe(true);
  });

  it('has no % against nothing before, and says when there was no number before', () => {
    const fromZero = show(
      { ...sessions, sparkline: null },
      answer('12', { previous: { value: '0', from: '2026-08-15', to: '2026-09-13' } }),
    );
    expect(fromZero.host.querySelector('.percent')).toBeNull();
    expect(plain(fromZero.host.querySelector('.amount')?.textContent)).toBe('+12');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [WidgetContext] });
    const none = show(
      { ...sessions, sparkline: null },
      answer('12', { previous: { value: null, from: '2026-08-15', to: '2026-08-15' } }),
    );
    expect(plain(none.host.querySelector('.change')?.textContent)).toMatch(
      /^Nothing to compare in Aug 15(, 2026)?$/,
    );
  });

  it('draws its sparkline, every day between, the latest marked and read by pointer or keyboard', () => {
    const points = [
      ['2026-10-10', '10'],
      ['2026-10-11', '30'],
      ['2026-10-13', '20'],
    ];
    const { host, fixture } = show({ ...sessions, compare: null }, answer('60', { points }));
    const trend = host.querySelector<HTMLElement>('.trend')!;
    expect(trend.getAttribute('aria-label')).toBe(
      'Sessions by day, 4 days, from 10 to 20. Use the arrow keys to read each.',
    );
    // The 12th had none: zero, as a count is.
    expect(host.querySelector('path')?.getAttribute('d')).toBe('M0 64 L33.33 8 L66.67 92 L100 36');
    const dot = host.querySelector<HTMLElement>('.dot')!;
    expect([dot.style.left, dot.style.top]).toEqual(['100%', '36%']);
    expect(host.querySelector('.hair')).toBeNull();
    expect([
      host.querySelector('.tip'),
      plain(host.querySelector('.readout')?.textContent),
    ]).toEqual([null, '']);

    const key = (name: string) => {
      trend.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true }));
      fixture.detectChanges();
    };
    key('ArrowLeft');
    expect(plain(host.querySelector('.readout')?.textContent)).toBe('Oct 12, 2026: 0');
    expect(host.querySelector<HTMLElement>('.hair')?.style.left).toMatch(/^66\.66\d*%$/);
    // A tip by the point says it too, kept within the sparkline.
    const tip = host.querySelector<HTMLElement>('.tip')!;
    expect([plain(tip.textContent), tip.style.left, tip.style.right]).toEqual([
      'Oct 12, 2026: 0',
      '',
      expect.stringMatching(/^33\.33\d*%$/),
    ]);
    key('Home');
    expect(plain(host.querySelector('.readout')?.textContent)).toBe('Oct 10, 2026: 10');
    key('ArrowLeft');
    expect(plain(host.querySelector('.readout')?.textContent)).toBe('Oct 10, 2026: 10');
    key('End');
    expect(plain(host.querySelector('.readout')?.textContent)).toBe('Oct 13, 2026: 20');

    vi.spyOn(trend, 'getBoundingClientRect').mockReturnValue({
      left: 100,
      width: 300,
      top: 0,
      height: 40,
    } as DOMRect);
    trend.dispatchEvent(new MouseEvent('pointermove', { clientX: 210, bubbles: true }));
    fixture.detectChanges();
    expect(plain(host.querySelector('.readout')?.textContent)).toBe('Oct 11, 2026: 30');
    trend.dispatchEvent(new MouseEvent('pointerleave'));
    fixture.detectChanges();
    expect(host.querySelector('.hair')).toBeNull();
  });

  it('says when its sparkline is the latest of more, and has none without points', () => {
    const latest = show(
      { ...sessions, compare: null },
      answer('60', { points: [['2026-10-13', '20']], truncated: true }),
    );
    expect(latest.host.querySelector('.trend')?.getAttribute('aria-label')).toBe(
      'Sessions by day, the latest 1 day, from 20 to 20. Use the arrow keys to read each.',
    );
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [WidgetContext] });
    const empty = show({ ...sessions, compare: null }, answer('0', { points: [] }));
    expect(empty.host.querySelector('.trend')).toBeNull();
  });
});
