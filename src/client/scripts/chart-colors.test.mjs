import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

// The application's eight chart colours are written twice: as CSS (what charts read) and in TypeScript (where a
// page can't be read, and as a palette's first base). They say the same.
test("the charts' colours in the styles are those of the charts' theme", () => {
  const styles = readFileSync(new URL('../src/styles.scss', import.meta.url), 'utf8');
  const theme = readFileSync(
    new URL('../src/app/features/dashboards/charts/chart-theme.ts', import.meta.url),
    'utf8',
  );
  const css = [...styles.matchAll(/--gd-chart-(\d): light-dark\((#[0-9a-f]{6}), (#[0-9a-f]{6})\)/g)]
    .sort((a, b) => Number(a[1]) - Number(b[1]))
    .map((m) => [m[2], m[3]]);
  const start = theme.indexOf('export const galaxyColors');
  const constant = theme.slice(start, theme.indexOf('];', start));
  const ts = [...constant.matchAll(/light: '(#[0-9a-f]{6})', dark: '(#[0-9a-f]{6})'/g)].map((m) => [
    m[1],
    m[2],
  ]);
  assert.equal(css.length, 8);
  assert.deepEqual(ts, css);
});
