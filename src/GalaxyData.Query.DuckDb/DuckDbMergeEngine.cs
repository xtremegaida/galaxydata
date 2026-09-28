using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Providers;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.DuckDb;

public sealed class DuckDbMergeOptions
{
   /// <summary>A database file to merge in; null to merge in memory, which spills to <see cref="TempDirectory"/> past <see cref="MemoryLimit"/>.</summary>
   public string? DatabasePath { get; init; }

   /// <summary>How much memory DuckDB may use, as DuckDB writes sizes (<c>2GB</c>, <c>512MB</c>); null for DuckDB's default, 80% of RAM.</summary>
   public string? MemoryLimit { get; init; }

   /// <summary>
   /// Where data that doesn't fit in memory goes. Null for a directory of the engine's own under the system's temp
   /// directory, removed when the engine is disposed.
   /// </summary>
   public string? TempDirectory { get; init; }

   /// <summary>The threads DuckDB may use; null for its default, one per core.</summary>
   public int? Threads { get; init; }
}

/// <summary>
/// The merge engine: one DuckDB database, in memory unless configured otherwise, shared by every query of the
/// process. Each query gets a session with a schema of its own (<c>q_1</c>, <c>q_2</c>, ...) for its fragments'
/// tables, loaded through appenders on connections of their own, and dropped when the session ends.
/// </summary>
public sealed class DuckDbMergeEngine : IMergeEngine, IDisposable
{
   private readonly DuckDbMergeOptions options;
   private readonly string? ownTempDirectory;
   private readonly Lock gate = new();
   private DuckDBConnection? root;
   private int sessions;
   private int active;
   private bool disposed;

   public DuckDbMergeEngine(DuckDbMergeOptions? options = null)
   {
      this.options = options ?? new DuckDbMergeOptions();
      if (this.options.TempDirectory == null && this.options.DatabasePath == null)
      {
         ownTempDirectory = Path.Combine(Path.GetTempPath(), "galaxydata-merge",
            $"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}");
      }
   }

   public SourceProvider Provider => DuckDbSourceProvider.Instance;

   /// <summary>The sessions open now: queries running, and results not yet disposed.</summary>
   public int ActiveSessions => Volatile.Read(ref active);

   /// <summary>The directory DuckDB spills to; null when it is DuckDB's default for the database file.</summary>
   public string? TempDirectory => options.TempDirectory ?? ownTempDirectory;

   public async ValueTask<IMergeSession> OpenSessionAsync(CancellationToken cancellationToken)
   {
      DuckDBConnection connection = Connect();
      try
      {
         await Provider.PrepareConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
         string schema = "q_" + Interlocked.Increment(ref sessions).ToString(CultureInfo.InvariantCulture);
         await connection.ExecuteAsync($"CREATE SCHEMA {schema}", cancellationToken).ConfigureAwait(false);
         // The merge SQL names its tables without a schema; this connection finds them in the session's.
         await connection.ExecuteAsync($"SET search_path = '{schema}'", cancellationToken).ConfigureAwait(false);
         Interlocked.Increment(ref active);
         return new Session(this, connection, schema);
      }
      catch
      {
         await connection.DisposeAsync().ConfigureAwait(false);
         throw;
      }
   }

   /// <summary>A new connection to the merge database, which is opened with its settings the first time.</summary>
   private DuckDBConnection Connect()
   {
      lock (gate)
      {
         ObjectDisposedException.ThrowIf(disposed, this);
         root ??= OpenRoot();
         DuckDBConnection connection = root.Duplicate();
         connection.Open();
         return connection;
      }
   }

   private DuckDBConnection OpenRoot()
   {
      DuckDBConnection connection = new("Data Source=" + (options.DatabasePath ?? ":memory:"));
      connection.Open();
      try
      {
         using DbCommand command = connection.CreateCommand();
         List<string> settings = [];
         if (options.MemoryLimit != null) { settings.Add($"SET memory_limit = {Text(options.MemoryLimit)}"); }
         if (TempDirectory != null) { settings.Add($"SET temp_directory = {Text(TempDirectory)}"); }
         if (options.Threads is { } threads) { settings.Add($"SET threads = {threads.ToString(CultureInfo.InvariantCulture)}"); }
         foreach (string setting in settings)
         {
            command.CommandText = setting;
            command.ExecuteNonQuery();
         }
         if (options.DatabasePath != null) { DropLeftovers(connection); }
         return connection;
      }
      catch
      {
         connection.Dispose();
         throw;
      }
   }

   /// <summary>A file database may still hold the sessions of a process that ended without closing them.</summary>
   private static void DropLeftovers(DuckDBConnection connection)
   {
      List<string> schemas = [];
      using (DbCommand command = connection.CreateCommand())
      {
         command.CommandText = "SELECT schema_name FROM information_schema.schemata WHERE starts_with(schema_name, 'q_')";
         using DbDataReader reader = command.ExecuteReader();
         while (reader.Read()) { schemas.Add(reader.GetString(0)); }
      }
      foreach (string schema in schemas)
      {
         using DbCommand drop = connection.CreateCommand();
         drop.CommandText = $"DROP SCHEMA {SqlDialect.DuckDb.QuoteIdentifier(schema)} CASCADE";
         drop.ExecuteNonQuery();
      }
   }

   private static string Text(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

   public void Dispose()
   {
      lock (gate)
      {
         disposed = true;
         root?.Dispose();
         root = null;
      }
      if (ownTempDirectory != null && Directory.Exists(ownTempDirectory))
      {
         try
         {
            Directory.Delete(ownTempDirectory, recursive: true);
         }
         catch (Exception e) when (e is IOException or UnauthorizedAccessException)
         {
            // Temp files still in use; the system's temp cleanup gets them.
         }
      }
   }

   private sealed class Session(DuckDbMergeEngine engine, DuckDBConnection connection, string schema) : IMergeSession
   {
      private int closed;

      public async ValueTask<IMergeTableWriter> CreateTableAsync(string name, IReadOnlyList<MergeColumn> columns, CancellationToken cancellationToken)
      {
         ArgumentNullException.ThrowIfNull(name);
         ArgumentNullException.ThrowIfNull(columns);
         DuckDBConnection loader = engine.Connect();
         try
         {
            string definitions = string.Join(", ", columns.Select(c => SqlDialect.DuckDb.QuoteIdentifier(c.Name) + " " + DuckDbTypeMapper.TypeName(c.Type)));
            await loader.ExecuteAsync($"CREATE TABLE {schema}.{SqlDialect.DuckDb.QuoteIdentifier(name)} ({definitions})", cancellationToken).ConfigureAwait(false);
            return new TableWriter(loader, loader.CreateAppender(schema, name), columns);
         }
         catch
         {
            await loader.DisposeAsync().ConfigureAwait(false);
            throw;
         }
      }

      public DbCommand CreateCommand()
      {
         DuckDBCommand command = connection.CreateCommand();
         command.UseStreamingMode = true;
         return command;
      }

      public async ValueTask DisposeAsync()
      {
         if (Interlocked.Exchange(ref closed, 1) != 0) { return; }
         try
         {
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE").ConfigureAwait(false);
         }
         catch (DbException)
         {
            // The database is going away, and the schema with it.
         }
         finally
         {
            await connection.DisposeAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref engine.active);
         }
      }
   }

   /// <summary>
   /// Appends rows through an appender. Each value is checked against its column before any of the row is appended:
   /// a row the appender takes only in part must be cleared before the appender is disposed, or DuckDB crashes.
   /// </summary>
   private sealed class TableWriter(DuckDBConnection connection, DuckDBAppender appender, IReadOnlyList<MergeColumn> columns) : IMergeTableWriter
   {
      private bool completed;

      public void Append(object?[] row)
      {
         ArgumentNullException.ThrowIfNull(row);
         if (row.Length != columns.Count) { throw new ArgumentException($"A row of {row.Length} values for a table of {columns.Count} columns", nameof(row)); }
         for (int i = 0; i < row.Length; i++) { row[i] = Stored(row[i], columns[i]); }
         try
         {
            IDuckDBAppenderRow line = appender.CreateRow();
            foreach (object? value in row) { line = Value(line, value); }
            line.EndRow();
         }
         catch
         {
            appender.Clear();
            throw;
         }
      }

      /// <summary>The value as its column stores it; an InvalidCastException, before anything is appended, when it can't be.</summary>
      private static object? Stored(object? value, MergeColumn column)
      {
         if (value == null) { return null; }
         ScalarType type = column.Type;
         bool fits = type.Kind switch
         {
            ScalarKind.Boolean => value is bool,
            ScalarKind.Int16 => value is short,
            ScalarKind.Int32 => value is int,
            ScalarKind.Int64 => value is long,
            ScalarKind.Decimal => value is decimal,
            ScalarKind.Single => value is float,
            ScalarKind.Double => value is double,
            ScalarKind.Binary => value is byte[],
            ScalarKind.Guid => value is Guid,
            ScalarKind.Date => value is DateOnly,
            ScalarKind.Time => value is TimeOnly,
            ScalarKind.DateTime => value is DateTime,
            ScalarKind.DateTimeOffset => value is DateTimeOffset,
            ScalarKind.Interval => value is TimeSpan,
            _ => value is string,
         };
         if (!fits)
         {
            throw new InvalidCastException($"The merge table's column '{column.Name}' holds {TypeRules.Describe(type)}, and a {value.GetType().Name} isn't one");
         }
         // Decimals of no precision are stored as doubles.
         return type.Kind == ScalarKind.Decimal && type.Precision == 0 ? (double)(decimal)value : value;
      }

      private static IDuckDBAppenderRow Value(IDuckDBAppenderRow row, object? value) => value switch
      {
         null => row.AppendNullValue(),
         bool flag => row.AppendValue((bool?)flag),
         short number => row.AppendValue((short?)number),
         int number => row.AppendValue((int?)number),
         long number => row.AppendValue((long?)number),
         float number => row.AppendValue((float?)number),
         double number => row.AppendValue((double?)number),
         decimal number => row.AppendValue((decimal?)number),
         string text => row.AppendValue(text),
         byte[] bytes => row.AppendValue(bytes),
         Guid guid => row.AppendValue((Guid?)guid),
         DateOnly date => row.AppendValue((DateOnly?)date),
         TimeOnly time => row.AppendValue((TimeOnly?)time),
         DateTime dateTime => row.AppendValue((DateTime?)dateTime),
         DateTimeOffset offset => row.AppendValue((DateTimeOffset?)offset),
         TimeSpan interval => row.AppendValue((TimeSpan?)interval),
         _ => throw new InvalidCastException($"A {value.GetType().Name} can't be appended"),
      };

      public ValueTask CompleteAsync(CancellationToken cancellationToken)
      {
         completed = true;
         appender.Close();
         return ValueTask.CompletedTask;
      }

      public async ValueTask DisposeAsync()
      {
         try
         {
            if (!completed)
            {
               // The rows of a load that failed: their table is going too.
               appender.Clear();
               appender.Dispose();
            }
         }
         catch (Exception e) when (e is DbException or InvalidOperationException)
         {
            // Already closed.
         }
         finally
         {
            await connection.DisposeAsync().ConfigureAwait(false);
         }
      }
   }
}
