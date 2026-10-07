using System;
using System.Collections.Generic;
using System.Globalization;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.ClickHouse;

/// <summary>
/// Maps a ClickHouse type, as <c>system.columns</c> writes it, to a logical type. <c>LowCardinality(...)</c> and
/// <c>Nullable(...)</c> are looked through (only <c>Nullable</c> lets a column be null), as is a
/// <c>SimpleAggregateFunction</c>'s value. Unsigned whole numbers map to the signed type that holds them all;
/// <c>UInt64</c>, to a decimal. A date-time with a time zone of its own is an instant (a date-time with an offset).
/// Types the language has no values for (wider integers and decimals, arrays, maps, tuples, aggregate states) are
/// unknown, read as their text (<see cref="ReadAs"/>), as are enums and network addresses, which text functions don't take.
/// </summary>
public static class ClickHouseTypeMapper
{
   public static ScalarType Map(string type)
   {
      ArgumentNullException.ThrowIfNull(type);
      (string name, IReadOnlyList<string> arguments, bool nullable) = Unwrap(type);
      ScalarType mapped = name switch
      {
         "Bool" or "Boolean" => ScalarType.Boolean,
         "Int8" or "Int16" or "UInt8" => ScalarType.Int16,
         "Int32" or "UInt16" => ScalarType.Int32,
         "Int64" or "UInt32" => ScalarType.Int64,
         "UInt64" => ScalarType.Decimal(20, 0),
         "Float32" or "BFloat16" => ScalarType.Single,
         "Float64" => ScalarType.Double,
         "Decimal" when arguments.Count == 2 && Int(arguments[0]) is { } precision and <= 38 => ScalarType.Decimal((byte)precision, (byte)(Int(arguments[1]) ?? 0)),
         "Decimal32" => Decimal(9, arguments),
         "Decimal64" => Decimal(18, arguments),
         "Decimal128" => Decimal(38, arguments),
         "String" => ScalarType.Text(),
         "FixedString" => arguments.Count == 1 && Int(arguments[0]) is { } length ? ScalarType.Text(length) : ScalarType.Text(),
         "Enum8" or "Enum16" or "Enum" or "IPv4" or "IPv6" => ScalarType.Text(),
         "UUID" => ScalarType.Guid,
         "Date" or "Date32" => ScalarType.Date,
         "DateTime" => arguments.Count >= 1 ? ScalarType.DateTimeOffset : ScalarType.DateTime,
         "DateTime64" => arguments.Count >= 2 ? ScalarType.DateTimeOffset : ScalarType.DateTime,
         "JSON" or "Object" => ScalarType.Json,
         _ => ScalarType.Unknown,
      };
      return mapped.WithNullable(nullable);
   }

   /// <summary>
   /// The type the source's SQL reads a column of <paramref name="type"/> as, when it can't be read as it is: text,
   /// for enums and network addresses (which text functions don't take), JSON, and types the language has no values
   /// for; null reads it as it is.
   /// </summary>
   public static string? ReadAs(string type)
   {
      ArgumentNullException.ThrowIfNull(type);
      (string name, _, bool nullable) = Unwrap(type);
      ScalarType mapped = Map(type);
      bool asText = mapped.Kind is ScalarKind.Unknown or ScalarKind.Json || name is "Enum8" or "Enum16" or "Enum" or "IPv4" or "IPv6";
      return !asText ? null : nullable ? "Nullable(String)" : "String";
   }

   /// <summary>The type's name and arguments, inside <c>LowCardinality</c>, <c>Nullable</c> and <c>SimpleAggregateFunction</c>; and whether it may be null.</summary>
   private static (string Name, IReadOnlyList<string> Arguments, bool Nullable) Unwrap(string type)
   {
      bool nullable = false;
      string current = type.Trim();
      while (true)
      {
         (string name, List<string> arguments) = Parse(current);
         switch (name)
         {
            case "LowCardinality" when arguments.Count == 1:
               current = arguments[0];
               continue;
            case "Nullable" when arguments.Count == 1:
               nullable = true;
               current = arguments[0];
               continue;
            case "SimpleAggregateFunction" when arguments.Count == 2:
               current = arguments[1];
               continue;
            default:
               return (name, arguments, nullable);
         }
      }
   }

   /// <summary><c>Name(a, b)</c>: the name, and the arguments split at the commas outside parentheses and quotes.</summary>
   private static (string Name, List<string> Arguments) Parse(string type)
   {
      int open = type.IndexOf('(', StringComparison.Ordinal);
      if (open < 0 || !type.EndsWith(')')) { return (type.Trim(), []); }
      List<string> arguments = [];
      int depth = 0;
      bool quoted = false;
      int start = open + 1;
      for (int i = start; i < type.Length - 1; i++)
      {
         char c = type[i];
         if (c == '\'' && (i == 0 || type[i - 1] != '\\')) { quoted = !quoted; }
         else if (!quoted && c == '(') { depth++; }
         else if (!quoted && c == ')') { depth--; }
         else if (!quoted && depth == 0 && c == ',')
         {
            arguments.Add(type[start..i].Trim());
            start = i + 1;
         }
      }
      arguments.Add(type[start..^1].Trim());
      return (type[..open].Trim(), arguments);
   }

   private static ScalarType Decimal(byte precision, IReadOnlyList<string> arguments) =>
      ScalarType.Decimal(precision, (byte)(arguments.Count == 1 ? Int(arguments[0]) ?? 0 : 0));

   private static int? Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
