using System;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.DuckDb;

/// <summary>
/// Maps DuckDB type names (as <c>duckdb_columns().data_type</c> spells them) to logical types. Unsigned types
/// widen to the next signed type that holds them; nested types (lists, structs, maps, unions) are unknown.
/// </summary>
public static class DuckDbTypeMapper
{
   public static ScalarType Map(string dataType, int? precision, int? scale, bool nullable)
   {
      ArgumentNullException.ThrowIfNull(dataType);
      string name = dataType.Trim().ToUpperInvariant();
      if (name.EndsWith(']') || name.StartsWith("STRUCT", StringComparison.Ordinal) ||
          name.StartsWith("MAP", StringComparison.Ordinal) || name.StartsWith("UNION", StringComparison.Ordinal))
      {
         return ScalarType.Unknown.WithNullable(nullable);
      }
      if (name.StartsWith("ENUM", StringComparison.Ordinal)) { return ScalarType.Text().WithNullable(nullable); }
      if (name.StartsWith("DECIMAL", StringComparison.Ordinal) || name.StartsWith("NUMERIC", StringComparison.Ordinal))
      {
         return ScalarType.Decimal(Math.Clamp(precision ?? 18, 1, 38), Math.Clamp(scale ?? 3, 0, 38)).WithNullable(nullable);
      }
      if (name.StartsWith("VARCHAR", StringComparison.Ordinal)) { name = "VARCHAR"; }

      ScalarType type = name switch
      {
         "BOOLEAN" or "BOOL" => ScalarType.Boolean,
         "TINYINT" or "SMALLINT" or "UTINYINT" => ScalarType.Int16,
         "INTEGER" or "USMALLINT" => ScalarType.Int32,
         "BIGINT" or "UINTEGER" => ScalarType.Int64,
         "UBIGINT" => ScalarType.Decimal(20, 0),
         "HUGEINT" or "UHUGEINT" => ScalarType.Decimal(38, 0),
         "FLOAT" or "REAL" => ScalarType.Single,
         "DOUBLE" => ScalarType.Double,
         "VARCHAR" or "TEXT" or "STRING" or "BIT" => ScalarType.Text(),
         "BLOB" => ScalarType.Binary,
         "UUID" => ScalarType.Guid,
         "DATE" => ScalarType.Date,
         "TIME" => ScalarType.Time,
         "TIMESTAMP" or "TIMESTAMP_S" or "TIMESTAMP_MS" or "TIMESTAMP_NS" or "DATETIME" => ScalarType.DateTime,
         "TIMESTAMP WITH TIME ZONE" or "TIMESTAMPTZ" => ScalarType.DateTimeOffset,
         "INTERVAL" => ScalarType.Interval,
         "JSON" => ScalarType.Json,
         _ => ScalarType.Unknown,
      };
      return type.WithNullable(nullable);
   }
}
