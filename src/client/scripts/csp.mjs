// What a page or stylesheet has that the server's content security policy (Security:ContentSecurityPolicy) refuses.
// The policy loads scripts, styles, fonts and images from the client's own origin alone (images also as data:
// URLs), and runs no inline script: no script element without a src, no event handler attribute, no javascript:
// URL. Inline styles are allowed.

/** Script types that are data, which browsers don't run (and the policy doesn't refuse). */
const dataScriptTypes = /^(application|text)\/(json|ld\+json)$/i;

/** Attributes holding URLs that are loaded or followed. */
const urlAttributes = new Set(['src', 'href', 'action', 'formaction', 'srcset', 'poster', 'data']);

/** A URL to another origin: with a scheme, or starting with //. */
const otherOrigin = /^\s*([a-z][a-z0-9+.-]*:|\/\/)/i;

/** The page's problems, in the order found. */
export function pageProblems(html) {
  const problems = [];
  // Comments hold no elements.
  const markup = html.replace(/<!--[\s\S]*?-->/g, '');
  for (const tag of markup.matchAll(/<([a-zA-Z][\w-]*)([^>]*)>/g)) {
    const name = tag[1].toLowerCase();
    const attributes = attributesOf(tag[2]);
    for (const [attribute] of attributes) {
      if (/^on/i.test(attribute)) {
        problems.push(`an event handler (${attribute} on ${name})`);
      }
    }
    if (
      name === 'script' &&
      !attributes.has('src') &&
      !dataScriptTypes.test(attributes.get('type') ?? '')
    ) {
      problems.push('an inline script');
    }
    for (const [attribute, value] of attributes) {
      if (!urlAttributes.has(attribute.toLowerCase())) {
        continue;
      }
      if (/^\s*javascript:/i.test(value)) {
        problems.push(`a javascript: URL (${attribute} of ${name})`);
      } else if (
        name !== 'a' &&
        otherOrigin.test(value) &&
        !(name === 'img' && /^\s*data:/i.test(value))
      ) {
        problems.push(`a URL of another origin (${attribute} of ${name}: ${value.trim()})`);
      }
    }
  }
  return problems;
}

/** A stylesheet's problems: what it imports or loads from another origin. */
export function styleProblems(css) {
  const problems = [];
  const text = css.replace(/\/\*[\s\S]*?\*\//g, '');
  for (const match of text.matchAll(/url\(\s*(?:(["'])(.*?)\1|([^)]*?))\s*\)/gi)) {
    const url = match[2] ?? match[3] ?? '';
    if (otherOrigin.test(url) && !/^\s*data:/i.test(url)) {
      problems.push(`a URL of another origin (${url.trim()})`);
    }
  }
  for (const [, url] of text.matchAll(/@import\s+(?:url\(\s*)?["']?([^"')\s;]+)/gi)) {
    if (otherOrigin.test(url)) {
      problems.push(`an import of another origin (${url})`);
    }
  }
  return [...new Set(problems)];
}

/** A tag's attributes, by name (lower case); separated by spaces or slashes, as browsers read them. */
function attributesOf(text) {
  const attributes = new Map();
  for (const [, name, double, single, bare] of text.matchAll(
    /([^\s"'<>/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?/g,
  )) {
    attributes.set(name.toLowerCase(), double ?? single ?? bare ?? '');
  }
  return attributes;
}
