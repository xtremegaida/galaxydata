using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Results;

namespace GalaxyData.Query.Execution;

/// <summary>What running a query took.</summary>
public sealed class ExecutionStats
{
   internal ExecutionStats(DateTimeOffset started) { Started = started; }

   public DateTimeOffset Started { get; }

   /// <summary>Rows read so far.</summary>
   public long Rows { get; internal set; }

   /// <summary>Time from the start of execution until the last row was read (or until now, while reading).</summary>
   public TimeSpan Elapsed { get; internal set; }
}

/// <summary>
/// The rows of a running query, streamed from the database. Each row is an array of values in schema order, of
/// the CLR types <see cref="ValueConverter"/> documents. Dispose it to release the connection.
/// </summary>
public sealed class QueryResult : IAsyncDisposable, IAsyncEnumerable<object?[]>
{
   private readonly QueryFragment fragment;
   private readonly SourceProvider provider;
   private readonly DbConnection connection;
   private readonly DbCommand command;
   private readonly DbDataReader reader;
   private readonly bool lenient;
   private readonly TimeProvider clock;
   private readonly long startTimestamp;
   private bool finished;

   internal QueryResult(ResultSchema schema, QueryFragment fragment, SourceProvider provider, DbConnection connection, DbCommand command,
                        DbDataReader reader, bool lenient, DateTimeOffset started, TimeProvider clock)
   {
      Schema = schema;
      this.fragment = fragment;
      this.provider = provider;
      this.connection = connection;
      this.command = command;
      this.reader = reader;
      this.lenient = lenient;
      this.clock = clock;
      startTimestamp = clock.GetTimestamp();
      Stats = new ExecutionStats(started);
   }

   public ResultSchema Schema { get; }

   public ExecutionStats Stats { get; }

   /// <summary>The query is <c>first()</c>: reading to the end without finding a row fails.</summary>
   internal bool RequiresRow { get; init; }

   /// <summary>The row the last successful <see cref="ReadAsync"/> read.</summary>
   public object?[] Current { get; private set; } = [];

   public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
   {
      if (finished) { return false; }
      bool read;
      try
      {
         read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (DbException e)
      {
         throw new QueryExecutionException($"{fragment.Source.Alias} ({fragment.Dialect.Name}) failed while reading rows: {e.Message}", e);
      }
      Stats.Elapsed = clock.GetElapsedTime(startTimestamp);
      if (!read)
      {
         finished = true;
         if (RequiresRow && Stats.Rows == 0)
         {
            throw new QueryExecutionException("first() found no rows; use firstOrDefault() when there may be none");
         }
         return false;
      }
      IReadOnlyList<ResultColumn> columns = Schema.Columns;
      object?[] row = new object?[columns.Count];
      for (int i = 0; i < row.Length; i++) { row[i] = Read(i, columns[i]); }
      Current = row;
      Stats.Rows++;
      return true;
   }

   private object? Read(int ordinal, ResultColumn column)
   {
      object? raw = provider.ReadValue(reader, ordinal);
      try
      {
         return ValueConverter.ToLogical(raw, column.Type);
      }
      catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
      {
         if (lenient) { return null; }
         throw new QueryExecutionException(
            $"Row {Stats.Rows + 1}, column '{column.Name}': {fragment.Source.Alias} returned a value that isn't {Types.TypeRules.Describe(column.Type)} ({e.Message})", e);
      }
   }

   /// <summary>Reads the remaining rows.</summary>
   public async Task<IReadOnlyList<object?[]>> ToListAsync(CancellationToken cancellationToken = default)
   {
      List<object?[]> rows = [];
      while (await ReadAsync(cancellationToken).ConfigureAwait(false)) { rows.Add(Current); }
      return rows;
   }

   public async IAsyncEnumerator<object?[]> GetAsyncEnumerator(CancellationToken cancellationToken = default)
   {
      while (await ReadAsync(cancellationToken).ConfigureAwait(false)) { yield return Current; }
   }

   public async ValueTask DisposeAsync()
   {
      finished = true;
      await reader.DisposeAsync().ConfigureAwait(false);
      await command.DisposeAsync().ConfigureAwait(false);
      await connection.DisposeAsync().ConfigureAwait(false);
   }
}
