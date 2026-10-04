import { Injector, runInInjectionContext, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { debounced } from './debounced';

describe('debounced', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("gives the source's value once it has stayed the same for the wait", () => {
    const typed = signal('a');
    const value = runInInjectionContext(TestBed.inject(Injector), () => debounced(typed, 250));
    expect(value()).toBe('a');
    TestBed.tick();
    typed.set('ab');
    TestBed.tick();
    vi.advanceTimersByTime(200);
    typed.set('abc');
    TestBed.tick();
    vi.advanceTimersByTime(200);
    expect(value()).toBe('a');
    vi.advanceTimersByTime(50);
    expect(value()).toBe('abc');
  });
});
