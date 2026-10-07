using System;
using System.Collections.Generic;
using GalaxyData.Query.Sql;

namespace GalaxyData.Query.Dml;

/// <summary>A statement of a script: its text, from its first word to its last, without the <c>;</c> that ends it.</summary>
public sealed record ScriptStatement(string Text, int Start, int Line)
{
   /// <summary>
   /// What the statement is, in upper case: its first word (<c>UPDATE</c>), or, for <c>WITH ...</c>, the word after
   /// its common table expressions; null when it doesn't start with a word.
   /// </summary>
   public string? Keyword { get; init; }

   internal IReadOnlyList<ScriptToken> Tokens { get; init; } = [];
}

/// <summary>Something wrong with a script, at <see cref="Start"/> (on <see cref="Line"/>, from 1) for <see cref="Length"/> characters.</summary>
public sealed record ScriptProblem(string Message, int Start, int Length, int Line)
{
   public override string ToString() => $"line {Line}: {Message}";
}

/// <summary>A script split into statements, and what is wrong with its text (a string that isn't closed).</summary>
public sealed class SplitScript
{
   internal SplitScript(string text, IReadOnlyList<ScriptStatement> statements, IReadOnlyList<ScriptProblem> problems)
   {
      Text = text;
      Statements = statements;
      Problems = problems;
   }

   public string Text { get; }

   public IReadOnlyList<ScriptStatement> Statements { get; }

   public IReadOnlyList<ScriptProblem> Problems { get; }
}

internal enum ScriptTokenKind : byte
{
   Word,
   QuotedName,
   Text,
   Number,
   Symbol,

   /// <summary>A <c>;</c>.</summary>
   Separator,

   /// <summary>SQL Server's <c>GO</c>, on a line of its own.</summary>
   BatchEnd,
}

internal readonly record struct ScriptToken(ScriptTokenKind Kind, int Start, int End, int Line);

/// <summary>
/// Splits a script into statements at each <c>;</c> outside strings, quoted names and comments, as the dialect writes
/// them (its <see cref="SqlDialect.ScriptSyntax"/>): PostgreSQL's and DuckDB's <c>E'...'</c> strings and
/// <c>$tag$...$tag$</c> quotes, SQL Server's and SQLite's <c>[names]</c>, SQLite's <c>`names`</c>, nested block
/// comments (but SQLite's), and SQL Server's <c>GO</c> lines. A SQLite <c>CREATE TRIGGER</c> keeps the statements of
/// its body. Procedural blocks of other databases (<c>BEGIN ... END</c> with statements inside) are split at their
/// statements, and so don't run as written.
/// </summary>
public static class SqlScriptSplitter
{
   public static SplitScript Split(string script, SqlDialect dialect)
   {
      ArgumentNullException.ThrowIfNull(script);
      ArgumentNullException.ThrowIfNull(dialect);
      ScriptSyntax syntax = dialect.ScriptSyntax;
      Lexer lexer = new(script, syntax);
      lexer.Run();
      List<ScriptStatement> statements = [];
      List<ScriptToken> current = [];
      // A SQLite trigger's body (BEGIN ... END, CASE ... END inside it) has statements of its own.
      bool trigger = false;
      int depth = 0;
      foreach (ScriptToken token in lexer.Tokens)
      {
         bool separator = token.Kind == ScriptTokenKind.BatchEnd || (token.Kind == ScriptTokenKind.Separator && depth == 0);
         if (separator)
         {
            Add(statements, current, script);
            current.Clear();
            trigger = false;
            depth = 0;
            continue;
         }
         if (token.Kind == ScriptTokenKind.Word && syntax.TriggerBodies)
         {
            string word = script[token.Start..token.End];
            if (current.Count > 0 && Is(script, current[0], "CREATE") && word.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase)) { trigger = true; }
            else if (trigger && (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) || word.Equals("CASE", StringComparison.OrdinalIgnoreCase))) { depth++; }
            else if (trigger && depth > 0 && word.Equals("END", StringComparison.OrdinalIgnoreCase)) { depth--; }
         }
         current.Add(token);
      }
      Add(statements, current, script);
      return new SplitScript(script, statements, lexer.Problems);
   }

   private static void Add(List<ScriptStatement> statements, List<ScriptToken> tokens, string script)
   {
      if (tokens.Count == 0) { return; }
      int start = tokens[0].Start;
      int end = tokens[^1].End;
      statements.Add(new ScriptStatement(script[start..end], start, tokens[0].Line) { Keyword = Keyword(tokens, script), Tokens = tokens.ToArray() });
   }

   private static bool Is(string script, ScriptToken token, string word) =>
      token.Kind == ScriptTokenKind.Word && script.AsSpan(token.Start, token.End - token.Start).Equals(word, StringComparison.OrdinalIgnoreCase);

   /// <summary>The first word, or, after <c>WITH</c>, the first word after a common table expression's body that isn't <c>,</c> or <c>AS</c>.</summary>
   private static string? Keyword(List<ScriptToken> tokens, string script)
   {
      if (tokens[0].Kind != ScriptTokenKind.Word) { return null; }
      string first = script[tokens[0].Start..tokens[0].End].ToUpperInvariant();
      if (first != "WITH") { return first; }
      int depth = 0;
      bool afterBody = false;
      for (int i = 1; i < tokens.Count; i++)
      {
         ScriptToken token = tokens[i];
         string text = script[token.Start..token.End];
         if (token.Kind == ScriptTokenKind.Symbol && text == "(")
         {
            depth++;
            continue;
         }
         if (token.Kind == ScriptTokenKind.Symbol && text == ")")
         {
            depth--;
            afterBody = depth == 0;
            continue;
         }
         if (depth > 0) { continue; }
         if (token.Kind == ScriptTokenKind.Word && afterBody)
         {
            // WITH x (a, b) AS (...): the parentheses before AS are a column list.
            if (!text.Equals("AS", StringComparison.OrdinalIgnoreCase)) { return text.ToUpperInvariant(); }
         }
         afterBody = false;
      }
      return first;
   }

   private sealed class Lexer(string script, ScriptSyntax syntax)
   {
      private int i;
      private int line = 1;

      /// <summary>Whether a token has been read on the current line: GO separates batches only on a line of its own.</summary>
      private bool lineHasToken;

      public List<ScriptToken> Tokens { get; } = [];

      public List<ScriptProblem> Problems { get; } = [];

      private char Next => i + 1 < script.Length ? script[i + 1] : '\0';

      public void Run()
      {
         while (i < script.Length)
         {
            char c = script[i];
            if (c == '\n')
            {
               line++;
               i++;
               lineHasToken = false;
               continue;
            }
            if (char.IsWhiteSpace(c))
            {
               i++;
               continue;
            }
            if (c == '-' && Next == '-')
            {
               while (i < script.Length && script[i] != '\n') { i++; }
               continue;
            }
            if (c == '/' && Next == '*')
            {
               BlockComment();
               continue;
            }
            int start = i;
            int startLine = line;
            ScriptTokenKind kind;
            if (c == '\'')
            {
               Quoted('\'', backslashes: false, "string");
               kind = ScriptTokenKind.Text;
            }
            else if (c == '"' || (c == '`' && syntax.BacktickNames))
            {
               Quoted(c, backslashes: false, "quoted name");
               kind = ScriptTokenKind.QuotedName;
            }
            else if (c == '[' && syntax.BracketNames)
            {
               Quoted(']', backslashes: false, "quoted name");
               kind = ScriptTokenKind.QuotedName;
            }
            else if (c == '$' && syntax.DollarQuotes && DollarTag() is { } tag)
            {
               DollarQuoted(tag);
               kind = ScriptTokenKind.Text;
            }
            else if (IsWordStart(c))
            {
               while (i < script.Length && IsWordPart(script[i])) { i++; }
               // E'...' has backslash escapes in PostgreSQL and DuckDB.
               if (syntax.EscapeStrings && i == start + 1 && c is 'E' or 'e' && i < script.Length && script[i] == '\'')
               {
                  Quoted('\'', backslashes: true, "string");
                  kind = ScriptTokenKind.Text;
               }
               else if (syntax.BatchSeparator && !lineHasToken && i - start == 2 && script.AsSpan(start, 2).Equals("GO", StringComparison.OrdinalIgnoreCase) && GoLine(start))
               {
                  continue;
               }
               else
               {
                  kind = ScriptTokenKind.Word;
               }
            }
            else if (char.IsAsciiDigit(c))
            {
               Number();
               kind = ScriptTokenKind.Number;
            }
            else
            {
               i++;
               kind = c == ';' ? ScriptTokenKind.Separator : ScriptTokenKind.Symbol;
            }
            Tokens.Add(new ScriptToken(kind, start, i, startLine));
            lineHasToken = true;
         }
      }

      private static bool IsWordStart(char c) => char.IsLetter(c) || c is '_' or '@' or '#';

      private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '@' or '#';

      /// <summary>From the opening character at <c>i</c> to the closing one; doubled, the closing character is part of the text.</summary>
      private void Quoted(char close, bool backslashes, string what)
      {
         int start = i;
         int startLine = line;
         i++;
         while (i < script.Length)
         {
            char c = script[i];
            if (c == '\n') { line++; }
            if (backslashes && c == '\\')
            {
               i += 2;
               continue;
            }
            if (c == close)
            {
               if (i + 1 < script.Length && script[i + 1] == close)
               {
                  i += 2;
                  continue;
               }
               i++;
               return;
            }
            i++;
         }
         Problems.Add(new ScriptProblem($"This {what} isn't closed", start, script.Length - start, startLine));
      }

      private void BlockComment()
      {
         int start = i;
         int startLine = line;
         int depth = 0;
         while (i < script.Length)
         {
            if (script[i] == '/' && Next == '*' && (depth == 0 || syntax.NestedComments))
            {
               depth++;
               i += 2;
               continue;
            }
            if (script[i] == '*' && Next == '/')
            {
               depth--;
               i += 2;
               if (depth == 0) { return; }
               continue;
            }
            if (script[i] == '\n') { line++; }
            i++;
         }
         Problems.Add(new ScriptProblem("This comment isn't closed", start, script.Length - start, startLine));
      }

      /// <summary>The tag of a dollar quote opening at <c>i</c> (<c>$$</c>, <c>$body$</c>); null when <c>$</c> starts none (<c>$1</c>).</summary>
      private string? DollarTag()
      {
         int j = i + 1;
         if (j < script.Length && char.IsAsciiDigit(script[j])) { return null; }
         while (j < script.Length && (char.IsLetterOrDigit(script[j]) || script[j] == '_')) { j++; }
         return j < script.Length && script[j] == '$' ? script[i..(j + 1)] : null;
      }

      private void DollarQuoted(string tag)
      {
         int start = i;
         int startLine = line;
         int close = script.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
         int end = close < 0 ? script.Length : close + tag.Length;
         for (int j = i; j < end; j++)
         {
            if (script[j] == '\n') { line++; }
         }
         i = end;
         if (close < 0) { Problems.Add(new ScriptProblem($"This {tag} quote isn't closed", start, script.Length - start, startLine)); }
      }

      private void Number()
      {
         while (i < script.Length)
         {
            char c = script[i];
            if (char.IsLetterOrDigit(c) || c == '.' || c == '_')
            {
               i++;
            }
            else if (c is '+' or '-' && script[i - 1] is 'e' or 'E' && i + 1 < script.Length && char.IsAsciiDigit(script[i + 1]))
            {
               i++;
            }
            else
            {
               break;
            }
         }
      }

      /// <summary>
      /// Whether the GO just read is alone on its line (but for a count and a comment), so it ends a batch; reads the
      /// rest of the line if so. A count other than 1 is a problem: the batch would run that many times.
      /// </summary>
      private bool GoLine(int start)
      {
         int j = i;
         while (j < script.Length && script[j] is ' ' or '\t' or '\r') { j++; }
         int digits = j;
         while (j < script.Length && char.IsAsciiDigit(script[j])) { j++; }
         string count = script[digits..j];
         while (j < script.Length && script[j] is ' ' or '\t' or '\r') { j++; }
         bool comment = j + 1 < script.Length && script[j] == '-' && script[j + 1] == '-';
         if (j < script.Length && script[j] != '\n' && !comment) { return false; }
         if (count.Length > 0 && count.TrimStart('0') != "1")
         {
            Problems.Add(new ScriptProblem("GO with a count would run its batch more than once", start, j - start, line));
         }
         while (j < script.Length && script[j] != '\n') { j++; }
         Tokens.Add(new ScriptToken(ScriptTokenKind.BatchEnd, start, i, line));
         i = j;
         return true;
      }
   }
}
