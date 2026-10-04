import assert from 'node:assert/strict';
import { test } from 'node:test';
import { pageProblems, styleProblems } from './csp.mjs';

const page = (body, head = '') => `<!doctype html>
<html lang="en">
  <head><base href="/"/><link rel="icon" href="favicon.svg"/>${head}<link rel="stylesheet" href="styles-ABCDEFGH.css"></head>
  <body><gd-root></gd-root>${body}<script src="main-ABCDEFGH.js" type="module"></script></body>
</html>`;

test("Angular's page passes", () => {
  assert.deepEqual(
    pageProblems(page('<noscript>GalaxyData needs JavaScript: turn it on.</noscript>')),
    [],
  );
});

test("inline scripts are refused, data blocks aren't", () => {
  assert.deepEqual(pageProblems(page('<script>go()</script>')), ['an inline script']);
  assert.deepEqual(pageProblems(page('<script type="importmap">{}</script>')), [
    'an inline script',
  ]);
  assert.deepEqual(pageProblems(page('<script type="application/json">{"a": 1}</script>')), []);
  assert.deepEqual(pageProblems(page('<!-- <script>go()</script> -->')), []);
});

test('event handlers are refused, however they are written', () => {
  assert.deepEqual(
    pageProblems(
      page('', `<link rel="stylesheet" href="s.css" media="print" onload="this.media='all'">`),
    ),
    ['an event handler (onload on link)'],
  );
  assert.deepEqual(pageProblems(page('<div/onclick="go()"></div>')), [
    'an event handler (onclick on div)',
  ]);
  assert.deepEqual(pageProblems(page("<img src=x.png OnError='go()'>")), [
    'an event handler (onerror on img)',
  ]);
});

test('javascript: URLs are refused', () => {
  assert.deepEqual(pageProblems(page('<a href=" JavaScript:go()">x</a>')), [
    'a javascript: URL (href of a)',
  ]);
});

test('what loads from another origin is refused', () => {
  assert.deepEqual(
    pageProblems(page('', '<script src="https://www.example.com/tag.js"></script>')),
    ['a URL of another origin (src of script: https://www.example.com/tag.js)'],
  );
  assert.deepEqual(
    pageProblems(page('', '<link rel="stylesheet" href="//fonts.example.com/css">')),
    ['a URL of another origin (href of link: //fonts.example.com/css)'],
  );
  assert.deepEqual(
    pageProblems(page('<img src="data:image/png;base64,AAAA"><a href="https://example.com">x</a>')),
    [],
  );
});

test("stylesheets' URLs and imports of other origins are refused", () => {
  assert.deepEqual(
    styleProblems(`@font-face { src: url(media/roboto-ABCDEFGH.woff2) format("woff2"); }
      .a { background: url("data:image/svg+xml,<svg/>"); } /* url(https://example.com/x.png) */`),
    [],
  );
  assert.deepEqual(
    styleProblems(`@font-face { src: url( 'https://fonts.example.com/r.woff2' ); }`),
    ['a URL of another origin (https://fonts.example.com/r.woff2)'],
  );
  assert.deepEqual(styleProblems(`@import "https://fonts.example.com/css";`), [
    'an import of another origin (https://fonts.example.com/css)',
  ]);
});
