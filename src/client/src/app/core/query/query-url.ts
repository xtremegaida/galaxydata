import { UrlSegment, UrlSegmentGroup, UrlTree } from '@angular/router';

/** Parameters' values as typed, by name (without its `$`): text, or null for NULL. */
export type ParameterValues = Readonly<Record<string, string | null>>;

/**
 * Where the query editor is: the saved query it shows (or none); its text and its parameters' values when they
 * aren't the saved query's (or there is none); and whether its rows are shown, run with them.
 */
export interface QueryAddress {
  readonly id: number | null;
  /** The text; null for the saved query's (or, without one, an empty text). */
  readonly text: string | null;
  /** The parameters' values; null for the saved query's (or, without one, none). */
  readonly values: ParameterValues | null;
  /** Whether the rows of the query, with its parameters' values, are shown. */
  readonly run: boolean;
}

/** An address read: where it leads, and whether something in it couldn't be read (and was left out). */
export interface ReadQueryAddress {
  readonly address: QueryAddress;
  readonly problem: string | null;
}

/** Where the query editor lives. */
export const queryPath = 'query';

/** A new query: no saved query, no text, nothing run. */
export const newQuery: QueryAddress = { id: null, text: null, values: null, run: false };

/**
 * The address of the editor's state, as the router takes it: `/query`, or `/query/<id>` for a saved query; the text,
 * values and whether it ran in its fragment (as JSON), which the browser keeps but doesn't send to the server (a
 * query's text may be long, and may hold values).
 */
export function queryUrlTree(address: QueryAddress): UrlTree {
  const segments = [
    new UrlSegment(queryPath, {}),
    ...(address.id === null ? [] : [new UrlSegment(String(address.id), {})]),
  ];
  return new UrlTree(
    new UrlSegmentGroup([], { primary: new UrlSegmentGroup(segments, {}) }),
    {},
    fragmentOf(address),
  );
}

/** The fragment of an address: null when it has nothing to say. */
export function fragmentOf(address: QueryAddress): string | null {
  const state: { text?: string; values?: ParameterValues; run?: true } = {};
  if (address.text !== null) {
    state.text = address.text;
  }
  if (address.values !== null) {
    state.values = address.values;
  }
  if (address.run) {
    state.run = true;
  }
  return Object.keys(state).length === 0 ? null : JSON.stringify(state);
}

/**
 * Where an address of the editor leads: the saved query its path names (`id`, a whole number), and what its
 * fragment says. A fragment that can't be read is left out, and said.
 */
export function readQueryAddress(id: string | null, fragment: string | null): ReadQueryAddress {
  const saved = id !== null && /^\d{1,9}$/.test(id) ? Number(id) : null;
  if (!fragment) {
    return { address: { ...newQuery, id: saved }, problem: null };
  }
  let state: unknown;
  try {
    state = JSON.parse(fragment);
  } catch {
    state = undefined;
  }
  if (typeof state !== 'object' || state === null || Array.isArray(state)) {
    return { address: { ...newQuery, id: saved }, problem: unreadable };
  }
  const { text, values, run } = state as Record<string, unknown>;
  const read = valuesOf(values);
  const wrong =
    (text !== undefined && typeof text !== 'string') ||
    (values !== undefined && read === null) ||
    (run !== undefined && typeof run !== 'boolean');
  return {
    address: {
      id: saved,
      text: typeof text === 'string' ? text : null,
      values: read,
      run: run === true,
    },
    problem: wrong ? unreadable : null,
  };
}

/** Whether two sets of values are the same (whatever order their names are in). */
export function sameValues(a: ParameterValues | null, b: ParameterValues | null): boolean {
  if (a === null || b === null) {
    return a === b;
  }
  // As many names, each with the same value: a name b hasn't gives no value of a's (text, or null).
  const names = Object.keys(a);
  return names.length === Object.keys(b).length && names.every((name) => a[name] === b[name]);
}

const unreadable = "Part of the address couldn't be read, and was left out.";

function valuesOf(values: unknown): ParameterValues | null {
  if (typeof values !== 'object' || values === null || Array.isArray(values)) {
    return null;
  }
  const entries = Object.entries(values);
  if (entries.some(([, value]) => typeof value !== 'string' && value !== null)) {
    return null;
  }
  // As own properties, whatever their names (`__proto__` too).
  return Object.fromEntries(entries) as ParameterValues;
}
