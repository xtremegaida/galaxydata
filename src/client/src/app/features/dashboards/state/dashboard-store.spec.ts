import { salesDefinition, tabbedDefinition } from '../../../../testing/dashboards';
import { DashboardStore } from './dashboard-store';

describe("a dashboard's store", () => {
  it('shows the tab chosen when it is there, else the first; none without tabs', () => {
    const store = new DashboardStore();
    expect(store.tab()).toBeNull();
    expect([...store.shown()]).toEqual([]);
    store.definition.set(salesDefinition());
    expect([...store.shown()]).toEqual(['title', 'by-status', 'by-city']);
    store.definition.set(tabbedDefinition());
    expect([store.tab(), [...store.shown()]]).toEqual(['orders', ['title', 'by-status']]);
    store.chosenTab.set('customers');
    expect([store.tab(), [...store.shown()]]).toEqual(['customers', ['by-city']]);
    store.chosenTab.set('gone');
    expect(store.tab()).toBe('orders');
  });

  it("says when the shown widgets' rows were read, and forgets widgets that are gone", () => {
    const store = new DashboardStore();
    store.definition.set(tabbedDefinition());
    store.setRefreshedAt('by-status', '2026-03-01T10:00:00Z');
    store.setRefreshedAt('by-city', '2026-03-01T09:00:00Z');
    store.setFailing('by-city', true);
    expect([store.oldestRefresh(), store.failingShown().size]).toEqual(['2026-03-01T10:00:00Z', 0]);
    store.chosenTab.set('customers');
    expect([store.oldestRefresh(), store.failingShown().size]).toEqual(['2026-03-01T09:00:00Z', 1]);
    store.forget('by-city');
    expect(store.failing().has('by-city')).toBe(true);
    store.definition.set(salesDefinition({ widgets: salesDefinition().widgets.slice(0, 2) }));
    store.forget('by-city');
    expect([store.failing().has('by-city'), store.refreshedAt().has('by-city')]).toEqual([
      false,
      false,
    ]);
  });
});
