using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace GalaxyData.Query.Sqlite;

/// <summary>
/// SQLite sources. Values are stored by value, not declared type, so parameters are sent in the form stored
/// data has: whole decimals as integers and the others as reals (as text they would compare as text, above every
/// number; as reals, large whole numbers lose digits), dates and times as ISO text, guids as upper-case text.
/// </summary>
public sealed class SqliteSourceProvider : SourceProvider
{
   public static SqliteSourceProvider Instance { get; } = new();

   public override string ProviderKind => "sqlite";

   public override SqlDialect Dialect => SqlDialect.Sqlite;

   public override ISchemaIntrospector Introspector { get; } = new SqliteSchemaIntrospector();

   /// <summary>
   /// Interrupts the command's connection (<c>sqlite3_interrupt</c>): Microsoft.Data.Sqlite's <c>Cancel</c> does
   /// nothing, and checks a token only before a statement starts, so a busy one would run to its end.
   /// </summary>
   public override void CancelCommand(DbCommand command)
   {
      ArgumentNullException.ThrowIfNull(command);
      if (command.Connection is SqliteConnection { State: ConnectionState.Open, Handle: { } handle }) { raw.sqlite3_interrupt(handle); }
   }

   /// <summary>
   /// Foreign keys are checked as each statement runs, or not, as the source says (see
   /// <see cref="SourceInfo.EnforceForeignKeys"/>). SQLite takes the setting only outside a transaction, and it stays
   /// with the connection, a pooled one too.
   /// </summary>
   public override async ValueTask PrepareWriteAsync(DbConnection connection, SourceInfo source, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      if (source.EnforceForeignKeys is { } enforce)
      {
         await connection.ExecuteAsync(enforce ? "PRAGMA foreign_keys = ON" : "PRAGMA foreign_keys = OFF", cancellationToken).ConfigureAwait(false);
      }
   }

   public override void BindParameter(DbParameter parameter, object? value, ScalarType type)
   {
      ArgumentNullException.ThrowIfNull(parameter);
      parameter.Value = value switch
      {
         null => DBNull.Value,
         decimal number when decimal.Truncate(number) == number && number >= long.MinValue && number <= long.MaxValue => (long)number,
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
