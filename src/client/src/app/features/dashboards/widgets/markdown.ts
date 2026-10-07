import { Marked, type Tokens } from 'marked';

/** Text made plain in HTML. */
function escape(text: string): string {
  return text.replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] ?? c,
  );
}

/** Whether a link may be followed: the web's (http, https), mail, or this site's own (relative). */
function safeHref(href: string): boolean {
  const trimmed = href.trim();
  if (/^(https?:|mailto:)/i.test(trimmed)) {
    return true;
  }
  return !/^[a-z][a-z0-9+.-]*:/i.test(trimmed) && !trimmed.startsWith('//');
}

/** Whether an image may be shown: this site's own, or written into the page (the content security policy takes no other). */
function safeImage(href: string): boolean {
  const trimmed = href.trim();
  return (
    /^data:image\/(png|gif|jpeg|webp);/i.test(trimmed) ||
    (trimmed.startsWith('/') && !trimmed.startsWith('//'))
  );
}

/**
 * A text widget's Markdown, as HTML: its own HTML written out as text (none is kept), links to the web, mail and
 * this site only (each opening a tab of its own, telling it nothing of this one), headings one level down (the
 * page's name is its level 1), images of this site or written in, and task boxes as characters. A private
 * instance, so the editor's (Monaco's) settings and this one's don't meet. Angular sanitizes it besides.
 */
const markdown = new Marked({
  async: false,
  gfm: true,
  breaks: false,
  renderer: {
    html({ text }: Tokens.HTML | Tokens.Tag): string {
      return escape(text);
    },
    heading({ tokens, depth }: Tokens.Heading): string {
      const level = Math.min(depth + 1, 6);
      return `<h${level}>${this.parser.parseInline(tokens)}</h${level}>\n`;
    },
    link({ href, title, tokens }: Tokens.Link): string {
      const text = this.parser.parseInline(tokens);
      if (!safeHref(href)) {
        return text;
      }
      const titled = title ? ` title="${escape(title)}"` : '';
      return `<a href="${escape(href)}"${titled} target="_blank" rel="noopener noreferrer">${text}</a>`;
    },
    image({ href, title, text }: Tokens.Image): string {
      if (!safeImage(href)) {
        return safeHref(href)
          ? `<a href="${escape(href)}" target="_blank" rel="noopener noreferrer">${escape(text || 'image')}</a>`
          : escape(text);
      }
      return `<img src="${escape(href)}" alt="${escape(text)}"${title ? ` title="${escape(title)}"` : ''}>`;
    },
    checkbox({ checked }: Tokens.Checkbox): string {
      return checked ? '☑ ' : '☐ ';
    },
  },
});

/** The HTML of a text widget's Markdown. */
export function markdownHtml(text: string): string {
  return markdown.parse(text) as string;
}

/** Whether Markdown names images the page won't show (of other sites), for the editor to say. */
export function hasForeignImages(text: string): boolean {
  let found = false;
  markdown.walkTokens(markdown.lexer(text), (token) => {
    if (token.type === 'image' && !safeImage((token as Tokens.Image).href)) {
      found = true;
    }
  });
  return found;
}
