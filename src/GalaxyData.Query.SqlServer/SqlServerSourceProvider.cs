using System;
using System.Data;
using System.Data.Common;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;
using Microsoft.Data.SqlClient;

namespace GalaxyData.Query.SqlServer;

/// <summary>
/// SQL Server sources, through Microsoft.Data.SqlClient. Parameters are typed as the columns they are compared with,
/// so SQL Server doesn't convert the column to the parameter's type and lose its index: text compared with a
/// <c>varchar(20)</c> column is sent as <c>varchar(20)</c> (longer only when the value is), decimals with the
/// column's precision and scale (more when the value needs them), date-times as <c>datetime2</c>, or as
/// <c>datetime</c> when compared with one: SQL Server compares a <c>datetime</c> with a <c>datetime2</c> in its 1/300
/// seconds, which a <c>datetime2</c> holds only when they are whole hundredths. Values of the CLR types SqlClient can't
/// read without Microsoft.SqlServer.Types (<c>hierarchyid</c>, <c>geometry</c>, <c>geography</c>) are read as their bytes.
/// </summary>
public sealed class SqlServerSourceProvider : SourceProvider
{
   /// <summary>The longest <c>nvarchar</c> and <c>varchar</c> (in characters) short of <c>max</c>.</summary>
   private const int MaxUnicode = 4000;

   private const int MaxAnsi = 8000;

   public static SqlServerSourceProvider Instance { get; } = new();

   public override string ProviderKind => SqlServerSchemaIntrospector.ProviderKind;

   public override SqlDialect Dialect => SqlDialect.SqlServer;

   public override ISchemaIntrospector Introspector { get; } = new SqlServerSchemaIntrospector();

   public override void BindParameter(DbParameter parameter, object? value, ScalarType type)
   {
      ArgumentNullException.ThrowIfNull(parameter);
      if (parameter is not SqlParameter sql)
      {
         base.BindParameter(parameter, value, type);
         return;
      }
      switch (type.Kind)
      {
         case ScalarKind.Boolean:
            sql.SqlDbType = SqlDbType.Bit;
            break;
         case ScalarKind.Int16:
            sql.SqlDbType = SqlDbType.SmallInt;
            break;
         case ScalarKind.Int32:
            sql.SqlDbType = SqlDbType.Int;
            break;
         case ScalarKind.Int64:
            sql.SqlDbType = SqlDbType.BigInt;
            break;
         case ScalarKind.Decimal:
            sql.SqlDbType = SqlDbType.Decimal;
            (sql.Precision, sql.Scale) = DecimalShape(value as decimal?, type);
            break;
         case ScalarKind.Single:
            sql.SqlDbType = SqlDbType.Real;
            break;
         case ScalarKind.Double:
            sql.SqlDbType = SqlDbType.Float;
            break;
         case ScalarKind.String or ScalarKind.Json:
            // Text a varchar's code page may not hold is sent as nvarchar, which SQL Server compares by converting
            // the column: as varchar, SqlClient would send 'a?b' for 'a日b', and it would match.
            bool ansi = type.IsAnsi && type.Kind == ScalarKind.String && (value is not string text || System.Text.Ascii.IsValid(text));
            sql.SqlDbType = ansi ? SqlDbType.VarChar : SqlDbType.NVarChar;
            sql.Size = Size(type.Length, (value as string)?.Length ?? 0, ansi ? MaxAnsi : MaxUnicode);
            break;
         case ScalarKind.Binary:
            sql.SqlDbType = SqlDbType.VarBinary;
            sql.Size = Size(type.Length, (value as byte[])?.Length ?? 0, MaxAnsi);
            break;
         case ScalarKind.Guid:
            sql.SqlDbType = SqlDbType.UniqueIdentifier;
            break;
         case ScalarKind.Date:
            sql.SqlDbType = SqlDbType.Date;
            break;
         case ScalarKind.Time:
            sql.SqlDbType = SqlDbType.Time;
            break;
         case ScalarKind.DateTime:
            sql.SqlDbType = SqlDbType.DateTime2;
            sql.Scale = 7;
            break;
         case ScalarKind.DateTimeOffset:
            sql.SqlDbType = SqlDbType.DateTimeOffset;
            sql.Scale = 7;
            break;
      }
      sql.Value = (value, type.Kind) switch
      {
         (null, _) => DBNull.Value,
         (DateOnly date, _) => date.ToDateTime(TimeOnly.MinValue),
         (TimeOnly time, _) => time.ToTimeSpan(),
         (DateTime dateTime, ScalarKind.DateTimeOffset) => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
         (DateTimeOffset offset, ScalarKind.DateTime) => offset.UtcDateTime,
         (DateTime dateTime, ScalarKind.DateTime) => DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),
         _ => value,
      };
   }

   public override void BindParameter(DbParameter parameter, object? value, SqlParameterSlot slot)
   {
      ArgumentNullException.ThrowIfNull(slot);
      BindParameter(parameter, value, slot.Type);
      if (slot.Type.Kind == ScalarKind.DateTime && string.Equals(slot.ColumnType, "datetime", StringComparison.OrdinalIgnoreCase) && parameter is SqlParameter sql)
      {
         sql.SqlDbType = SqlDbType.DateTime;
      }
   }

   /// <summary>
   /// The column's length, or the longest short of <c>max</c> when it has none (so values of any length share a plan);
   /// never shorter than the value, which SqlClient would cut to the parameter's size.
   /// </summary>
   private static int Size(int declared, int needed, int limit) =>
      declared > 0 && needed <= declared ? declared : needed <= limit ? limit : -1;

   /// <summary>The column's precision and scale, widened to hold the value when it doesn't fit them.</summary>
   private static (byte Precision, byte Scale) DecimalShape(decimal? value, ScalarType type)
   {
      int precision = type.Precision > 0 ? type.Precision : 18;
      int scale = type.Precision > 0 ? type.Scale : 0;
      if (value is { } number)
      {
         int valueScale = number.Scale;
         decimal whole = decimal.Truncate(Math.Abs(number));
         int digits = whole == 0 ? 1 : (int)Math.Floor(Math.Log10((double)whole)) + 1;
         scale = Math.Max(scale, valueScale);
         precision = Math.Max(precision - type.Scale + scale, digits + scale);
      }
      return ((byte)Math.Min(precision, 38), (byte)Math.Min(scale, 38));
   }

   public override object? ReadValue(DbDataReader reader, int ordinal)
   {
      ArgumentNullException.ThrowIfNull(reader);
      if (reader.IsDBNull(ordinal)) { return null; }
      if (reader is SqlDataReader sql && IsClrType(sql.GetDataTypeName(ordinal))) { return sql.GetSqlBytes(ordinal).Value; }
      return reader.GetValue(ordinal);
   }

   private static bool IsClrType(string name) =>
      name.EndsWith("hierarchyid", StringComparison.OrdinalIgnoreCase) || name.EndsWith("geometry", StringComparison.OrdinalIgnoreCase) ||
      name.EndsWith("geography", StringComparison.OrdinalIgnoreCase);
}
