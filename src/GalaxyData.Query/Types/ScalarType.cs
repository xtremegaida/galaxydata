using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalaxyData.Query.Types;

public enum ScalarKind : byte
{
   Unknown,
   Boolean,
   Int16,
   Int32,
   Int64,
   Decimal,
   Single,
   Double,
   String,
   Binary,
   Guid,
   Date,
   Time,
   DateTime,
   DateTimeOffset,
   Interval,
   Json,
}

/// <summary>
/// A logical scalar type, independent of any one database. Precision and scale apply to decimals (0 means
/// unconstrained), length to strings and binaries (-1 means unbounded), and <see cref="IsAnsi"/> marks
/// single-byte strings (SQL Server varchar), which matters for parameter typing.
/// </summary>
/// <remarks>
/// The text form is the kind in lower case with optional arguments and a trailing <c>?</c> when nullable:
/// <c>int64</c>, <c>decimal(10,2)?</c>, <c>string(50)</c>, <c>string(50,ansi)</c>, <c>binary(16)?</c>.
/// </remarks>
[JsonConverter(typeof(ScalarTypeJsonConverter))]
public readonly record struct ScalarType(
   ScalarKind Kind, bool Nullable = true, byte Precision = 0, byte Scale = 0, int Length = -1, bool IsAnsi = false)
{
   public static ScalarType Unknown => new(ScalarKind.Unknown);
   public static ScalarType Boolean => new(ScalarKind.Boolean);
   public static ScalarType Int16 => new(ScalarKind.Int16);
   public static ScalarType Int32 => new(ScalarKind.Int32);
   public static ScalarType Int64 => new(ScalarKind.Int64);
   public static ScalarType Single => new(ScalarKind.Single);
   public static ScalarType Double => new(ScalarKind.Double);
   public static ScalarType String => new(ScalarKind.String);
   public static ScalarType Binary => new(ScalarKind.Binary);
   public static ScalarType Guid => new(ScalarKind.Guid);
   public static ScalarType Date => new(ScalarKind.Date);
   public static ScalarType Time => new(ScalarKind.Time);
   public static ScalarType DateTime => new(ScalarKind.DateTime);
   public static ScalarType DateTimeOffset => new(ScalarKind.DateTimeOffset);
   public static ScalarType Interval => new(ScalarKind.Interval);
   public static ScalarType Json => new(ScalarKind.Json);

   public static ScalarType Decimal(int precision = 0, int scale = 0) =>
      new(ScalarKind.Decimal, Precision: checked((byte)precision), Scale: checked((byte)scale));

   public static ScalarType Text(int length = -1, bool ansi = false) =>
      new(ScalarKind.String, Length: length, IsAnsi: ansi);

   public ScalarType AsNullable() => this with { Nullable = true };

   public ScalarType AsNonNullable() => this with { Nullable = false };

   public ScalarType WithNullable(bool nullable) => this with { Nullable = nullable };

   public bool IsNumeric => Kind is ScalarKind.Int16 or ScalarKind.Int32 or ScalarKind.Int64 or ScalarKind.Decimal
      or ScalarKind.Single or ScalarKind.Double;

   public bool IsInteger => Kind is ScalarKind.Int16 or ScalarKind.Int32 or ScalarKind.Int64;

   public bool IsTemporal => Kind is ScalarKind.Date or ScalarKind.Time or ScalarKind.DateTime or ScalarKind.DateTimeOffset;

   public override string ToString()
   {
      StringBuilder text = new(24);
      text.Append(Name(Kind));
      switch (Kind)
      {
         case ScalarKind.Decimal when Precision > 0:
            text.Append('(').Append(Precision).Append(',').Append(Scale).Append(')');
            break;
         case ScalarKind.String when Length >= 0 || IsAnsi:
            text.Append('(').Append(Length >= 0 ? Length.ToString(CultureInfo.InvariantCulture) : "max");
            if (IsAnsi) { text.Append(",ansi"); }
            text.Append(')');
            break;
         case ScalarKind.Binary when Length >= 0:
            text.Append('(').Append(Length).Append(')');
            break;
      }
      if (Nullable) { text.Append('?'); }
      return text.ToString();
   }

   public static ScalarType Parse(string text)
   {
      if (TryParse(text, out ScalarType type)) { return type; }
      throw new FormatException($"'{text}' is not a scalar type; expected something like 'int64', 'decimal(10,2)?' or 'string(50,ansi)'");
   }

   public static bool TryParse([NotNullWhen(true)] string? text, out ScalarType type)
   {
      type = default;
      if (string.IsNullOrWhiteSpace(text)) { return false; }
      ReadOnlySpan<char> span = text.AsSpan().Trim();
      bool nullable = span.EndsWith("?");
      if (nullable) { span = span[..^1]; }

      ReadOnlySpan<char> arguments = default;
      int open = span.IndexOf('(');
      if (open >= 0)
      {
         if (!span.EndsWith(")")) { return false; }
         arguments = span[(open + 1)..^1];
         span = span[..open];
      }
      if (!TryKind(span.Trim(), out ScalarKind kind)) { return false; }
      type = new ScalarType(kind, nullable);
      if (arguments.IsEmpty) { return open < 0; }

      Span<Range> parts = stackalloc Range[3];
      int count = arguments.Split(parts, ',', StringSplitOptions.TrimEntries);
      switch (kind)
      {
         case ScalarKind.Decimal:
            if (count != 2 ||
                !byte.TryParse(arguments[parts[0]], NumberStyles.None, CultureInfo.InvariantCulture, out byte precision) ||
                !byte.TryParse(arguments[parts[1]], NumberStyles.None, CultureInfo.InvariantCulture, out byte scale))
            {
               return false;
            }
            type = type with { Precision = precision, Scale = scale };
            return true;
         case ScalarKind.String:
            if (count is < 1 or > 2) { return false; }
            if (!TryLength(arguments[parts[0]], out int length)) { return false; }
            bool ansi = false;
            if (count == 2)
            {
               if (!arguments[parts[1]].Equals("ansi", StringComparison.OrdinalIgnoreCase)) { return false; }
               ansi = true;
            }
            type = type with { Length = length, IsAnsi = ansi };
            return true;
         case ScalarKind.Binary:
            if (count != 1 || !TryLength(arguments[parts[0]], out int size)) { return false; }
            type = type with { Length = size };
            return true;
         default:
            return false;
      }
   }

   private static bool TryLength(ReadOnlySpan<char> text, out int length)
   {
      if (text.Equals("max", StringComparison.OrdinalIgnoreCase)) { length = -1; return true; }
      return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out length);
   }

   private static string Name(ScalarKind kind) => kind switch
   {
      ScalarKind.Unknown => "unknown",
      ScalarKind.Boolean => "boolean",
      ScalarKind.Int16 => "int16",
      ScalarKind.Int32 => "int32",
      ScalarKind.Int64 => "int64",
      ScalarKind.Decimal => "decimal",
      ScalarKind.Single => "single",
      ScalarKind.Double => "double",
      ScalarKind.String => "string",
      ScalarKind.Binary => "binary",
      ScalarKind.Guid => "guid",
      ScalarKind.Date => "date",
      ScalarKind.Time => "time",
      ScalarKind.DateTime => "datetime",
      ScalarKind.DateTimeOffset => "datetimeoffset",
      ScalarKind.Interval => "interval",
      ScalarKind.Json => "json",
      _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
   };

   private static bool TryKind(ReadOnlySpan<char> name, out ScalarKind kind)
   {
      foreach (ScalarKind candidate in Enum.GetValues<ScalarKind>())
      {
         if (name.Equals(Name(candidate), StringComparison.OrdinalIgnoreCase))
         {
            kind = candidate;
            return true;
         }
      }
      kind = default;
      return false;
   }
}

public sealed class ScalarTypeJsonConverter : JsonConverter<ScalarType>
{
   public override ScalarType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
   {
      string? text = reader.GetString();
      return ScalarType.TryParse(text, out ScalarType type) ? type : throw new JsonException($"Invalid scalar type '{text}'");
   }

   public override void Write(Utf8JsonWriter writer, ScalarType value, JsonSerializerOptions options) =>
      writer.WriteStringValue(value.ToString());
}
