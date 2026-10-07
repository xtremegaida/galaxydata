import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { entityOf } from '../../../../../testing/catalog';
import { settle } from '../../../../../testing/http';
import { textOf } from '../../../../../testing/pages';
import { EntityCatalog } from '../entity-catalog';
import { type ChosenField, FieldPicker } from './field-picker';

describe("a source's field, picked", () => {
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

  const customers = entityOf({
    name: 'shop.customers',
    columns: [
      { ...entityOf().columns[0], name: 'id', label: null },
      {
        ...entityOf().columns[2],
        name: 'city',
        label: null,
        type: { kind: 'string', nullable: true, text: 'string?' },
      },
      {
        ...entityOf().columns[0],
        name: 'credit_limit',
        label: 'Credit',
        type: { kind: 'decimal', nullable: true, text: 'decimal(10,2)?' },
      },
    ],
    navigations: [],
  });

  async function render(use: 'any' | 'number' = 'any') {
    const fixture = TestBed.createComponent(FieldPicker);
    fixture.componentRef.setInput('entity', 'shop.orders');
    fixture.componentRef.setInput('use', use);
    const chosen: ChosenField[] = [];
    fixture.componentInstance.chosen.subscribe((c) => chosen.push(c));
    fixture.detectChanges();
    const input = (fixture.nativeElement as HTMLElement).querySelector('input')!;
    const type = async (text: string) => {
      input.focus();
      input.value = text;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      fixture.detectChanges();
      await settle();
      fixture.detectChanges();
    };
    const options = () => [...document.querySelectorAll<HTMLElement>('mat-option')];
    return { fixture, input, type, options, chosen };
  }

  /** Answers the entity asked for. */
  function answer(name: string, entity = entityOf()) {
    http.expectOne(`/api/catalog/entity?name=${name}`).flush(entity);
  }

  it('steps through navigations to one row, then picks a column there', async () => {
    const { fixture, type, options, chosen } = await render();
    answer('shop.orders');
    await type('cus');
    expect(options().map((o) => textOf(o))).toEqual([
      'customer_id Customer · int64',
      'customer. to shop.customers',
    ]);
    // Navigations to many rows aren't fields.
    expect(options().some((o) => textOf(o).startsWith('order_lines'))).toBe(false);
    options()[1].click();
    await settle();
    answer('shop.customers', customers);
    await settle();
    fixture.detectChanges();
    // The panel opens again on the entity it leads to.
    expect(options().map((o) => textOf(o))).toEqual([
      'customer.id int64',
      'customer.city string?',
      'customer.credit_limit Credit · decimal(10,2)?',
    ]);
    options()[1].click();
    fixture.detectChanges();
    expect(fixture.componentInstance.field()).toEqual({ path: ['customer'], column: 'city' });
    expect(chosen.map((c) => c.label)).toEqual(['City']);
  });

  it('suggests the types a use allows, and says when what is typed is no field', async () => {
    const { fixture, type, options, input } = await render('number');
    answer('shop.orders');
    await type('');
    expect(options().map((o) => textOf(o).split(' ')[0])).toEqual([
      'id',
      'customer_id',
      'customer.',
    ]);
    await type('nothing');
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(fixture.componentInstance.field()).toBeNull();
    expect(textOf((fixture.nativeElement as HTMLElement).querySelector('.problem'))).toBe(
      'Not a column this source has (or leads to by its navigations to one)',
    );
  });
});
