using System;
using System.Globalization;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sqlite;

/// <summary>
/// Maps a SQLite declared column type to a logical type. Well-known names come first (so <c>DATETIME</c> is a
/// date-time, not text), then SQLite's own affinity rules. Every integer type maps to int64, which is how
/// SQLite stores them.
/// </summary>
public static class SqliteTypeMapper
{
   public static ScalarType Map(string? declared, bool nullable)
   {
      (string name, int[] arguments) = Split(declared);
      ScalarType type = name switch
      {
         "" => ScalarType.Unknown,
         "BOOLEAN" or "BOOL" or "BIT" => ScalarType.Boolean,
         "DATE" => ScalarType.Date,
         "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "TIMESTAMP" => ScalarType.DateTime,
         "DATETIMEOFFSET" or "TIMESTAMPTZ" => ScalarType.DateTimeOffset,
         "TIME" => ScalarType.Time,
         "GUID" or "UUID" or "UNIQUEIDENTIFIER" => ScalarType.Guid,
         "JSON" or "JSONB" => ScalarType.Json,
         "DECIMAL" or "NUMERIC" or "NUMBER" or "MONEY" or "SMALLMONEY" => arguments.Length switch
         {
            >= 2 => ScalarType.Decimal(Clamp(arguments[0]), Clamp(arguments[1])),
            1 => ScalarType.Decimal(Clamp(arguments[0]), 0),
            _ => ScalarType.Decimal(),
         },
         "INTERVAL" => ScalarType.Text(),
         _ => ByAffinity(name, arguments),
      };
      return type.WithNullable(nullable);
   }

   private static ScalarType ByAffinity(string name, int[] arguments)
   {
      if (name.Contains("INT", StringComparison.Ordinal)) { return ScalarType.Int64; }
      if (name.Contains("CHAR", StringComparison.Ordinal) || name.Contains("CLOB", StringComparison.Ordinal) ||
          name.Contains("TEXT", StringComparison.Ordinal) || name == "STRING")
      {
         return ScalarType.Text(arguments.Length > 0 ? arguments[0] : -1);
      }
      if (name.Contains("BLOB", StringComparison.Ordinal) || name is "BINARY" or "VARBINARY")
      {
         return arguments.Length > 0 ? ScalarType.Binary with { Length = arguments[0] } : ScalarType.Binary;
      }
      if (name.Contains("REAL", StringComparison.Ordinal) || name.Contains("FLOA", StringComparison.Ordinal) ||
          name.Contains("DOUB", StringComparison.Ordinal))
      {
         return ScalarType.Double;
      }
      return ScalarType.Unknown;
   }

   private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 38);

   private static (string Name, int[] Arguments) Split(string? declared)
   {
      if (string.IsNullOrWhiteSpace(declared)) { return (string.Empty, []); }
      string text = declared.Trim().ToUpperInvariant();
      int open = text.IndexOf('(', StringComparison.Ordinal);
      if (open < 0) { return (text, []); }
      string name = text[..open].Trim();
      int close = text.IndexOf(')', open);
      string inside = close > open ? text[(open + 1)..close] : text[(open + 1)..];
      string[] parts = inside.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      int[] arguments = new int[parts.Length];
      for (int i = 0; i < parts.Length; i++)
      {
         if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out arguments[i])) { return (name, []); }
      }
      return (name, arguments);
   }
}
