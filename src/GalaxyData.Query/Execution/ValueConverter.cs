using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Execution;

/// <summary>
/// Converts values read from a database to the CLR type of their logical type, whatever the provider handed back:
/// SQLite gives integers, reals and text for everything, DuckDB gives its own numeric types, and so on.
/// </summary>
/// <remarks>
/// The CLR types: bool, short, int, long, decimal, float, double, string, byte[], Guid, DateOnly, TimeOnly,
/// DateTime, DateTimeOffset and TimeSpan (intervals). Decimals with a scale get exactly that many digits, so
/// <c>250</c> from SQLite and <c>250.00</c> from DuckDB are the same value and print the same way; decimals with
/// no scale lose their trailing zeros. Date-times with an offset are instants, in UTC, as PostgreSQL and DuckDB
/// keep them: SQL Server's and SQLite's own offsets are left behind.
/// </remarks>
public static class ValueConverter
{
   private static readonly string[] DateTimeFormats =
   [
      "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd",
   ];

   /// <summary>The value as <paramref name="type"/>'s CLR type; throws <see cref="FormatException"/> or <see cref="InvalidCastException"/> when it can't be.</summary>
   public static object? ToLogical(object? value, ScalarType type)
   {
      if (value is null or DBNull) { return null; }
      return type.Kind switch
      {
         ScalarKind.Boolean => ToBoolean(value),
         ScalarKind.Int16 => checked((short)ToInt64(value)),
         ScalarKind.Int32 => checked((int)ToInt64(value)),
         ScalarKind.Int64 => ToInt64(value),
         ScalarKind.Decimal => WithScale(ToDecimal(value), type),
         ScalarKind.Single => (float)ToDouble(value),
         ScalarKind.Double => ToDouble(value),
         ScalarKind.String or ScalarKind.Json => ToText(value),
         ScalarKind.Binary => ToBytes(value) ?? throw Cannot(value, type),
         ScalarKind.Guid => ToGuid(value),
         ScalarKind.Date => ToDate(value),
         ScalarKind.Time => ToTime(value),
         ScalarKind.DateTime => ToDateTime(value),
         ScalarKind.DateTimeOffset => ToDateTimeOffset(value),
         ScalarKind.Interval => value switch
         {
            TimeSpan span => span,
            string text => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
            _ => throw Cannot(value, type),
         },
         _ => value,
      };
   }

   /// <summary>
   /// A value of an unknown type as text: how the merge engine holds it, and so what <c>toString(...)</c> gives there.
   /// Lists and structs are written as DuckDB writes them (<c>[a, b]</c>, <c>{k: v}</c>), binary values as <c>\x0A\xFF</c>.
   /// </summary>
   public static string UnknownText(object value)
   {
      ArgumentNullException.ThrowIfNull(value);
      StringBuilder text = new();
      WriteUnknown(text, value);
      return text.ToString();
   }

   private static void WriteUnknown(StringBuilder text, object? value)
   {
      switch (value)
      {
         case null or DBNull:
            text.Append("NULL");
            break;
         case string s:
            text.Append(s);
            break;
         case byte[] bytes:
            foreach (byte b in bytes) { text.Append("\\x").Append(b.ToString("X2", CultureInfo.InvariantCulture)); }
            break;
         case IDictionary dictionary:
         {
            text.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
               if (!first) { text.Append(", "); }
               first = false;
               WriteUnknown(text, entry.Key);
               text.Append(": ");
               WriteUnknown(text, entry.Value);
            }
            text.Append('}');
            break;
         }
         case IEnumerable sequence:
         {
            text.Append('[');
            bool first = true;
            foreach (object? item in sequence)
            {
               if (!first) { text.Append(", "); }
               first = false;
               WriteUnknown(text, item);
            }
            text.Append(']');
            break;
         }
         case DateTimeOffset offset:
            text.Append(offset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture));
            break;
         default:
            text.Append(ToText(value));
            break;
      }
   }

   private static InvalidCastException Cannot(object value, ScalarType type) =>
      new($"A {value.GetType().Name} ({Convert.ToString(value, CultureInfo.InvariantCulture)}) can't be read as {TypeRules.Describe(type)}");

   private static bool ToBoolean(object value) => value switch
   {
      bool flag => flag,
      string text => text.Trim().ToLowerInvariant() switch
      {
         "1" or "true" or "t" or "yes" or "y" => true,
         "0" or "false" or "f" or "no" or "n" => false,
         _ => throw new FormatException($"'{text}' is not a true/false value"),
      },
      _ => ToDouble(value) != 0,
   };

   private static long ToInt64(object value) => value switch
   {
      long l => l,
      int i => i,
      short s => s,
      sbyte sb => sb,
      byte b => b,
      ushort us => us,
      uint ui => ui,
      ulong ul => checked((long)ul),
      BigInteger big => (long)big,
      bool flag => flag ? 1 : 0,
      decimal m when decimal.Truncate(m) == m => (long)m,
      double d when Math.Truncate(d) == d => checked((long)d),
      float f when MathF.Truncate(f) == f => checked((long)f),
      string text => long.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture),
      _ => throw new InvalidCastException($"{Convert.ToString(value, CultureInfo.InvariantCulture)} is not a whole number"),
   };

   private static decimal ToDecimal(object value) => value switch
   {
      decimal m => m,
      double d => (decimal)d,
      float f => (decimal)f,
      long or int or short or sbyte or byte or ushort or uint or ulong => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
      BigInteger big => (decimal)big,
      string text => decimal.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
      _ => throw new InvalidCastException($"A {value.GetType().Name} is not a number"),
   };

   private static decimal WithScale(decimal value, ScalarType type)
   {
      // With no scale to give it, a value has no trailing zeros, whichever database computed it: 200.00 is 200.
      if (type.Precision == 0) { return value / 1.0000000000000000000000000000m; }
      decimal rounded = decimal.Round(value, type.Scale, MidpointRounding.AwayFromZero);
      // Adding zero with the wanted scale sets the scale without changing the value: 250 + 0.00 = 250.00.
      return rounded + new decimal(0, 0, 0, false, type.Scale);
   }

   private static double ToDouble(object value) => value switch
   {
      double d => d,
      float f => f,
      decimal m => (double)m,
      long or int or short or sbyte or byte or ushort or uint or ulong => Convert.ToDouble(value, CultureInfo.InvariantCulture),
      BigInteger big => (double)big,
      bool flag => flag ? 1 : 0,
      string text => double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
      _ => throw new InvalidCastException($"A {value.GetType().Name} is not a number"),
   };

   private static byte[]? ToBytes(object value)
   {
      switch (value)
      {
         case byte[] bytes:
            return bytes;
         case Stream stream:
            using (MemoryStream copy = new())
            {
               stream.CopyTo(copy);
               return copy.ToArray();
            }
         default:
            return null;
      }
   }

   private static string ToText(object value) => value switch
   {
      string text => text,
      DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
      DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
      Guid guid => guid.ToString("D"),
      bool flag => flag ? "true" : "false",
      _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
   };

   private static Guid ToGuid(object value) => value switch
   {
      Guid guid => guid,
      string text => Guid.Parse(text),
      byte[] { Length: 16 } bytes => new Guid(bytes),
      _ => throw new InvalidCastException($"A {value.GetType().Name} is not a guid"),
   };

   private static DateOnly ToDate(object value) => value switch
   {
      DateOnly date => date,
      DateTime dateTime => DateOnly.FromDateTime(dateTime),
      DateTimeOffset offset => DateOnly.FromDateTime(offset.DateTime),
      string text => DateOnly.FromDateTime(ParseDateTime(text)),
      _ => throw new InvalidCastException($"A {value.GetType().Name} is not a date"),
   };

   private static TimeOnly ToTime(object value) => value switch
   {
      TimeOnly time => time,
      TimeSpan span => TimeOnly.FromTimeSpan(span),
      DateTime dateTime => TimeOnly.FromDateTime(dateTime),
      string text => TimeOnly.Parse(text.Trim(), CultureInfo.InvariantCulture),
      _ => throw new InvalidCastException($"A {value.GetType().Name} is not a time"),
   };

   private static DateTime ToDateTime(object value) => value switch
   {
      DateTime dateTime => dateTime,
      DateOnly date => date.ToDateTime(TimeOnly.MinValue),
      DateTimeOffset offset => offset.DateTime,
      string text => ParseDateTime(text),
      _ => throw new InvalidCastException($"A {value.GetType().Name} is not a date-time"),
   };

   private static DateTimeOffset ToDateTimeOffset(object value) => value switch
   {
      DateTimeOffset offset => offset.ToUniversalTime(),
      DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
      DateOnly date => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
      string text => DateTimeOffset.Parse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
      _ => throw new InvalidCastException($"A {value.GetType().Name} is not a date-time with offset"),
   };

   private static DateTime ParseDateTime(string text) =>
      DateTime.ParseExact(text.Trim(), DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces);
}
