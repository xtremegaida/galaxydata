import type { ChangeIssue, PendingChange } from './pending-changes';

/**
 * How a change's row is named: "Row 1001, 2" (its key's values), or "New row 2" (its place among its entity's new
 * rows, in the order they were made).
 */
export function rowLabelOf(change: PendingChange, changes: readonly PendingChange[]): string {
  if (change.kind === 'insert') {
    const inserts = changes.filter(
      (each) => each.entity === change.entity && each.kind === 'insert',
    );
    return `New row ${inserts.findIndex((each) => each.id === change.id) + 1}`;
  }
  return `Row ${(change.key ?? []).map((value) => valueText(value)).join(', ')}`;
}

/** What is wrong with a change, in a sentence: its column named first, unless the sentence names it. */
export function issueText({ column, message }: ChangeIssue): string {
  return column && !message.toLowerCase().includes(`'${column.toLowerCase()}'`)
    ? `${column}: ${message}`
    : message;
}

/** A value as sent, as text: NULL for null, date-times with a space. */
export function valueText(value: unknown): string {
  if (value === null || value === undefined) {
    return 'NULL';
  }
  if (typeof value !== 'string') {
    return JSON.stringify(value);
  }
  return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(value) ? value.replace('T', ' ') : value;
}
