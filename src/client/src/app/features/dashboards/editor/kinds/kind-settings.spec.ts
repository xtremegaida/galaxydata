import type { Dimension } from '../../model/definition';
import { limitedTo, seriesLimits } from './kind-settings';

const region: Dimension = { field: { path: [], column: 'region' }, bucket: null, label: 'Region' };
const city: Dimension = { field: { path: [], column: 'city' }, bucket: null, label: 'City' };

describe("a chart's largest series", () => {
  it('are eight, the rest as Other, once split by a series; another series keeps them; none, neither', () => {
    expect(seriesLimits(null, region, {})).toEqual({ seriesLimit: 8, seriesOther: true });
    expect(seriesLimits(region, city, { seriesLimit: 5, seriesOther: null })).toEqual({
      seriesLimit: 5,
      seriesOther: undefined,
    });
    expect(seriesLimits(region, city, { seriesLimit: null, seriesOther: null })).toEqual({
      seriesLimit: undefined,
      seriesOther: undefined,
    });
    // Left out, as the server writes a definition: never null or false.
    const off = seriesLimits(region, null, { seriesLimit: 8, seriesOther: true });
    expect(JSON.stringify({ kind: 'bar', ...off })).toBe('{"kind":"bar"}');
  });

  it('are every one when their limit is cleared, and the rest is Other only while they are limited', () => {
    expect(limitedTo(12, true)).toEqual({ seriesLimit: 12, seriesOther: true });
    expect(limitedTo(12, false)).toEqual({ seriesLimit: 12, seriesOther: undefined });
    expect(JSON.stringify(limitedTo(undefined, true))).toBe('{}');
  });
});
