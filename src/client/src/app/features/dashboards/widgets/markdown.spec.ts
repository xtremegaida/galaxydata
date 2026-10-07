import { SecurityContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { DomSanitizer } from '@angular/platform-browser';
import { hasForeignImages, markdownHtml } from './markdown';

describe('text widgets’ Markdown', () => {
  /** As the page shows it: rendered, then sanitized by Angular. */
  const shown = (text: string) => {
    const div = document.createElement('div');
    div.innerHTML =
      TestBed.inject(DomSanitizer).sanitize(SecurityContext.HTML, markdownHtml(text)) ?? '';
    return div.innerHTML.trim();
  };

  it('writes headings one level down, under the page’s name', () => {
    expect(shown('# Sales\n\nA note.')).toBe('<h2>Sales</h2>\n<p>A note.</p>');
    expect(shown('###### Deep')).toBe('<h6>Deep</h6>');
  });

  it('keeps no HTML of its own, writing it out as text', () => {
    expect(shown('<script>alert(1)</script>')).toBe('&lt;script&gt;alert(1)&lt;/script&gt;');
    expect(shown('Hi <img src=x onerror=alert(1)>')).toBe(
      '<p>Hi &lt;img src=x onerror=alert(1)&gt;</p>',
    );
  });

  it('links to the web, mail and this site, each in a tab of its own; others are text', () => {
    expect(shown('[Docs](https://example.com "The docs")')).toBe(
      '<p><a href="https://example.com" title="The docs" target="_blank" rel="noopener noreferrer">Docs</a></p>',
    );
    expect(shown('[Mail](mailto:a@b.c) [Here](/dashboards)')).toContain('href="/dashboards"');
    expect(shown('[Run](javascript:alert(1))')).toBe('<p>Run</p>');
    expect(shown('[Data](data:text/html,x)')).toBe('<p>Data</p>');
  });

  it('shows images of this site or written in, and links to the others', () => {
    expect(shown('![Logo](/logo.svg)')).toBe('<p><img src="/logo.svg" alt="Logo"></p>');
    expect(shown('![Chart](https://elsewhere.org/c.png)')).toBe(
      '<p><a href="https://elsewhere.org/c.png" target="_blank" rel="noopener noreferrer">Chart</a></p>',
    );
    expect([
      hasForeignImages('![a](https://x.org/a.png)'),
      hasForeignImages('![a](/a.png)'),
    ]).toEqual([true, false]);
  });

  it('writes task boxes as characters', () => {
    expect(shown('- [x] Done\n- [ ] Not yet')).toContain('☑ Done');
  });
});
