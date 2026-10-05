import * as monaco from 'monaco-editor/editor/editor.api';
import { FakeMonaco } from '../../../testing/monaco';
import { gdqConfiguration, gdqLanguageId, gdqMonarch, registerGdq } from './gdq-language';

/** Each line's tokens, as [text, type] (types without the language's postfix), with Monaco's own tokenizer. */
function tokensOf(text: string): [string, string][][] {
  const lines = text.split('\n');
  return monaco.editor
    .tokenize(text, gdqLanguageId)
    .map((tokens, line) =>
      tokens.map((token, index) => [
        lines[line].slice(token.offset, tokens[index + 1]?.offset ?? lines[line].length),
        token.type.replace(/\.gdq$/, ''),
      ]),
    );
}

/** The tokens of a text on one line, but white space. */
function line(text: string): [string, string][] {
  return tokensOf(text)[0].filter(([part]) => part.trim() !== '');
}

describe('the query language in the editor', () => {
  beforeAll(() => {
    // Monaco's theme service asks; jsdom hasn't it.
    window.matchMedia ??= (() => ({
      matches: false,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    })) as unknown as typeof window.matchMedia;
    registerGdq(monaco);
  });

  it('is registered with its highlighting and what editing knows of it', () => {
    const fake = new FakeMonaco();
    registerGdq(fake);
    expect(fake.languages.registered.get('gdq')).toEqual({
      tokens: gdqMonarch,
      configuration: gdqConfiguration,
    });
    expect(gdqConfiguration.comments).toEqual({ lineComment: '//', blockComment: ['/*', '*/'] });
  });

  it("marks names called (methods and functions), the language's words, parameters and operators", () => {
    expect(line('shop.orders.where(total > $min and not desc(x)) := it')).toEqual([
      ['shop', 'identifier'],
      ['.', 'operator'],
      ['orders', 'identifier'],
      ['.', 'operator'],
      ['where', 'type'],
      ['(', 'delimiter.parenthesis'],
      ['total', 'identifier'],
      ['>', 'operator'],
      ['$min', 'variable'],
      ['and', 'keyword'],
      ['not', 'keyword'],
      ['desc', 'keyword'],
      ['(', 'delimiter.parenthesis'],
      ['x', 'identifier'],
      // Tokens of a kind side by side are one.
      ['))', 'delimiter.parenthesis'],
      [':=', 'operator'],
      ['it', 'keyword'],
    ]);
    expect(line('if (true) null')).toEqual([
      ['if', 'invalid'],
      ['(', 'delimiter.parenthesis'],
      ['true', 'keyword'],
      [')', 'delimiter.parenthesis'],
      ['null', 'keyword'],
    ]);
    expect(line('total>-5 ?? x[0], $1')).toEqual([
      ['total', 'identifier'],
      ['>-', 'operator'],
      ['5', 'number'],
      ['??', 'operator'],
      ['x', 'identifier'],
      ['[', 'delimiter.square'],
      ['0', 'number'],
      [']', 'delimiter.square'],
      [',', 'delimiter'],
      ['$1', 'variable'],
    ]);
  });

  it('marks numbers of each kind', () => {
    expect(line('42 2.5 1e3 2.5E-2 0x1F')).toEqual([
      ['42', 'number'],
      ['2.5', 'number'],
      ['1e3', 'number.float'],
      ['2.5E-2', 'number.float'],
      ['0x1F', 'number.hex'],
    ]);
  });

  it('marks text in quotes with its escapes, and what is wrong in it', () => {
    expect(line("'it\\'s' \"tab\\t\" 'caf\\u00e9' 'bad\\q'")).toEqual([
      ["'it", 'string'],
      ["\\'", 'string.escape'],
      ["s'", 'string'],
      ['"tab', 'string'],
      ['\\t', 'string.escape'],
      ['"', 'string'],
      ["'caf", 'string'],
      ['\\u00e9', 'string.escape'],
      ["'", 'string'],
      ["'bad", 'string'],
      ['\\q', 'invalid'],
      ["'", 'string'],
    ]);
    // Text in quotes is on one line.
    expect(tokensOf("x == 'open\ny")).toEqual([
      [
        ['x', 'identifier'],
        [' ', ''],
        ['==', 'operator'],
        [' ', ''],
        ["'open", 'invalid'],
      ],
      [['y', 'identifier']],
    ]);
  });

  it('marks raw text and block comments over lines, and comments to the line end', () => {
    expect(tokensOf('`C:\\temp\nmore` # a comment\n/* one\ntwo */ x // end')).toEqual([
      [['`C:\\temp', 'string']],
      [
        ['more`', 'string'],
        [' ', ''],
        ['# a comment', 'comment'],
      ],
      [['/* one', 'comment']],
      [
        ['two */', 'comment'],
        [' ', ''],
        ['x', 'identifier'],
        [' ', ''],
        ['// end', 'comment'],
      ],
    ]);
  });
});
