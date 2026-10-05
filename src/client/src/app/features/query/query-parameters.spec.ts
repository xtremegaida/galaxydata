import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import type { ParameterValues } from '../../core/query/query-url';
import { textOf } from '../../../testing/pages';
import {
  QueryParameters,
  type UsedParameter,
  kindOf,
  parameterInputOf,
  given,
  parameterProblemOf,
  valueText,
  withValue,
} from './query-parameters';

describe("parameters' values", () => {
  it('are of the type the query takes for them, by its kind', () => {
    expect(kindOf('decimal(10,2)?')).toBe('decimal');
    expect(kindOf('string(20,ansi)')).toBe('string');
    expect(kindOf('date')).toBe('date');
    expect(kindOf(null)).toBeNull();
    expect(parameterInputOf('min', ' 50.5 ', 'decimal(10,2)')).toEqual({
      name: 'min',
      type: 'decimal',
      value: '50.5',
    });
    expect(parameterInputOf('since', '2026-01-01', 'date')).toEqual({
      name: 'since',
      type: 'date',
      value: '2026-01-01',
    });
    // Text as typed, spaces and all.
    expect(parameterInputOf('s', ' 5 ', 'string(20)')).toEqual({
      name: 's',
      type: 'string',
      value: ' 5 ',
    });
    expect(parameterInputOf('s', null, 'string(20)')).toEqual({ name: 's', value: null });
  });

  it('are typed as the command line types them, when the query takes no type', () => {
    expect(parameterInputOf('n', '42', null)).toEqual({ name: 'n', type: 'int64', value: '42' });
    expect(parameterInputOf('n', '-9223372036854775808', null)).toMatchObject({ type: 'int64' });
    // Past 64 bits, a decimal.
    expect(parameterInputOf('n', '9223372036854775808', null)).toMatchObject({ type: 'decimal' });
    expect(parameterInputOf('n', '2.50', 'unknown')).toEqual({
      name: 'n',
      type: 'decimal',
      value: '2.50',
    });
    expect(parameterInputOf('b', 'TRUE', null)).toEqual({
      name: 'b',
      type: 'boolean',
      value: 'true',
    });
    expect(parameterInputOf('b', 'false', null)).toEqual({
      name: 'b',
      type: 'boolean',
      value: 'false',
    });
    expect(parameterInputOf('x', 'null', null)).toEqual({ name: 'x', value: null });
    expect(parameterInputOf('x', '2026-01-01', null)).toEqual({ name: 'x', value: '2026-01-01' });
    // Quoted, text.
    expect(parameterInputOf('x', "'42'", null)).toEqual({ name: 'x', value: '42' });
    expect(parameterInputOf('x', '"null"', null)).toEqual({ name: 'x', value: 'null' });
    expect(parameterInputOf('x', "'", null)).toEqual({ name: 'x', value: "'" });
  });

  it('are given when typed, or NULL', () => {
    const values = { min: '5', since: null, none: '' };
    expect(
      ['min', 'since', 'none', 'other', 'constructor'].map((name) => given(values, name)),
    ).toEqual([true, true, false, false, false]);
  });

  it("say what is wrong with them for the query's types", () => {
    expect(parameterProblemOf('min', 'abc', 'decimal(10,2)')).toBe("abc isn't a number");
    // A parameter takes any precision.
    expect(parameterProblemOf('min', '1.555', 'decimal(10,2)')).toBeNull();
    expect(parameterProblemOf('since', '2026-02-30', 'date')).toBe(
      "2026-02-30 isn't a date (yyyy-mm-dd)",
    );
    expect(parameterProblemOf('n', '70000', 'int16')).toBe(
      '70000 is out of range (-32768 to 32767)',
    );
    expect(parameterProblemOf('b', 'yes', 'boolean?')).toBe("yes isn't true or false");
    expect(parameterProblemOf('s', 'anything', 'string')).toBeNull();
    expect(parameterProblemOf('x', 'anything', null)).toBeNull();
    expect(parameterProblemOf('x', 'anything', 'unknown')).toBeNull();
    expect(parameterProblemOf('min', null, 'decimal')).toBeNull();
    expect(parameterProblemOf('min', undefined, 'decimal')).toBeNull();
    expect(parameterProblemOf('min', '', 'decimal')).toBeNull();
  });

  it('are kept as own properties, whatever their names', () => {
    const values = withValue(withValue({}, '__proto__', 'x'), 'a', null);
    expect(Object.getPrototypeOf(values)).toBe(Object.prototype);
    expect(values).toEqual(
      Object.fromEntries([
        ['__proto__', 'x'],
        ['a', null],
      ]),
    );
    expect(withValue(values, '__proto__', undefined)).toEqual({ a: null });
  });

  it('are held as text, from those saved', () => {
    expect(valueText('x')).toBe('x');
    expect(valueText(50)).toBe('50');
    expect(valueText(true)).toBe('true');
    expect(valueText(null)).toBeNull();
    expect(valueText(undefined)).toBeNull();
  });
});

@Component({
  imports: [QueryParameters],
  template: `<gd-query-parameters [used]="used()" [problems]="problems()" [(values)]="values" />`,
})
class Host {
  readonly used = signal<UsedParameter[]>([
    { name: 'min', given: false, type: 'decimal(10,2)' },
    { name: 'since', given: false, type: 'date' },
    { name: 'x', given: false, type: null },
  ]);
  readonly problems = signal<Record<string, string>>({});
  readonly values = signal<ParameterValues>({ min: '50' });
}

/** A field's label and hint. */
function fieldText(field: Element): string {
  return `${textOf(field.querySelector('mat-label'))}: ${textOf(field.querySelector('mat-hint'))}`;
}

describe('QueryParameters', () => {
  function opened() {
    TestBed.configureTestingModule({
      providers: [{ provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } }],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const input = (name: string) =>
      element.querySelector<HTMLInputElement>(`input[data-parameter="${name}"]`)!;
    return { fixture, host: fixture.componentInstance, element, input };
  }

  it("asks for each parameter's value, saying its type and how it is written", () => {
    const { element, input } = opened();
    expect(element.querySelector('[role=group]')?.getAttribute('aria-label')).toBe('Parameters');
    const fields = [...element.querySelectorAll('mat-form-field')].map(fieldText);
    expect(fields).toEqual(['$min: decimal', '$since: date', '$x: typed as written']);
    expect(input('min').value).toBe('50');
    expect(input('since').placeholder).toBe('yyyy-mm-dd');
    expect(input('x').placeholder).toBe('');
  });

  it('takes the values typed, NULL, and none', () => {
    const { fixture, host, input, element } = opened();
    input('since').value = '2026-01-01';
    input('since').dispatchEvent(new Event('input'));
    expect(host.values()).toEqual({ min: '50', since: '2026-01-01' });
    input('min').value = '';
    input('min').dispatchEvent(new Event('input'));
    expect(host.values()).toEqual({ since: '2026-01-01' });

    const nulls = element.querySelectorAll<HTMLInputElement>('mat-checkbox input');
    nulls[0].click();
    fixture.detectChanges();
    expect(host.values()).toEqual({ since: '2026-01-01', min: null });
    expect(input('min').disabled).toBe(true);
    nulls[0].click();
    fixture.detectChanges();
    expect(host.values()).toEqual({ since: '2026-01-01' });
    expect(input('min').disabled).toBe(false);
  });

  it("names each NULL by its parameter, gives back what was typed before it, and shows no value of objects' own", () => {
    const { fixture, host, input, element } = opened();
    const nulls = element.querySelectorAll<HTMLInputElement>('mat-checkbox input');
    expect(nulls[0].getAttribute('aria-label')).toBe('$min is NULL');
    nulls[0].click();
    fixture.detectChanges();
    nulls[0].click();
    fixture.detectChanges();
    expect(host.values()).toEqual({ min: '50' });
    expect(input('min').value).toBe('50');

    host.used.set([{ name: 'constructor', given: false, type: null }]);
    fixture.detectChanges();
    expect(input('constructor').value).toBe('');
    expect(input('constructor').disabled).toBe(false);
  });

  it('says what is wrong with a value at its field', () => {
    const { fixture, host, input, element } = opened();
    host.problems.set({ min: "abc isn't a number" });
    fixture.detectChanges();
    expect(input('min').getAttribute('aria-invalid')).toBe('true');
    expect(fieldText(element.querySelector('mat-form-field')!)).toBe("$min: abc isn't a number");
    expect(element.querySelector('mat-hint')?.classList).toContain('problem');
    expect(input('since').getAttribute('aria-invalid')).toBe('false');
  });

  it('asks for nothing when the query takes no parameters', () => {
    const { fixture, host, element } = opened();
    host.used.set([]);
    fixture.detectChanges();
    expect(element.querySelector('[role=group]')).toBeNull();
  });
});
