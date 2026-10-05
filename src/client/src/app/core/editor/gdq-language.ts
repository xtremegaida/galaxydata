import type { languages } from 'monaco-editor/editor/editor.api';

/** The query language's id among Monaco's languages. */
export const gdqLanguageId = 'gdq';

/**
 * How the query language is highlighted (Monaco's Monarch), as its reference describes it (`docs/language.md`):
 * comments (`#` and `//` to the line's end, `/* … *\/`), text in quotes on one line with its escapes, raw text in
 * backticks (over lines too), numbers (whole, decimal, with an exponent, hexadecimal), parameters (`$min`), the
 * language's words, the words it refuses (`if`, `for`, `break`, `continue`), operators, and names called as methods
 * and functions.
 */
export const gdqMonarch: languages.IMonarchLanguage = {
  defaultToken: '',
  tokenPostfix: '.gdq',
  keywords: [
    'true',
    'false',
    'null',
    'and',
    'or',
    'not',
    'in',
    'let',
    'it',
    'outer',
    'inner',
    'key',
    'desc',
    'asc',
  ],
  refused: ['if', 'for', 'break', 'continue'],
  symbols: /[=><!?:&|+\-*/%.^~]+/,
  escapes: /\\(?:['"\\/nrtbf0]|u[0-9A-Fa-f]{4})/,
  tokenizer: {
    root: [
      { include: '@whitespace' },
      [/\$[\w$]+/, 'variable'],
      // A name called: a method or a function (the language's words keep their own).
      [
        /[A-Za-z_][\w$]*(?=\s*\()/,
        { cases: { '@keywords': 'keyword', '@refused': 'invalid', '@default': 'type' } },
      ],
      [
        /[A-Za-z_][\w$]*/,
        { cases: { '@keywords': 'keyword', '@refused': 'invalid', '@default': 'identifier' } },
      ],
      [/0[xX][0-9A-Fa-f]+/, 'number.hex'],
      [/\d+(?:\.\d+)?[eE][+-]?\d+/, 'number.float'],
      [/\d+(?:\.\d+)?/, 'number'],
      [/[()[\]{}]/, '@brackets'],
      // Operators run together (`total>-5`) are operators too; the language's own checks say what is wrong.
      [/@symbols/, 'operator'],
      [/[,;]/, 'delimiter'],
      // Text in quotes is on one line: unclosed, it is wrong to the line's end.
      [/'(?:[^'\\]|\\.)*$/, 'invalid'],
      [/"(?:[^"\\]|\\.)*$/, 'invalid'],
      [/'/, 'string', '@single'],
      [/"/, 'string', '@double'],
      [/`/, 'string', '@raw'],
    ],
    whitespace: [
      [/[ \t\r\n]+/, ''],
      [/\/\*/, 'comment', '@comment'],
      [/(?:\/\/|#).*$/, 'comment'],
    ],
    // Block comments don't nest.
    comment: [
      [/[^*]+/, 'comment'],
      [/\*\//, 'comment', '@pop'],
      [/\*/, 'comment'],
    ],
    single: [
      [/[^'\\]+/, 'string'],
      [/@escapes/, 'string.escape'],
      [/\\./, 'invalid'],
      [/'/, 'string', '@pop'],
    ],
    double: [
      [/[^"\\]+/, 'string'],
      [/@escapes/, 'string.escape'],
      [/\\./, 'invalid'],
      [/"/, 'string', '@pop'],
    ],
    // Raw text has no escapes, and may span lines.
    raw: [
      [/[^`]+/, 'string'],
      [/`/, 'string', '@pop'],
    ],
  },
};

/** What editing the language knows: its comments (Ctrl+/ toggles them), brackets, and pairs closed as typed. */
export const gdqConfiguration: languages.LanguageConfiguration = {
  comments: { lineComment: '//', blockComment: ['/*', '*/'] },
  brackets: [
    ['(', ')'],
    ['[', ']'],
    ['{', '}'],
  ],
  autoClosingPairs: [
    { open: '(', close: ')' },
    { open: '[', close: ']' },
    { open: '{', close: '}' },
    { open: "'", close: "'", notIn: ['string', 'comment'] },
    { open: '"', close: '"', notIn: ['string', 'comment'] },
    { open: '`', close: '`', notIn: ['string', 'comment'] },
  ],
  surroundingPairs: [
    { open: '(', close: ')' },
    { open: '[', close: ']' },
    { open: "'", close: "'" },
    { open: '"', close: '"' },
    { open: '`', close: '`' },
  ],
  wordPattern: /(-?\d+(?:\.\d+)?)|(\$?[A-Za-z_][\w$]*)/g,
};

/** Monaco's language API, as far as registering one goes. */
interface Languages {
  register(language: { id: string }): void;
  setMonarchTokensProvider(id: string, language: languages.IMonarchLanguage): unknown;
  setLanguageConfiguration(id: string, configuration: languages.LanguageConfiguration): unknown;
}

/** Registers the query language with Monaco. */
export function registerGdq(monaco: { readonly languages: Languages }): void {
  monaco.languages.register({ id: gdqLanguageId });
  monaco.languages.setMonarchTokensProvider(gdqLanguageId, gdqMonarch);
  monaco.languages.setLanguageConfiguration(gdqLanguageId, gdqConfiguration);
}
