import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatSelectHarness } from '@angular/material/select/testing';
import { MatSlideToggleHarness } from '@angular/material/slide-toggle/testing';
import { ConditionValueEditor } from './condition-editor';

/** A period relative to today, as a condition's (or a filter's default) value. */
describe('a period relative to today', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [{ provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } }],
    });
  });

  afterEach(() => TestBed.resetTestingModule());

  it("leaves today out of a period ending today, and not of one that doesn't", async () => {
    const fixture = TestBed.createComponent(ConditionValueEditor);
    fixture.componentRef.setInput('ops', ['relative']);
    fixture.componentRef.setInput('value', {
      op: 'relative',
      relative: { mode: 'last', count: 30, unit: 'day' },
    });
    fixture.detectChanges();
    const loader = TestbedHarnessEnvironment.loader(fixture);
    const value = () => fixture.componentInstance.value().relative;

    const toggle = await loader.getHarness(
      MatSlideToggleHarness.with({ label: /Up to yesterday/ }),
    );
    expect(await toggle.isChecked()).toBe(false);
    await toggle.toggle();
    expect(value()).toEqual({ mode: 'last', count: 30, unit: 'day', excludeToday: true });

    // Month to date, still up to yesterday.
    const [mode] = await loader.getAllHarnesses(MatSelectHarness.with({ ancestor: '.period' }));
    await mode.clickOptions({ text: 'To date: this' });
    expect(value()).toEqual({ mode: 'toDate', count: 30, unit: 'day', excludeToday: true });

    // The previous month doesn't end today: there's nothing to leave out.
    await mode.clickOptions({ text: 'The previous' });
    expect(value()).toEqual({ mode: 'previous', count: 30, unit: 'day' });
    expect(
      await loader.getAllHarnesses(MatSlideToggleHarness.with({ label: /Up to yesterday/ })),
    ).toEqual([]);

    await mode.clickOptions({ text: 'The last' });
    const again = await loader.getHarness(MatSlideToggleHarness.with({ label: /Up to yesterday/ }));
    await again.toggle();
    await again.toggle();
    expect(value()).toEqual({ mode: 'last', count: 30, unit: 'day' });
  });
});
