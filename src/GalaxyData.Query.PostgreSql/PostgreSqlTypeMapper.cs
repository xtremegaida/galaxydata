using System;
using System.Globalization;
using System.Text.RegularExpressions;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.PostgreSql;

/// <summary>
/// Maps a PostgreSQL type, as <c>format_type</c> writes it, to a logical type. A domain maps as its base type and an
/// enum as text. Arrays, composites, ranges and types the language has no values for (<c>money</c>, which compares
/// with no number, <c>time with time zone</c>, network and geometric types, <c>xml</c>) are unknown.
/// </summary>
public static partial class PostgreSqlTypeMapper
{
   /// <param name="type">The type as <c>format_type(oid, typmod)</c> writes it: <c>character varying(40)</c>.</param>
   /// <param name="kind">Its <c>pg_type.typtype</c>: <c>b</c> base, <c>e</c> enum, <c>c</c> composite, <c>r</c> range, <c>m</c> multirange.</param>
   /// <param name="category">Its <c>pg_type.typcategory</c>: <c>A</c> for arrays.</param>
   public static ScalarType Map(string type, char kind, char category, bool nullable)
   {
      ArgumentNullException.ThrowIfNull(type);
      if (category == 'A' || kind is not ('b' or 'e')) { return ScalarType.Unknown.WithNullable(nullable); }
      if (kind == 'e') { return ScalarType.Text().WithNullable(nullable); }

      // Modifiers are written after the name, or inside it: numeric(10,2), timestamp(3) without time zone.
      Match modifiers = Modifiers().Match(type);
      string name = modifiers.Success ? type.Remove(modifiers.Index, modifiers.Length) : type;
      int first = modifiers.Success ? int.Parse(modifiers.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
      int second = modifiers.Success && modifiers.Groups[2].Success ? int.Parse(modifiers.Groups[2].Value, CultureInfo.InvariantCulture) : -1;

      ScalarType mapped = name.Trim() switch
      {
         "boolean" => ScalarType.Boolean,
         "smallint" => ScalarType.Int16,
         "integer" => ScalarType.Int32,
         "bigint" => ScalarType.Int64,
         "numeric" => first > 0 ? ScalarType.Decimal(Math.Min(first, 38), Math.Clamp(second, 0, Math.Min(first, 38))) : ScalarType.Decimal(),
         "real" => ScalarType.Single,
         "double precision" => ScalarType.Double,
         "text" or "citext" or "name" => ScalarType.Text(),
         "character varying" => ScalarType.Text(first),
         "character" => ScalarType.Text(first > 0 ? first : 1),
         "\"char\"" => ScalarType.Text(1),
         "bytea" => ScalarType.Binary,
         "uuid" => ScalarType.Guid,
         "date" => ScalarType.Date,
         "time without time zone" => ScalarType.Time,
         "timestamp without time zone" => ScalarType.DateTime,
         "timestamp with time zone" => ScalarType.DateTimeOffset,
         "json" or "jsonb" => ScalarType.Json,
         _ when name.StartsWith("interval", StringComparison.Ordinal) => ScalarType.Interval,
         _ => ScalarType.Unknown,
      };
      return mapped.WithNullable(nullable);
   }

   [GeneratedRegex(@"\((\d+)(?:,(\d+))?\)")]
   private static partial Regex Modifiers();
}
