using System;
using System.Globalization;
using GalaxyData.Query.Binding;

namespace GalaxyData.Query.Cli;

/// <summary>
/// A <c>name=value</c> parameter from the command line. The value's type follows from how it is written: <c>null</c>,
/// <c>true</c>/<c>false</c>, a whole number, a decimal number, or text (quoted with ' or " to keep it text).
/// Text meets its column's type when the query binds, so <c>since=2026-01-01</c> works against a date.
/// </summary>
internal static class ParameterSpec
{
   public static QueryParameters AddTo(QueryParameters parameters, string text)
   {
      int equals = text.IndexOf('=', StringComparison.Ordinal);
      if (equals <= 0) { throw new FormatException($"'{text}' is not a parameter; write name=value, e.g. since=2026-01-01"); }
      string name = text[..equals].Trim();
      return parameters.Add(name, Value(text[(equals + 1)..]));
   }

   public static object? Value(string text)
   {
      string value = text.Trim();
      if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0]) { return value[1..^1]; }
      switch (value.ToLowerInvariant())
      {
         case "null":
            return null;
         case "true":
            return true;
         case "false":
            return false;
      }
      if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole)) { return whole; }
      if (decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number))
      {
         return number;
      }
      return value;
   }
}
