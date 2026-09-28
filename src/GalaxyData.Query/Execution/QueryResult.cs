using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Execution;

/// <summary>What running a query took.</summary>
public sealed class ExecutionStats
{
   private readonly TimeProvider clock;
   private readonly long startTimestamp;
   private readonly List<FragmentStats> fragments = [];

   internal ExecutionStats(DateTimeOffset started, TimeProvider clock)
   {
      Started = started;
      this.clock = clock;
      startTimestamp = clock.GetTimestamp();
   }

   public DateTimeOffset Started { get; }

   /// <summary>Rows read so far.</summary>
   public long Rows { get; internal set; }

   /// <summary>Time from the start of execution until the last row was read (or until now, while reading).</summary>
   public TimeSpan Elapsed { get; internal set; }

   /// <summary>For a query that combines sources: the fragments fetched into the merge engine, in the order they finished.</summary>
   public IReadOnlyList<FragmentStats> Fragments
   {
      get
      {
         lock (fragments) { return [.. fragments]; }
      }
   }

   /// <summary>The rows fetched from the sources into the merge engine.</summary>
   public long FetchedRows => Fragments.Sum(f => f.Rows);

   internal TimeSpan SinceStart => clock.GetElapsedTime(startTimestamp);

   internal void Add(FragmentStats fragment)
   {
      lock (fragments) { fragments.Add(fragment); }
   }
}

/// <summary>One fragment fetched from a source into a merge table: how many rows, and how long it took.</summary>
public sealed record FragmentStats(string Source, string Table, long Rows, TimeSpan Elapsed);

/// <summary>Where a result's rows come from, for messages: a source (<c>shop</c>, <c>SQLite</c>) or the merge engine.</summary>
internal sealed record RowSource(string Name, string Dialect);

/// <summary>
/// The rows of a running query, streamed from the database. Each row is an array of values in schema order, of
/// the CLR types <see cref="ValueConverter"/> documents. Dispose it to release the connection.
/// </summary>
public sealed class QueryResult : IAsyncDisposable, IAsyncEnumerable<object?[]>
{
   private readonly RowSource source;
   private readonly SourceProvider provider;
   private readonly DbCommand command;
   private readonly DbDataReader reader;
   private readonly IAsyncDisposable owner;
   private readonly bool lenient;
   private bool finished;
   private CancellationToken watched;
   private CancellationTokenRegistration watching;

   /// <summary>A result read from <paramref name="reader"/>; disposing it disposes the reader, the command, then <paramref name="owner"/>.</summary>
   internal QueryResult(ResultSchema schema, RowSource source, SourceProvider provider, DbCommand command, DbDataReader reader,
                        IAsyncDisposable owner, bool lenient, ExecutionStats stats)
   {
      Schema = schema;
      this.source = source;
      this.provider = provider;
      this.command = command;
      this.reader = reader;
      this.owner = owner;
      this.lenient = lenient;
      Stats = stats;
   }

   public ResultSchema Schema { get; }

   public ExecutionStats Stats { get; }

   /// <summary>The query is <c>first()</c>: reading to the end without finding a row fails.</summary>
   internal bool RequiresRow { get; init; }

   /// <summary>For a merge engine's result: values of unknown types, by the text the merge engine held them as.</summary>
   internal IReadOnlyDictionary<string, object>? Unknowns { get; init; }

   /// <summary>The row the last successful <see cref="ReadAsync"/> read.</summary>
   public object?[] Current { get; private set; } = [];

   public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
   {
      if (finished) { return false; }
      Watch(cancellationToken);
      bool read;
      try
      {
         read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (DbException e)
      {
         cancellationToken.ThrowIfCancellationRequested();
         throw new QueryExecutionException($"{source.Name} ({source.Dialect}) failed while reading rows: {e.Message}", e);
      }
      Stats.Elapsed = Stats.SinceStart;
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
      for (int i = 0; i < row.Length; i++)
      {
         if (Unknowns != null && columns[i].Type.Kind == ScalarKind.Unknown)
         {
            object? raw = provider.ReadValue(reader, i);
            row[i] = raw is string text && Unknowns.TryGetValue(text, out object? original) ? original : raw;
            continue;
         }
         if (!TryConvert(provider.ReadValue(reader, i), columns[i].Type, out row[i], out Exception? error) && !lenient)
         {
            throw Unconverted($"Row {Stats.Rows + 1}, column '{columns[i].Name}': {source.Name}", columns[i].Type, error);
         }
      }
      Current = row;
      Stats.Rows++;
      return true;
   }

   /// <summary>
   /// Stops the command when the token given to a read is cancelled, so a database busy computing the next rows
   /// stops too; most readers only check the token between rows. Callers usually pass one token throughout.
   /// </summary>
   private void Watch(CancellationToken cancellationToken)
   {
      if (!cancellationToken.CanBeCanceled || cancellationToken == watched) { return; }
      watching.Dispose();
      watched = cancellationToken;
      watching = cancellationToken.Register(Cancel, command);
   }

   internal static void Cancel(object? command)
   {
      try
      {
         ((DbCommand)command!).Cancel();
      }
      catch (Exception e) when (e is DbException or InvalidOperationException or ObjectDisposedException)
      {
         // Nothing running to stop.
      }
   }

   /// <summary>A raw value as its logical type's CLR value; false, with null and the reason, when it doesn't convert.</summary>
   internal static bool TryConvert(object? raw, ScalarType type, out object? value, out Exception? error)
   {
      try
      {
         value = ValueConverter.ToLogical(raw, type);
         error = null;
         return true;
      }
      catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
      {
         value = null;
         error = e;
         return false;
      }
   }

   /// <summary>The error for a value that didn't convert; <paramref name="where"/> says whose (<c>Row 2, column 'happened': shop</c>).</summary>
   internal static QueryExecutionException Unconverted(string where, ScalarType type, Exception? error) =>
      new($"{where} returned a value that isn't {TypeRules.Describe(type)} ({error?.Message})", error);

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
      await watching.DisposeAsync().ConfigureAwait(false);
      await reader.DisposeAsync().ConfigureAwait(false);
      await command.DisposeAsync().ConfigureAwait(false);
      await owner.DisposeAsync().ConfigureAwait(false);
   }
}
