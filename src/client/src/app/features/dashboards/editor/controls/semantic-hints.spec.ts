import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { entityOf } from '../../../../../testing/catalog';
import { settle } from '../../../../../testing/http';
import { textOf } from '../../../../../testing/pages';
import type { ConditionValue } from '../../model/definition';
import { barDefaults, lineDefaults, overTime, rowCount } from '../../model/widget-defaults';
import { EntityCatalog, type EntityColumn } from '../entity-catalog';
import { ConditionValueEditor } from './condition-editor';
import { FieldPicker } from './field-picker';
import { MeasureList, suggestedMeasures } from './measure-list';

/** What a source says of its columns (a HyperWhirlpool warehouse's model, say), as the dashboard editor uses it. */
describe('what a source says of its columns', () => {
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
    qualifiedName: 'hw.web.session',
    timeColumn: 'first_seen',
    columns: [
      column({
        name: 'first_seen',
        type: { kind: 'dateTimeOffset', nullable: false, text: 'datetimeoffset' },
      }),
      column({
        name: 'page_views',
        type: { kind: 'decimal', nullable: false, text: 'decimal(20,0)' },
        comment: 'Pages viewed in the visit.',
        aggregate: 'sum',
      }),
      column({ name: 'longest', label: 'Longest page', aggregate: 'max' }),
      column({ name: 'events', aggregate: 'sum', hidden: true }),
      column({
        name: 'bot',
        type: { kind: 'boolean', nullable: false, text: 'boolean' },
        aggregate: 'max',
      }),
      column({ name: 'traffic_type', values: ['paid', 'search', 'direct'] }),
    ],
    navigations: [],
  });

  function answer() {
    http.expectOne('/api/catalog/entity?name=hw.session').flush(sessions);
  }

  it('suggests the measures its columns sum up to, as they say they do', () => {
    expect(suggestedMeasures(sessions.columns)).toEqual([
      {
        aggregate: 'sum',
        field: { path: [], column: 'page_views' },
        label: 'Sum of page views',
        format: null,
      },
      {
        aggregate: 'max',
        field: { path: [], column: 'longest' },
        label: 'Greatest longest page',
        format: null,
      },
    ]);
  });

  it('offers them in place of a count of rows', async () => {
    const fixture = TestBed.createComponent(MeasureList);
    fixture.componentRef.setInput('entity', 'hw.session');
    fixture.componentRef.setInput('measures', [rowCount()]);
    fixture.detectChanges();
    answer();
    await settle();
    fixture.detectChanges();
    const suggestions = () => [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('.suggested button'),
    ];
    expect(suggestions().map((b) => textOf(b))).toEqual([
      'Sum of page views',
      'Greatest longest page',
    ]);
    suggestions()[0].click();
    fixture.detectChanges();
    expect(fixture.componentInstance.measures()).toEqual([
      {
        aggregate: 'sum',
        field: { path: [], column: 'page_views' },
        label: 'Sum of page views',
        format: null,
      },
    ]);
    // A measure that isn't a count of rows is the one wanted.
    expect(suggestions()).toEqual([]);
  });

  it('tells what a chosen field holds', async () => {
    const fixture = TestBed.createComponent(FieldPicker);
    fixture.componentRef.setInput('entity', 'hw.session');
    fixture.componentRef.setInput('field', { path: [], column: 'page_views' });
    fixture.detectChanges();
    answer();
    await settle();
    fixture.detectChanges();
    expect(textOf((fixture.nativeElement as HTMLElement).querySelector('mat-hint'))).toBe(
      'Pages viewed in the visit.',
    );
  });

  describe("a condition's values", () => {
    function render(value: ConditionValue) {
      const fixture = TestBed.createComponent(ConditionValueEditor);
      fixture.componentRef.setInput('known', ['paid', 'search', 'direct']);
      fixture.componentRef.setInput('value', value);
      fixture.detectChanges();
      return fixture;
    }

    function choices(fixture: ReturnType<typeof render>, index: number): string[] {
      const selects = (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(
        '.mat-mdc-select-trigger',
      );
      selects[index].click();
      fixture.detectChanges();
      return [...document.querySelectorAll<HTMLElement>('mat-option')].map((o) => textOf(o));
    }

    it('are picked from those the field is known to take, and those it had', () => {
      const fixture = render({ op: 'in', values: ['search', 'email'] });
      // The op's select, then the values'.
      expect(choices(fixture, 1)).toEqual(['paid', 'search', 'direct', 'email']);
    });

    it('are typed for an op that matches text', () => {
      const fixture = render({ op: 'contains', value: 'se' });
      const element = fixture.nativeElement as HTMLElement;
      expect(element.querySelectorAll('mat-select').length).toBe(1);
      expect(element.querySelector('input')!.value).toBe('se');
    });
  });

  it("draws a new line chart over the time its source's rows are dated by", () => {
    const time = { column: 'first_seen', label: 'First seen' };
    expect(overTime(lineDefaults('s1'), time)).toEqual({
      ...lineDefaults('s1'),
      dimension: {
        field: { path: [], column: 'first_seen' },
        bucket: 'month',
        label: 'First seen',
      },
    });
    expect(overTime(lineDefaults('s1'), null)).toEqual(lineDefaults('s1'));
    expect(overTime(barDefaults('s1'), time)).toEqual(barDefaults('s1'));
  });
});
