using System;
using System.Globalization;

namespace GalaxyData.Query.Types;

/// <summary>
/// How logical types combine: numeric promotion (int16 &lt; int32 &lt; int64 &lt; decimal &lt; double), which types
/// compare, the common type of branches, and converting constants to the type they are compared with.
/// </summary>
public static class TypeRules
{
   private static int NumericRank(ScalarKind kind) => kind switch
   {
      ScalarKind.Int16 => 1,
      ScalarKind.Int32 => 2,
      ScalarKind.Int64 => 3,
      ScalarKind.Decimal => 4,
      ScalarKind.Single => 5,
      ScalarKind.Double => 6,
      _ => 0,
   };

   /// <summary>The type arithmetic on two numbers produces; null when either is not a number.</summary>
   public static ScalarType? PromoteNumeric(ScalarType a, ScalarType b)
   {
      if (!a.IsNumeric || !b.IsNumeric) { return null; }
      bool nullable = a.Nullable || b.Nullable;
      if (a.Kind == ScalarKind.Single && b.Kind == ScalarKind.Single) { return ScalarType.Single.WithNullable(nullable); }
      ScalarType wider = NumericRank(a.Kind) >= NumericRank(b.Kind) ? a : b;
      if (wider.Kind == ScalarKind.Single) { return ScalarType.Double.WithNullable(nullable); }
      if (wider.Kind == ScalarKind.Decimal)
      {
         bool same = a.Kind == b.Kind && a.Precision == b.Precision && a.Scale == b.Scale;
         return (same ? wider : ScalarType.Decimal()).WithNullable(nullable);
      }
      return new ScalarType(wider.Kind, nullable);
   }

   public static bool IsDateLike(ScalarKind kind) => kind is ScalarKind.Date or ScalarKind.DateTime or ScalarKind.DateTimeOffset;

   public static bool HasTimeOfDay(ScalarKind kind) => kind is ScalarKind.Time or ScalarKind.DateTime or ScalarKind.DateTimeOffset;

   /// <summary>Whether values of the two types can be compared; <paramref name="ordering"/> asks for &lt; and &gt; too.</summary>
   public static bool AreComparable(ScalarType a, ScalarType b, bool ordering, out string? reason)
   {
      reason = null;
      if (a.Kind is ScalarKind.Unknown or ScalarKind.Json || b.Kind is ScalarKind.Unknown or ScalarKind.Json)
      {
         reason = "values of an unknown or JSON type can only be tested for null; convert them first, e.g. toString(x)";
         return false;
      }
      bool comparable =
         (a.IsNumeric && b.IsNumeric) ||
         (IsDateLike(a.Kind) && IsDateLike(b.Kind)) ||
         a.Kind == b.Kind;
      if (!comparable)
      {
         reason = $"{Describe(a)} can't be compared with {Describe(b)}";
         return false;
      }
      if (ordering && a.Kind is ScalarKind.Guid or ScalarKind.Binary)
      {
         reason = $"{KindName(a)} values have no order that every database agrees on";
         return false;
      }
      return true;
   }

   public static bool IsOrderable(ScalarType type) =>
      type.Kind is not (ScalarKind.Unknown or ScalarKind.Json or ScalarKind.Guid or ScalarKind.Binary);

   /// <summary>A type both values fit, for the branches of a conditional or the arguments of coalesce.</summary>
   public static ScalarType? Unify(ScalarType a, ScalarType b)
   {
      bool nullable = a.Nullable || b.Nullable;
      if (a.Kind == ScalarKind.Unknown) { return b.WithNullable(nullable); }
      if (b.Kind == ScalarKind.Unknown) { return a.WithNullable(nullable); }
      if (a.Kind == b.Kind)
      {
         return a.Kind switch
         {
            ScalarKind.String => (a.Length == b.Length && a.IsAnsi == b.IsAnsi ? a : ScalarType.Text()).WithNullable(nullable),
            ScalarKind.Decimal => PromoteNumeric(a, b),
            _ => a.WithNullable(nullable),
         };
      }
      if (a.IsNumeric && b.IsNumeric) { return PromoteNumeric(a, b); }
      if (IsDateLike(a.Kind) && IsDateLike(b.Kind))
      {
         ScalarKind kind = a.Kind == ScalarKind.DateTimeOffset || b.Kind == ScalarKind.DateTimeOffset ? ScalarKind.DateTimeOffset : ScalarKind.DateTime;
         return new ScalarType(kind, nullable);
      }
      return null;
   }

   /// <summary>A short description of a type for messages, with its article: "a number", "text", "a date", ...</summary>
   public static string Describe(ScalarType type) => type.Kind switch
   {
      ScalarKind.Boolean => "a true/false value",
      ScalarKind.Int16 or ScalarKind.Int32 or ScalarKind.Int64 => "a whole number",
      ScalarKind.Decimal or ScalarKind.Single or ScalarKind.Double => "a number",
      ScalarKind.String => "text",
      ScalarKind.Binary => "binary data",
      ScalarKind.Guid => "a guid",
      ScalarKind.Date => "a date",
      ScalarKind.Time => "a time",
      ScalarKind.DateTime => "a date-time",
      ScalarKind.DateTimeOffset => "a date-time with offset",
      ScalarKind.Interval => "an interval",
      ScalarKind.Json => "a JSON value",
      _ => "a value of unknown type",
   };

   /// <summary>The kind of values a type holds, for "{kind} values ..." messages: "guid", "text", "binary".</summary>
   public static string KindName(ScalarType type) => type.Kind switch
   {
      ScalarKind.Boolean => "true/false",
      ScalarKind.Int16 or ScalarKind.Int32 or ScalarKind.Int64 => "whole-number",
      ScalarKind.Decimal or ScalarKind.Single or ScalarKind.Double => "number",
      ScalarKind.String => "text",
      ScalarKind.Binary => "binary",
      ScalarKind.Guid => "guid",
      ScalarKind.Date => "date",
      ScalarKind.Time => "time",
      ScalarKind.DateTime => "date-time",
      ScalarKind.DateTimeOffset => "date-time-with-offset",
      ScalarKind.Interval => "interval",
      ScalarKind.Json => "JSON",
      _ => "unknown-type",
   };

   /// <summary>
   /// Converts a constant to <paramref name="target"/> when that loses nothing: an integer to a narrower integer it
   /// fits, a number to decimal or double, text to a date, time, guid or to text of the target's length. Returns
   /// false when no conversion applies; <paramref name="error"/> is set when the text doesn't parse.
   /// </summary>
   public static bool TryConvertConstant(object? value, ScalarType from, ScalarType target, out object? converted, out ScalarType type, out string? error)
   {
      converted = value;
      type = from;
      error = null;
      if (value == null)
      {
         if (target.Kind == ScalarKind.Unknown) { return false; }
         type = target.AsNullable();
         return true;
      }
      // Whole numbers of every width convert as a long does (a parameter given as an int, for a bigint column).
      if (value is sbyte or byte or short or ushort or int or uint)
      {
         value = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
         converted = value;
      }
      switch (value)
      {
         case long integer:
            switch (target.Kind)
            {
               case ScalarKind.Int16 when integer is >= short.MinValue and <= short.MaxValue:
                  converted = (short)integer;
                  break;
               case ScalarKind.Int32 when integer is >= int.MinValue and <= int.MaxValue:
                  converted = (int)integer;
                  break;
               case ScalarKind.Int64:
                  break;
               case ScalarKind.Decimal:
                  converted = (decimal)integer;
                  break;
               case ScalarKind.Single or ScalarKind.Double:
                  converted = (double)integer;
                  break;
               default:
                  return false;
            }
            break;
         case decimal exact:
            switch (target.Kind)
            {
               case ScalarKind.Decimal:
                  break;
               case ScalarKind.Single or ScalarKind.Double:
                  converted = (double)exact;
                  break;
               default:
                  return false;
            }
            break;
         case double real:
            switch (target.Kind)
            {
               case ScalarKind.Decimal when !double.IsNaN(real) && !double.IsInfinity(real) && Math.Abs(real) < 7.9e28:
                  converted = (decimal)real;
                  break;
               case ScalarKind.Single or ScalarKind.Double:
                  break;
               default:
                  return false;
            }
            break;
         case string text when from.Kind == ScalarKind.String:
            if (!TryParseText(text, target.Kind, out converted, out error))
            {
               converted = value;
               return false;
            }
            break;
         default:
            if (from.Kind != target.Kind) { return false; }
            break;
      }
      type = target.WithNullable(false);
      return true;
   }

   private static bool TryParseText(string text, ScalarKind target, out object? converted, out string? error)
   {
      error = null;
      converted = text;
      CultureInfo invariant = CultureInfo.InvariantCulture;
      switch (target)
      {
         case ScalarKind.String:
            return true;
         case ScalarKind.Date:
            if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", invariant, DateTimeStyles.None, out DateOnly date)) { converted = date; return true; }
            error = $"'{text}' is not a date; write dates as 'yyyy-MM-dd'";
            return false;
         case ScalarKind.DateTime:
            if (DateTime.TryParse(text.Trim(), invariant, DateTimeStyles.AllowWhiteSpaces, out DateTime dateTime) && !HasOffset(text))
            {
               converted = DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
               return true;
            }
            error = $"'{text}' is not a date-time; write it as 'yyyy-MM-dd HH:mm:ss'";
            return false;
         case ScalarKind.DateTimeOffset:
            if (DateTimeOffset.TryParse(text.Trim(), invariant, DateTimeStyles.AssumeUniversal, out DateTimeOffset offset)) { converted = offset; return true; }
            error = $"'{text}' is not a date-time with offset; write it as 'yyyy-MM-dd HH:mm:ss+02:00'";
            return false;
         case ScalarKind.Time:
            if (TimeOnly.TryParse(text.Trim(), invariant, DateTimeStyles.None, out TimeOnly time)) { converted = time; return true; }
            error = $"'{text}' is not a time; write it as 'HH:mm:ss'";
            return false;
         case ScalarKind.Guid:
            if (Guid.TryParse(text.Trim(), out Guid guid)) { converted = guid; return true; }
            error = $"'{text}' is not a guid";
            return false;
         default:
            return false;
      }
   }

   private static bool HasOffset(string text)
   {
      string trimmed = text.Trim();
      if (trimmed.EndsWith('Z')) { return true; }
      int t = trimmed.IndexOfAny(['T', ' ']);
      if (t < 0) { return false; }
      int sign = trimmed.LastIndexOfAny(['+', '-']);
      return sign > t;
   }

   /// <summary>Whether a parameter of type <paramref name="from"/> can be converted to <paramref name="target"/> when the query runs.</summary>
   public static bool CanConvertParameter(ScalarType from, ScalarType target)
   {
      if (from.Kind == target.Kind || from.Kind == ScalarKind.Unknown) { return true; }
      if (from.IsNumeric && target.IsNumeric) { return true; }
      return from.Kind == ScalarKind.String &&
         target.Kind is ScalarKind.Date or ScalarKind.DateTime or ScalarKind.DateTimeOffset or ScalarKind.Time or ScalarKind.Guid;
   }
}
