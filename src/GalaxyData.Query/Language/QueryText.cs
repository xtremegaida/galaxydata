using System;
using System.Collections.Generic;
using System.Text;

namespace GalaxyData.Query.Language;

/// <summary>Lexical helpers for writing names and values into query text.</summary>
public static class QueryText
{
   private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "true", "false", "null" };

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
}
