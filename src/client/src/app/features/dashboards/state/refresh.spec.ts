import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { DashboardStore } from './dashboard-store';
import { REFRESH_CLOCK, type RefreshPolicy, refreshOnTimer } from './refresh';

describe('refreshing on a timer', () => {
  let now = 0;
  let store: DashboardStore;
  const policy = signal<RefreshPolicy | null>({ mode: 'interval', seconds: 30 });

  function hidden(value: boolean): void {
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      get: () => (value ? 'hidden' : 'visible'),
    });
    document.dispatchEvent(new Event('visibilitychange'));
  }

  async function wait(ms: number): Promise<void> {
    now += ms;
    await vi.advanceTimersByTimeAsync(ms);
    TestBed.tick();
  }

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    now = 0;
    policy.set({ mode: 'interval', seconds: 30 });
    TestBed.configureTestingModule({
      providers: [DashboardStore, { provide: REFRESH_CLOCK, useValue: () => now }],
    });
    store = TestBed.inject(DashboardStore);
    TestBed.runInInjectionContext(() => refreshOnTimer(store, policy));
    TestBed.tick();
  });

  afterEach(() => {
    vi.useRealTimers();
    delete (document as unknown as { visibilityState?: string }).visibilityState;
    TestBed.resetTestingModule();
  });

  it('refreshes every interval', async () => {
    await wait(29_000);
    expect(store.refreshes()).toBe(0);
    await wait(1_000);
    expect(store.refreshes()).toBe(1);
    await wait(30_000);
    expect(store.refreshes()).toBe(2);
  });

  it("doesn't while widgets are reading, so refreshes don't pile up", async () => {
    store.setLoading('a', true);
    TestBed.tick();
    await wait(90_000);
    expect(store.refreshes()).toBe(0);
    store.setLoading('a', false);
    TestBed.tick();
    await wait(0);
    expect(store.refreshes()).toBe(1);
  });

  it("doesn't while the page isn't shown, and does at once when shown again late", async () => {
    hidden(true);
    TestBed.tick();
    await wait(120_000);
    expect(store.refreshes()).toBe(0);
    hidden(false);
    TestBed.tick();
    await wait(0);
    expect(store.refreshes()).toBe(1);
  });

  it('waits longer after rows that couldn’t be read', async () => {
    store.setFailing('a', true);
    TestBed.tick();
    await wait(30_000);
    expect(store.refreshes()).toBe(1);
    store.setFailing('a', false);
    store.setFailing('a', true);
    TestBed.tick();
    await wait(30_000);
    expect(store.refreshes()).toBe(1);
    await wait(30_000);
    expect(store.refreshes()).toBe(2);
  });

  it("doesn't for a dashboard refreshed by hand", async () => {
    policy.set({ mode: 'manual', seconds: null });
    TestBed.tick();
    await wait(600_000);
    expect(store.refreshes()).toBe(0);
  });
});
