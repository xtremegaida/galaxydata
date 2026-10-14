import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { MatSelectHarness } from '@angular/material/select/testing';
import { MatSlideToggleHarness } from '@angular/material/slide-toggle/testing';
import { entityOf } from '../../../../../testing/catalog';
import { settle } from '../../../../../testing/http';
import { textOf } from '../../../../../testing/pages';
import type { Dimension, Measure } from '../../model/definition';
import { EntityCatalog, type EntityColumn } from '../entity-catalog';
import { DimensionEditor } from './dimension-editor';
import { MeasureList } from './measure-list';
import { NumberFormatEditor, keptFormat } from './number-format';

/** Rates, shares and durations: what a measure counts and divides by, and how its numbers (and yes and no) show. */
describe('measures that are rates, and how numbers show', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        EntityCatalog,
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => TestBed.resetTestingModule());

  const base = entityOf().columns[2];
  const column = (changes: Partial<EntityColumn>): EntityColumn => ({ ...base, ...changes });
  const sessions = entityOf({
    name: 'hw.session',
    columns: [
      column({
        name: 'bounced',
        label: 'Bounced',
        type: { kind: 'boolean', nullable: false, text: 'boolean' },
      }),
      column({ name: 'duration_ms', type: { kind: 'int64', nullable: false, text: 'int64' } }),
    ],
    navigations: [],
  });

  async function answer() {
    http.expectOne('/api/catalog/entity?name=hw.session').flush(sessions);
    await settle();
  }

  it('keeps a format as the server writes it: none for numbers as they are', () => {
    const plain = { decimals: null, prefix: '', suffix: null, compact: false };
    expect(keptFormat(plain)).toBeNull();
    expect(keptFormat({ ...plain, style: 'number', decimals: 2 })).toEqual({
      decimals: 2,
      prefix: null,
      suffix: null,
      compact: false,
    });
    expect(keptFormat({ ...plain, compact: true, style: 'duration' })).toEqual({
      decimals: null,
      prefix: null,
      suffix: null,
      compact: false,
      style: 'duration',
      unit: 'seconds',
    });
    expect(keptFormat({ ...plain, style: 'percent', unit: 'days' })).toEqual({
      decimals: null,
      prefix: null,
      suffix: null,
      compact: false,
      style: 'percent',
    });
  });

  it('shows numbers as percentages or durations, of a unit, with an example', async () => {
    const fixture = TestBed.createComponent(NumberFormatEditor);
    fixture.componentRef.setInput('format', null);
    fixture.detectChanges();
    const loader = TestbedHarnessEnvironment.loader(fixture);
    const hint = () => textOf((fixture.nativeElement as HTMLElement).querySelector('mat-hint'));
    expect(hint()).toBe('As 1,234.5678');

    const [style] = await loader.getAllHarnesses(MatSelectHarness);
    await style.clickOptions({ text: 'Percentages' });
    expect(fixture.componentInstance.format()).toEqual({
      decimals: null,
      prefix: null,
      suffix: null,
      compact: false,
      style: 'percent',
    });
    expect(hint()).toBe('As 25.7%');
    await (
      await loader.getHarness(MatInputHarness.with({ placeholder: 'As they are' }))
    ).setValue('0');
    expect(hint()).toBe('As 26%');

    await style.clickOptions({ text: 'Durations' });
    expect(fixture.componentInstance.format()).toMatchObject({
      decimals: null,
      style: 'duration',
      unit: 'seconds',
    });
    expect(hint()).toBe('As 2m 15s');
    expect(await loader.getAllHarnesses(MatSlideToggleHarness)).toEqual([]);
    const [, unit] = await loader.getAllHarnesses(MatSelectHarness);
    await unit.clickOptions({ text: 'Milliseconds' });
    expect(fixture.componentInstance.format()?.unit).toBe('milliseconds');
    expect(hint()).toBe('As 2m 15s');

    await style.clickOptions({ text: 'Numbers' });
    expect(fixture.componentInstance.format()).toBeNull();
  });

  it('averages a yes-or-no field as the share yes, shown as a percentage', async () => {
    const fixture = TestBed.createComponent(MeasureList);
    fixture.componentRef.setInput('entity', 'hw.session');
    fixture.componentRef.setInput('measures', [
      {
        aggregate: 'countValues',
        field: { path: [], column: 'bounced' },
        label: 'Count of bounced',
        format: null,
      },
    ] satisfies Measure[]);
    fixture.detectChanges();
    await answer();
    const loader = TestbedHarnessEnvironment.loader(fixture);
    const [aggregate] = await loader.getAllHarnesses(MatSelectHarness);
    await aggregate.clickOptions({ text: 'Average (of yes/no: the share yes)' });
    expect(fixture.componentInstance.measures()).toEqual([
      {
        aggregate: 'avg',
        field: { path: [], column: 'bounced' },
        label: 'Bounced (share)',
        format: { decimals: null, prefix: null, suffix: null, compact: false, style: 'percent' },
      },
    ]);
  });

  it('counts the rows its conditions keep, divided by another aggregate', async () => {
    const fixture = TestBed.createComponent(MeasureList);
    fixture.componentRef.setInput('entity', 'hw.session');
    fixture.componentRef.setInput('measures', [
      { aggregate: 'count', field: null, label: 'Bounce rate', format: null },
    ] satisfies Measure[]);
    fixture.detectChanges();
    await answer();
    const loader = TestbedHarnessEnvironment.loader(fixture);
    const measure = () => fixture.componentInstance.measures()[0];

    await (await loader.getHarness(MatButtonHarness.with({ text: /Only rows where/ }))).click();
    expect(measure().where).toEqual([
      { field: { path: [], column: '' }, value: { op: 'eq', value: '' } },
    ]);
    const divided = await loader.getHarness(
      MatSlideToggleHarness.with({ label: /Divided by another aggregate/ }),
    );
    await divided.toggle();
    expect(measure().per).toEqual({ aggregate: 'count', field: null });
    expect(
      await loader.getAllHarnesses(MatButtonHarness.with({ text: /Only rows where/ })),
    ).toHaveLength(2);
    const [, , perAggregate] = await loader.getAllHarnesses(MatSelectHarness);
    await perAggregate.clickOptions({ text: 'Count of distinct values' });
    expect(measure().per).toEqual({ aggregate: 'countDistinct', field: null });

    await (
      await loader.getHarness(
        MatButtonHarness.with({ selector: '[aria-label="Remove row condition 1"]' }),
      )
    ).click();
    expect(measure().where).toBeNull();
    await divided.toggle();
    expect(measure()).toEqual({
      aggregate: 'count',
      field: null,
      label: 'Bounce rate',
      format: null,
      where: null,
      per: null,
    });
  });

  it("labels a yes-or-no dimension's values, yes and no where they are left empty", async () => {
    const fixture = TestBed.createComponent(DimensionEditor);
    fixture.componentRef.setInput('entity', 'hw.session');
    fixture.componentRef.setInput('dimension', {
      field: { path: [], column: 'bounced' },
      bucket: null,
      label: 'Bounced',
    } satisfies Dimension);
    fixture.detectChanges();
    await answer();
    fixture.detectChanges();
    const loader = TestbedHarnessEnvironment.loader(fixture);
    const yes = await loader.getHarness(MatInputHarness.with({ placeholder: 'Yes' }));
    await yes.setValue('Bounced');
    expect(fixture.componentInstance.dimension().booleans).toEqual({ true: 'Bounced', false: '' });
    await yes.setValue('');
    expect(fixture.componentInstance.dimension().booleans).toBeNull();

    fixture.componentRef.setInput('dimension', {
      field: { path: [], column: 'duration_ms' },
      bucket: null,
      label: 'Duration',
    } satisfies Dimension);
    fixture.detectChanges();
    expect(await loader.getAllHarnesses(MatInputHarness.with({ placeholder: 'Yes' }))).toEqual([]);
  });
});
