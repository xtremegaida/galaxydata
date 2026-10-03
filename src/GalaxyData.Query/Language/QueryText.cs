using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GalaxyData.Query.Language;

/// <summary>A sort key for <see cref="QueryText.Compose"/>: an expression over the rows, in query text.</summary>
public sealed record QuerySortKey(string Expression, bool Descending = false);

/// <summary>Lexical helpers for writing names and values into query text.</summary>
public static class QueryText
{
   private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "true", "false", "null" };

   /// <summary>Names that mean something else where a row's members are in scope: the row itself, a group's key, join sides.</summary>
   private static readonly HashSet<string> RowKeywords = new(StringComparer.Ordinal) { "it", "key", "outer", "inner" };

   /// <summary>
   /// True when <paramref name="name"/> lexes as a single identifier: a letter, '_' or '$' followed by letters,
   /// digits, '_' or '$', and not a literal keyword or a word operator.
   /// </summary>
   public static bool IsBareIdentifier(string? name)
   {
      if (string.IsNullOrEmpty(name)) { return false; }
      char first = name[0];
      if (!(char.IsLetter(first) || first == '_' || first == '$')) { return false; }
      for (int i = 1; i < name.Length; i++)
      {
         char c = name[i];
         if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$')) { return false; }
      }
      if (Reserved.Contains(name)) { return false; }
      foreach (string word in QueryOperatorTable.WordOperators)
      {
         if (string.Equals(word, name, StringComparison.Ordinal)) { return false; }
      }
      return true;
   }

   /// <summary>A string literal: single quotes, with backslash escapes for quotes, backslashes and control characters.</summary>
   public static string QuoteString(string value)
   {
      ArgumentNullException.ThrowIfNull(value);
      StringBuilder text = new(value.Length + 2);
      text.Append('\'');
      foreach (char c in value)
      {
         switch (c)
         {
            case '\'': text.Append("\\'"); break;
            case '\\': text.Append("\\\\"); break;
            case '\n': text.Append("\\n"); break;
            case '\r': text.Append("\\r"); break;
            case '\t': text.Append("\\t"); break;
            case '\0': text.Append("\\0"); break;
            default:
               if (c < ' ') { text.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); }
               else { text.Append(c); }
               break;
         }
      }
      return text.Append('\'').ToString();
   }

   /// <summary>
   /// A reference to a member of the implicit row, as in a filter: the name itself when it reads as one, else
   /// <c>it["Total Spend"]</c>. Names such as <c>it</c> or <c>key</c> are quoted too, since they mean something else,
   /// and so are names starting with <c>$</c>, which would be parameters.
   /// </summary>
   public static string QuoteName(string name)
   {
      ArgumentNullException.ThrowIfNull(name);
      return IsBareIdentifier(name) && name[0] != '$' && !RowKeywords.Contains(name) ? name : "it[" + QuoteString(name) + "]";
   }

   /// <summary>Appends a member access to <paramref name="text"/>: <c>.name</c>, or <c>["weird name"]</c> when needed.</summary>
   public static StringBuilder AppendMember(StringBuilder text, string name)
   {
      ArgumentNullException.ThrowIfNull(text);
      return IsBareIdentifier(name) ? text.Append('.').Append(name) : text.Append('[').Append(QuoteString(name)).Append(']');
   }

   /// <summary>
   /// Formats a dotted path such as <c>shop.main.orders</c> or <c>xl["Budget 2024"]["Sheet 1"]</c>. The first part
   /// must be a bare identifier because an indexer needs something to its left.
   /// </summary>
   public static string FormatPath(IReadOnlyList<string> parts)
   {
      ArgumentNullException.ThrowIfNull(parts);
      if (parts.Count == 0) { throw new ArgumentException("A path needs at least one part", nameof(parts)); }
      if (!IsBareIdentifier(parts[0]))
      {
         throw new ArgumentException($"The first part of a path must be a plain name, not '{parts[0]}'", nameof(parts));
      }
      StringBuilder text = new(parts[0]);
      for (int i = 1; i < parts.Count; i++) { AppendMember(text, parts[i]); }
      return text.ToString();
   }

   /// <summary>
   /// The statements of a query text, separated by <c>;</c> outside strings, comments and brackets: for each, the
   /// range from its first to its last character that isn't space or a comment. Empty statements are left out.
   /// </summary>
   public static IReadOnlyList<Range> SplitStatements(string text)
   {
      ArgumentNullException.ThrowIfNull(text);
      List<Range> statements = [];
      int depth = 0;
      int start = -1;
      int end = -1;
      int i = 0;
      while (i < text.Length)
      {
         char c = text[i];
         if (char.IsWhiteSpace(c)) { i++; continue; }
         if (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
         {
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') { i++; }
            continue;
         }
         if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
         {
            int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            i = close < 0 ? text.Length : close + 2;
            continue;
         }
         if (c == ';' && depth == 0)
         {
            if (start >= 0) { statements.Add(start..end); }
            start = -1;
            i++;
            continue;
         }
         if (start < 0) { start = i; }
         switch (c)
         {
            case '(' or '[' or '{':
               depth++;
               i++;
               break;
            case ')' or ']' or '}':
               depth = Math.Max(0, depth - 1);
               i++;
               break;
            case '\'' or '"':
               i++;
               while (i < text.Length && text[i] != c) { i += text[i] == '\\' ? 2 : 1; }
               i = Math.Min(i + 1, text.Length);
               break;
            case '`':
               int closing = text.IndexOf('`', i + 1);
               i = closing < 0 ? text.Length : closing + 1;
               break;
            default:
               i++;
               break;
         }
         end = i;
      }
      if (start >= 0) { statements.Add(start..end); }
      return statements;
   }

   /// <summary>
   /// The parameters a query text uses (<c>$min</c>), by name without the <c>$</c>, each once, in the order they first
   /// appear; outside strings and comments. The text needn't parse: an editor can ask for values as it is written.
   /// </summary>
   public static IReadOnlyList<string> Parameters(string text)
   {
      List<string> names = [];
      HashSet<string> seen = new(StringComparer.Ordinal);
      foreach ((string name, _) in ParameterUses(text))
      {
         if (seen.Add(name)) { names.Add(name); }
      }
      return names;
   }

   /// <summary>Each use of a parameter in a query text, outside strings and comments: its name without the <c>$</c>, and where it is (the <c>$</c> included).</summary>
   public static IReadOnlyList<(string Name, Range Range)> ParameterUses(string text)
   {
      ArgumentNullException.ThrowIfNull(text);
      List<(string, Range)> uses = [];
      int i = 0;
      while (i < text.Length)
      {
         char c = text[i];
         if (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
         {
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') { i++; }
            continue;
         }
         if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
         {
            int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            i = close < 0 ? text.Length : close + 2;
            continue;
         }
         switch (c)
         {
            case '\'' or '"':
               i++;
               while (i < text.Length && text[i] != c) { i += text[i] == '\\' ? 2 : 1; }
               i = Math.Min(i + 1, text.Length);
               continue;
            case '`':
               int closing = text.IndexOf('`', i + 1);
               i = closing < 0 ? text.Length : closing + 1;
               continue;
         }
         if (!IsNameStart(c))
         {
            i++;
            continue;
         }
         // A whole name, so a '$' inside one (a$b) isn't a parameter.
         int start = i;
         while (i < text.Length && (IsNameStart(text[i]) || char.IsDigit(text[i]))) { i++; }
         if (c == '$' && i - start > 1) { uses.Add((text[(start + 1)..i], start..i)); }
      }
      return uses;
   }

   private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || c == '$';

   /// <summary>
   /// Adds filters and a sort to a query text: the last statement becomes <c>(last).where(f1).where(f2).orderBy(...)</c>,
   /// and the statements before it (named subtrees) stay as they are. A sort replaces the query's own. Filters and
   /// sort keys are expressions over the rows, such as <c>total &gt; 100</c> or <c>QuoteName("Total Spend")</c>.
   /// <paramref name="tiebreak"/> names columns added to the end of the sort (when there is one) that aren't in it
   /// already, so pages of equal values come back in the same order each time.
   /// </summary>
   public static string Compose(string text, IEnumerable<string>? filters = null, IEnumerable<QuerySortKey>? sort = null,
                                IEnumerable<string>? tiebreak = null)
   {
      ArgumentNullException.ThrowIfNull(text);
      List<string> where = filters?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? [];
      List<QuerySortKey> keys = sort?.ToList() ?? [];
      if (keys.Count > 0 && tiebreak != null)
      {
         foreach (string name in tiebreak)
         {
            string expression = QuoteName(name);
            if (!keys.Any(k => string.Equals(k.Expression.Trim(), expression, StringComparison.Ordinal))) { keys.Add(new QuerySortKey(expression)); }
         }
      }
      if (where.Count == 0 && keys.Count == 0) { return text; }
      IReadOnlyList<Range> statements = SplitStatements(text);
      if (statements.Count == 0) { throw new ArgumentException("The query text has no statement to add to", nameof(text)); }
      (int start, int length) = statements[^1].GetOffsetAndLength(text.Length);
      StringBuilder composed = new(text.Length + 64);
      composed.Append(text, 0, start).Append('(').Append(text, start, length).Append(')');
      foreach (string filter in where) { composed.Append(".where(").Append(filter).Append(')'); }
      if (keys.Count > 0)
      {
         composed.Append(".orderBy(").AppendJoin(", ", keys.Select(k => k.Descending ? "desc(" + k.Expression + ")" : k.Expression)).Append(')');
      }
      return composed.Append(text, start + length, text.Length - start - length).ToString();
   }
}
