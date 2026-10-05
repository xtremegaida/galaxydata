/** How long something took: under a millisecond, in milliseconds, or in seconds. */
export function timeText(ms: number, locale: string): string {
  if (ms < 1) {
    return 'under 1 ms';
  }
  return ms < 1000
    ? `${Math.round(ms).toLocaleString(locale)} ms`
    : `${(ms / 1000).toLocaleString(locale, { maximumFractionDigits: 1 })} s`;
}
