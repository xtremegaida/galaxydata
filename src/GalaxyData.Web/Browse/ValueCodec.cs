using System;
using System.Globalization;
using System.Text.Json;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Types;

namespace GalaxyData.Web.Browse;

/// <summary>A value sent to the API that isn't one of its type.</summary>
public sealed class ValueFormatException(string message) : Exception(message);

/// <summary>
/// How values of the language's types travel as JSON, both ways. JSON numbers can't hold every <c>int64</c> or
/// <c>decimal</c> exactly (JavaScript reads them as doubles), so those travel as text; so do dates and times (ISO
/// 8601), guids, binary values (base64), doubles that aren't numbers (<c>NaN</c>, <c>Infinity</c>, <c>-Infinity</c>)
/// and values of types the language has no values for (as text). Booleans, smaller whole numbers and doubles are
/// JSON's own; null is null.
/// </summary>
public static class ValueCodec
{
   private const string DateFormat = "yyyy-MM-dd";
   private const string TimeFormat = "HH:mm:ss.FFFFFFF";
   private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF";
   private const string OffsetFormat = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz";

   private static readonly string[] DateTimeFormats = [DateTimeFormat, "yyyy-MM-dd' 'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd' 'HH:mm", DateFormat];

   /// <summary>ISO date-times with an offset, <c>Z</c>, or none (UTC).</summary>
   private static readonly string[] OffsetFormats =
      ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd' 'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd' 'HH:mmK", DateFormat];

   /// <summary>A value as it is sent: a string, a boolean, a JSON number or null.</summary>
   public static object? Encode(object? value, ScalarType type)
   {
      if (value is null or DBNull) { return null; }
      return (type.Kind, value) switch
      {
         (_, bool flag) => flag,
         (_, short or int or byte or sbyte or ushort) => Convert.ToInt32(value, CultureInfo.InvariantCulture),
         (_, long whole) => whole.ToString(CultureInfo.InvariantCulture),
         (_, uint or ulong) => Convert.ToString(value, CultureInfo.InvariantCulture),
         (_, decimal number) => number.ToString(CultureInfo.InvariantCulture),
         // As short as the single it is: widened, 0.1 would be 0.10000000149011612.
         (_, float single) => float.IsFinite(single) ? double.Parse(single.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : Number(single),
         (_, double number) => Number(number),
         (_, string text) => text,
         (_, byte[] bytes) => Convert.ToBase64String(bytes),
         (_, Guid guid) => guid.ToString("D"),
         (_, DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture),
         (_, TimeOnly time) => time.ToString(TimeFormat, CultureInfo.InvariantCulture),
         (ScalarKind.Time, TimeSpan span) => TimeOnly.FromTimeSpan(span).ToString(TimeFormat, CultureInfo.InvariantCulture),
         (_, TimeSpan span) => span.ToString("c", CultureInfo.InvariantCulture),
         (_, DateTime dateTime) => dateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
         (_, DateTimeOffset offset) => offset.ToString(OffsetFormat, CultureInfo.InvariantCulture),
         _ => ValueConverter.UnknownText(value),
      };
   }

   private static object Number(double value) =>
      double.IsNaN(value) ? "NaN" : double.IsPositiveInfinity(value) ? "Infinity" : double.IsNegativeInfinity(value) ? "-Infinity" : value;

   /// <summary>
   /// A value sent for <paramref name="type"/>, as the CLR value queries take: as <see cref="Encode"/> writes it,
   /// and for numbers a JSON number too. <see cref="ValueFormatException"/> when it isn't one.
   /// </summary>
   public static object? Decode(JsonElement value, ScalarType type)
   {
      if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { return null; }
      try
      {
         return type.Kind switch
         {
            ScalarKind.Boolean => value.ValueKind switch
            {
               JsonValueKind.True => true,
               JsonValueKind.False => false,
               _ => throw Wrong(value, type),
            },
            ScalarKind.Int16 => checked((short)Whole(value, type)),
            ScalarKind.Int32 => checked((int)Whole(value, type)),
            ScalarKind.Int64 => Whole(value, type),
            // No thousands separators: 12,25 is no number (and not 1225).
            ScalarKind.Decimal => value.ValueKind == JsonValueKind.Number
               ? value.GetDecimal()
               : decimal.Parse(Text(value, type), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
            ScalarKind.Single => (float)Floating(value, type),
            ScalarKind.Double => Floating(value, type),
            ScalarKind.Binary => Convert.FromBase64String(Text(value, type)),
            ScalarKind.Guid => Guid.Parse(Text(value, type), CultureInfo.InvariantCulture),
            ScalarKind.Date => DateOnly.ParseExact(Text(value, type), DateFormat, CultureInfo.InvariantCulture),
            ScalarKind.Time => TimeOnly.ParseExact(Text(value, type), [TimeFormat, "HH:mm"], CultureInfo.InvariantCulture),
            ScalarKind.DateTime => DateTime.ParseExact(Text(value, type), DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None),
            ScalarKind.DateTimeOffset => DateTimeOffset.ParseExact(Text(value, type), OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            ScalarKind.Interval => TimeSpan.Parse(Text(value, type), CultureInfo.InvariantCulture),
            _ => Text(value, type),
         };
      }
      catch (Exception e) when (e is FormatException or OverflowException or InvalidOperationException)
      {
         throw Wrong(value, type);
      }
   }

   /// <summary>A value as the API read it (JSON, as a <see cref="JsonElement"/>); a CLR value is read as JSON would write it.</summary>
   public static JsonElement Json(object? value) => value is JsonElement element ? element : JsonSerializer.SerializeToElement(value);

   /// <summary>Whether text is a date alone (<c>2026-03-01</c>), as a date filter of a date-time column gives it.</summary>
   public static bool IsDateOnly(JsonElement value, out DateOnly date)
   {
      date = default;
      return value.ValueKind == JsonValueKind.String &&
             DateOnly.TryParseExact(value.GetString(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
   }

   private static long Whole(JsonElement value, ScalarType type) =>
      value.ValueKind == JsonValueKind.Number ? value.GetInt64() : long.Parse(Text(value, type), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

   private static double Floating(JsonElement value, ScalarType type)
   {
      if (value.ValueKind == JsonValueKind.Number) { return value.GetDouble(); }
      return Text(value, type) switch
      {
         "NaN" => double.NaN,
         "Infinity" => double.PositiveInfinity,
         "-Infinity" => double.NegativeInfinity,
         string text => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
      };
   }

   private static string Text(JsonElement value, ScalarType type) =>
      value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Wrong(value, type);

   private static ValueFormatException Wrong(JsonElement value, ScalarType type)
   {
      string shown = value.GetRawText();
      if (shown.Length > 60) { shown = shown[..57] + "..."; }
      return new ValueFormatException($"{shown} isn't {Describe(type)}");
   }

   /// <summary>What a value of the type looks like, for messages.</summary>
   public static string Describe(ScalarType type) => type.Kind switch
   {
      ScalarKind.Boolean => "true or false",
      ScalarKind.Int16 or ScalarKind.Int32 or ScalarKind.Int64 => "a whole number",
      ScalarKind.Decimal or ScalarKind.Single or ScalarKind.Double => "a number",
      ScalarKind.Binary => "base64 text",
      ScalarKind.Guid => "a guid",
      ScalarKind.Date => "a date (yyyy-MM-dd)",
      ScalarKind.Time => "a time (HH:mm:ss)",
      ScalarKind.DateTime => "a date-time (yyyy-MM-ddTHH:mm:ss)",
      ScalarKind.DateTimeOffset => "a date-time with an offset (yyyy-MM-ddTHH:mm:ss+02:00)",
      ScalarKind.Interval => "an interval (d.hh:mm:ss)",
      _ => "text",
   };
}
