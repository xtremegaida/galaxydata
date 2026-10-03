using System;
using System.Globalization;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.SqlServer;

/// <summary>
/// Maps a SQL Server system type (an alias type maps as the type it is based on) to a logical type. <c>varchar</c>
/// and <c>char</c> are ansi text, so values compared with them are sent as <c>varchar</c> and keep index seeks.
/// <c>rowversion</c> is binary; <c>xml</c>, <c>sql_variant</c> and the CLR types (<c>hierarchyid</c>,
/// <c>geometry</c>, <c>geography</c>) are unknown.
/// </summary>
public static class SqlServerTypeMapper
{
   /// <param name="type">The system type's name, as <c>sys.types</c> has it: <c>nvarchar</c>.</param>
   /// <param name="maxLength">The column's <c>sys.columns.max_length</c>: bytes, -1 for <c>max</c>.</param>
   public static ScalarType Map(string type, int maxLength, int precision, int scale, bool nullable)
   {
      ArgumentNullException.ThrowIfNull(type);
      ScalarType mapped = type.ToLowerInvariant() switch
      {
         "bit" => ScalarType.Boolean,
         "tinyint" or "smallint" => ScalarType.Int16,
         "int" => ScalarType.Int32,
         "bigint" => ScalarType.Int64,
         "decimal" or "numeric" => ScalarType.Decimal(Math.Clamp(precision, 1, 38), Math.Clamp(scale, 0, 38)),
         "money" => ScalarType.Decimal(19, 4),
         "smallmoney" => ScalarType.Decimal(10, 4),
         "real" => ScalarType.Single,
         "float" => precision is > 0 and <= 24 ? ScalarType.Single : ScalarType.Double,
         "char" or "varchar" => ScalarType.Text(maxLength, ansi: true),
         "text" => ScalarType.Text(ansi: true),
         "nchar" or "nvarchar" => ScalarType.Text(maxLength < 0 ? -1 : maxLength / 2),
         "ntext" => ScalarType.Text(),
         "binary" or "varbinary" => maxLength < 0 ? ScalarType.Binary : ScalarType.Binary with { Length = maxLength },
         "image" => ScalarType.Binary,
         "timestamp" or "rowversion" => ScalarType.Binary with { Length = 8 },
         "uniqueidentifier" => ScalarType.Guid,
         "date" => ScalarType.Date,
         "time" => ScalarType.Time,
         "datetime" or "datetime2" or "smalldatetime" => ScalarType.DateTime,
         "datetimeoffset" => ScalarType.DateTimeOffset,
         _ => ScalarType.Unknown,
      };
      return mapped.WithNullable(nullable);
   }

   /// <summary>The type as it is declared: <c>varchar(20)</c>, <c>nvarchar(max)</c>, <c>decimal(10,2)</c>, <c>datetime2(3)</c>.</summary>
   public static string Declared(string type, int maxLength, int precision, int scale)
   {
      ArgumentNullException.ThrowIfNull(type);
      string length = maxLength < 0 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture);
      return type.ToLowerInvariant() switch
      {
         "char" or "varchar" or "binary" or "varbinary" => $"{type}({length})",
         "nchar" or "nvarchar" => maxLength < 0 ? $"{type}(max)" : $"{type}({(maxLength / 2).ToString(CultureInfo.InvariantCulture)})",
         "decimal" or "numeric" => $"{type}({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})",
         "datetime2" or "datetimeoffset" or "time" => $"{type}({scale.ToString(CultureInfo.InvariantCulture)})",
         _ => type,
      };
   }
}
