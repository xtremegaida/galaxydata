using System;
using System.Data.Common;
using System.Globalization;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sqlite;

/// <summary>
/// SQLite sources. Values are stored by value, not declared type, so parameters are sent in the form stored
/// data has: decimals as reals (as text they would compare as text, above every number), dates and times as ISO
/// text, guids as upper-case text.
/// </summary>
public sealed class SqliteSourceProvider : SourceProvider
{
   public static SqliteSourceProvider Instance { get; } = new();

   public override string ProviderKind => "sqlite";

   public override SqlDialect Dialect => SqlDialect.Sqlite;

   public override ISchemaIntrospector Introspector { get; } = new SqliteSchemaIntrospector();

   public override void BindParameter(DbParameter parameter, object? value, ScalarType type)
   {
      ArgumentNullException.ThrowIfNull(parameter);
      parameter.Value = value switch
      {
         null => DBNull.Value,
         decimal number => (double)number,
         bool flag => flag ? 1L : 0L,
         Guid guid => guid.ToString("D").ToUpperInvariant(),
         DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
         TimeOnly time => time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
         DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
         DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
         _ => value,
      };
   }
}
