using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GalaxyData.Web.Palettes;

/// <summary>
/// Labels as palettes match them: a value's text as answers carry it (never as it is shown, which depends on the
/// locale), and that text normalised as a palette says. The client does the same (<c>series-colors.ts</c>), which
/// one fixture of cases holds both to (<c>tests/fixtures/palette-labels.json</c>).
/// </summary>
public static class LabelText
{
   /// <summary>
   /// What JavaScript's <c>\s</c> and .NET's <see cref="char.IsWhiteSpace(char)"/> each have, named, so both sides
   /// agree: Unicode's white space, and the byte order mark.
   /// </summary>
   private static readonly HashSet<int> Spaces =
   [
      0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20, 0x85, 0xA0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
      0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF,
   ];

   /// <summary>
   /// A value's text, as an answer carries it (<c>ValueCodec</c>'s): text as it is; booleans <c>true</c> and
   /// <c>false</c>; numbers as JavaScript writes them; null (no value) as null.
   /// </summary>
   public static string? Of(object? value) => value switch
   {
      null => null,
      string text => text,
      bool flag => flag ? "true" : "false",
      double number => Number(number),
      float single => Number(single),
      JsonElement json => json.ValueKind switch
      {
         JsonValueKind.Null or JsonValueKind.Undefined => null,
         JsonValueKind.String => json.GetString(),
         JsonValueKind.True => "true",
         JsonValueKind.False => "false",
         JsonValueKind.Number => Number(json.GetDouble()),
         _ => json.GetRawText(),
      },
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString(),
   };

   /// <summary>
   /// The text a label is matched by: in Unicode's composed form; text in brackets left out (<c>()</c>,
   /// <c>[]</c>, <c>{}</c>, nested; one never closed, or closed without being opened, is text); accents left out
   /// (combining marks, decomposed); lower case (each code point's simple mapping); white space left out, or each
   /// run of it one space; trimmed.
   /// </summary>
   public static string Normalize(string text, LabelMatching matching)
   {
      ArgumentNullException.ThrowIfNull(text);
      ArgumentNullException.ThrowIfNull(matching);
      string normalized = text.Normalize(NormalizationForm.FormC);
      if (matching.IgnoreBrackets) { normalized = WithoutBrackets(normalized); }
      if (matching.IgnoreAccents) { normalized = WithoutAccents(normalized); }
      StringBuilder kept = new(normalized.Length);
      bool space = false;
      foreach (Rune rune in normalized.EnumerateRunes())
      {
         if (Spaces.Contains(rune.Value))
         {
            space = true;
            continue;
         }
         if (space && !matching.IgnoreWhitespace && kept.Length > 0) { kept.Append(' '); }
         space = false;
         kept.Append((matching.IgnoreCase ? Lower(rune) : rune).ToString());
      }
      return kept.ToString();
   }

   /// <summary>A code point's simple lowercase mapping: .NET's invariant one, but for İ, which it keeps (as Turkish casing would) and Unicode maps to i.</summary>
   private static Rune Lower(Rune rune) => rune.Value == 0x130 ? new Rune('i') : Rune.ToLowerInvariant(rune);

   /// <summary>A label's key among a palette's overrides: its normalised text, or none for no value.</summary>
   public static string? Key(string? label, LabelMatching matching) => label == null ? null : Normalize(label, matching);

   /// <summary>A number as ECMAScript's <c>Number::toString</c> writes it: the shortest digits, in exponent form from 1e21 and below 1e-6.</summary>
   public static string Number(double value)
   {
      if (double.IsNaN(value)) { return "NaN"; }
      if (double.IsInfinity(value)) { return value > 0 ? "Infinity" : "-Infinity"; }
      if (value == 0) { return "0"; }
      if (value < 0) { return "-" + Number(-value); }
      // The shortest digits that read back as the value, and where the point goes (value = 0.digits × 10^point).
      string shortest = value.ToString("R", CultureInfo.InvariantCulture);
      int e = shortest.IndexOfAny(['E', 'e']);
      string mantissa = e < 0 ? shortest : shortest[..e];
      int exponent = e < 0 ? 0 : int.Parse(shortest[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
      int dot = mantissa.IndexOf('.', StringComparison.Ordinal);
      string digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
      int point = (dot < 0 ? mantissa.Length : dot) + exponent;
      int lead = 0;
      while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; }
      digits = digits[lead..].TrimEnd('0');
      point -= lead;
      int k = digits.Length;
      if (k <= point && point <= 21) { return digits + new string('0', point - k); }
      if (0 < point && point <= 21) { return digits[..point] + "." + digits[point..]; }
      if (-6 < point && point <= 0) { return "0." + new string('0', -point) + digits; }
      int written = point - 1;
      string power = (written < 0 ? "-" : "+") + Math.Abs(written).ToString(CultureInfo.InvariantCulture);
      return (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + power;
   }

   /// <summary>Without what brackets hold: pairs found as a stack finds them, each closing its own kind only.</summary>
   private static string WithoutBrackets(string text)
   {
      Stack<(char Close, int At)> open = [];
      bool[] dropped = new bool[text.Length];
      for (int i = 0; i < text.Length; i++)
      {
         char c = text[i];
         switch (c)
         {
            case '(': open.Push((')', i)); break;
            case '[': open.Push((']', i)); break;
            case '{': open.Push(('}', i)); break;
            case ')' or ']' or '}':
               if (open.Count > 0 && open.Peek().Close == c)
               {
                  (_, int from) = open.Pop();
                  Array.Fill(dropped, true, from, i - from + 1);
               }
               break;
         }
      }
      StringBuilder kept = new(text.Length);
      for (int i = 0; i < text.Length; i++)
      {
         if (!dropped[i]) { kept.Append(text[i]); }
      }
      return kept.ToString();
   }

   private static string WithoutAccents(string text)
   {
      string decomposed = text.Normalize(NormalizationForm.FormD);
      StringBuilder kept = new(decomposed.Length);
      foreach (Rune rune in decomposed.EnumerateRunes())
      {
         if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark) { kept.Append(rune.ToString()); }
      }
      return kept.ToString().Normalize(NormalizationForm.FormC);
   }
}
