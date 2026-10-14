import { TestBed } from '@angular/core/testing';
import { rowsOf } from '../../../../testing/dashboards';
import { textOf } from '../../../../testing/pages';
import type { WidgetData } from '../model/definition';
import { barDefaults } from '../model/widget-defaults';
import { RowsTable } from './rows-table';

describe("a widget's rows as a table", () => {
  function render(data: WidgetData) {
    const fixture = TestBed.createComponent(RowsTable);
    fixture.componentRef.setInput('data', data);
    fixture.componentRef.setInput('config', barDefaults('orders'));
    fixture.detectChanges();
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr')].map((row) =>
      [...row.querySelectorAll('td')].map((cell) => textOf(cell)),
    );
  }

  const decimal = { kind: 'decimal', nullable: false, text: 'decimal(10,2)', scale: 2 } as const;

  it("keeps a decimal's scale, so amounts in a column read alike", () => {
    const data = rowsOf('Status', [
      ['open', '250.00'],
      ['shipped', '99.5'],
    ]);
    data.columns[1] = { ...data.columns[1], label: 'Revenue', type: decimal };
    expect(render(data)).toEqual([
      ['open', '250.00'],
      ['shipped', '99.50'],
    ]);
  });

  it("writes numbers as the measure's format says, before its type's scale", () => {
    const data = rowsOf('Status', [['open', '250.00']]);
    data.columns[1] = {
      ...data.columns[1],
      type: decimal,
      format: { decimals: 0, prefix: 'R ', suffix: null, compact: false },
    };
    expect(render(data)).toEqual([['open', 'R 250']]);
  });

  it('shows rates as percentages, durations in their parts, and yes and no by their labels', () => {
    const boolean = { kind: 'boolean', nullable: true, text: 'boolean?' } as const;
    const data = rowsOf('Bounced', [], {
      rows: [
        [true, 0.6, 473000],
        [false, 0.25, 59000],
        [null, null, null],
      ],
    });
    data.columns[0] = { ...data.columns[0], type: boolean };
    data.columns[1] = {
      ...data.columns[1],
      format: { decimals: null, prefix: null, suffix: null, compact: false, style: 'percent' },
    };
    data.columns.push({
      ...data.columns[1],
      name: 'm1',
      index: 1,
      format: {
        decimals: null,
        prefix: null,
        suffix: null,
        compact: false,
        style: 'duration',
        unit: 'milliseconds',
      },
    });
    const fixture = TestBed.createComponent(RowsTable);
    fixture.componentRef.setInput('data', data);
    const config = barDefaults('sessions');
    fixture.componentRef.setInput('config', {
      ...config,
      dimension: { ...config.dimension, booleans: { true: 'Bounced', false: 'Went on' } },
    });
    fixture.detectChanges();
    expect(
      [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr')].map((row) =>
        [...row.querySelectorAll('td')].map((cell) => textOf(cell)),
      ),
    ).toEqual([
      ['Bounced', '60%', '7m 53s'],
      ['Went on', '25%', '59s'],
      ['(no value)', '—', '—'],
    ]);
  });

  it("shows a chart's colours beside the cells (and headers) they are of, for eyes alone", () => {
    const fixture = TestBed.createComponent(RowsTable);
    fixture.componentRef.setInput(
      'data',
      rowsOf('Status', [
        ['open', '2'],
        ['shipped', '1'],
      ]),
    );
    fixture.componentRef.setInput('config', barDefaults('orders'));
    fixture.componentRef.setInput(
      'swatch',
      (row: readonly unknown[] | null, column: { role: string }) =>
        row === null
          ? column.role === 'measure'
            ? '#123456'
            : null
          : row[0] === 'open' && column.role === 'dimension'
            ? '#abcdef'
            : null,
    );
    fixture.detectChanges();
    const table = fixture.nativeElement as HTMLElement;
    const swatches = [...table.querySelectorAll<HTMLElement>('.swatch')];
    expect(swatches.map((s) => [s.closest('th, td')!.tagName, s.style.background])).toEqual([
      ['TH', 'rgb(18, 52, 86)'],
      ['TD', 'rgb(171, 205, 239)'],
    ]);
    expect(swatches.every((s) => s.getAttribute('aria-hidden') === 'true')).toBe(true);
    expect(textOf(table.querySelector('tbody td')!)).toBe('open');
  });

  it("shows a chart's Other last, not chosen, with a button that steps into it", () => {
    const fixture = TestBed.createComponent(RowsTable);
    fixture.componentRef.setInput('data', rowsOf('City', [['Cape Town', '5']]));
    fixture.componentRef.setInput('config', barDefaults('customers'));
    let stepped = 0;
    fixture.componentRef.setInput('other', {
      rows: [[null, '3']],
      column: 0,
      name: 'Other',
      color: '#999999',
      step: () => stepped++,
      stepLabel: 'Show the next 10 slices',
    });
    fixture.detectChanges();
    const table = fixture.nativeElement as HTMLElement;
    const rows = [...table.querySelectorAll('tbody tr')];
    expect(rows.map((row) => [...row.querySelectorAll('td')].map((cell) => textOf(cell)))).toEqual([
      ['Cape Town', '5', ''],
      ['Other', '3', 'arrow_forward'],
    ]);
    expect(rows[1].querySelectorAll('[aria-label^="Choose"]')).toHaveLength(0);
    rows[1].querySelector<HTMLButtonElement>('[aria-label="Show the next 10 slices"]')!.click();
    expect(stepped).toBe(1);

    // At its last step, it can't be stepped into: no button, nor a column for one.
    fixture.componentRef.setInput('other', {
      rows: [[null, '3']],
      column: 0,
      name: 'Other',
      color: '#999999',
      step: null,
      stepLabel: 'Show the next 10 slices',
    });
    fixture.detectChanges();
    expect(table.querySelectorAll('button')).toHaveLength(0);
    expect(table.querySelectorAll('th.actions')).toHaveLength(0);
  });
});
