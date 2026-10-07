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
});
