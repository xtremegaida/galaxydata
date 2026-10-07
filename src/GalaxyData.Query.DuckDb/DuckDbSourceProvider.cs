using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Sql;

namespace GalaxyData.Query.DuckDb;

/// <summary>
/// DuckDB sources. Each connection works in UTC, like <c>now()</c>, so mixing date-times with and without an offset
/// gives the same answer on every machine. DuckDB.NET hands back <c>TIMESTAMPTZ</c> as a UTC <see cref="DateTime"/>,
/// read as an offset of zero here, and BLOBs as streams.
/// </summary>
public sealed class DuckDbSourceProvider : SourceProvider
{
   public static DuckDbSourceProvider Instance { get; } = new();

   public override string ProviderKind => "duckdb";

   public override SqlDialect Dialect => DuckDbDialect.Instance;

   public override ISchemaIntrospector Introspector { get; } = new DuckDbSchemaIntrospector();

   /// <summary>
   /// Sets the time zone to UTC where DuckDB has one to set: the ICU extension, when it is loaded or installed.
   /// Without it, DuckDB works in UTC anyway, and setting one would have DuckDB download ICU, or wait for the
   /// network to say it can't.
   /// </summary>
   public override async ValueTask PrepareConnectionAsync(DbConnection connection, CancellationToken cancellationToken)
   {
      string? icu = await connection.ScalarTextAsync(
         "SELECT count(*) FROM duckdb_extensions() WHERE extension_name = 'icu' AND (loaded OR installed)", cancellationToken).ConfigureAwait(false);
      if (icu != "1") { return; }
      try
      {
         await connection.ExecuteAsync("SET TimeZone = 'UTC'", cancellationToken).ConfigureAwait(false);
      }
      catch (DbException)
      {
         // ICU is installed but not loaded, and can't be (autoloading is off): DuckDB works in UTC.
      }
   }

   /// <summary>Rows are streamed as they are computed, rather than all computed before the first is read.</summary>
   public override void PrepareCommand(DbCommand command)
   {
      if (command is DuckDBCommand duck) { duck.UseStreamingMode = true; }
   }

   public override object? ReadValue(DbDataReader reader, int ordinal)
   {
      ArgumentNullException.ThrowIfNull(reader);
      if (reader.IsDBNull(ordinal)) { return null; }
      object value = reader.GetValue(ordinal);
      if (value is DateTime dateTime && string.Equals(reader.GetDataTypeName(ordinal), "TimestampTz", StringComparison.OrdinalIgnoreCase))
      {
         return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
      }
      return value;
   }
}
