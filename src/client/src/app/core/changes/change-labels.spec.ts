import { changeOf, insertOf } from '../../../testing/changes';
import { issueText, rowLabelOf, valueText } from './change-labels';

describe('change labels', () => {
  it("names rows by their keys, and new rows by their places among their entity's", () => {
    const changes = [
      insertOf('t1', { id: 1 }),
      changeOf({ id: 2, key: ['7', 'A'], rowId: '["7","A"]' }),
      insertOf('t2', { id: 3, entity: 'shop.customers' }),
      insertOf('t3', { id: 4 }),
    ];
    expect(changes.map((change) => rowLabelOf(change, changes))).toEqual([
      'New row 1',
      'Row 7, A',
      'New row 1',
      'New row 2',
    ]);
  });

  it('says values as text', () => {
    expect(valueText(null)).toBe('NULL');
    expect(valueText(undefined)).toBe('NULL');
    expect(valueText(3)).toBe('3');
    expect(valueText('2026-03-01T10:30:00')).toBe('2026-03-01 10:30:00');
    expect(valueText('T-shirt')).toBe('T-shirt');
  });

  it('names the column of an issue, unless it says it', () => {
    expect(issueText({ change: 1, column: 'qty', message: 'Too large' })).toBe('qty: Too large');
    expect(issueText({ change: 1, column: 'QTY', message: "'qty' is too large" })).toBe(
      "'qty' is too large",
    );
    expect(issueText({ change: 1, column: null, message: 'Gone' })).toBe('Gone');
  });
});
