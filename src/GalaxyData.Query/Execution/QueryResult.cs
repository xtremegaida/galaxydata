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

   /// <summary>The keys sent to sources to fetch fragments by.</summary>
   public long KeysSent => Fragments.Sum(f => (long)f.Keys);

   internal TimeSpan SinceStart => clock.GetElapsedTime(startTimestamp);

   internal void Add(FragmentStats fragment)
   {
      lock (fragments) { fragments.Add(fragment); }
   }
}

/// <summary>One fragment fetched from a source into a merge table (or a value): how, how many rows, and how long it took.</summary>
public sealed record FragmentStats(string Source, string Table, long Rows, TimeSpan Elapsed)
{
   public FetchStrategy Strategy { get; init; }

   /// <summary>For a fragment fetched by keys: how many keys, in how many statements.</summary>
   public int Keys { get; init; }

   public int Batches { get; init; }
}

/// <summary>How a fragment was fetched.</summary>
public enum FetchStrategy : byte
{
   /// <summary>All its rows.</summary>
   Full,

   /// <summary>The rows with the keys of the fragment it's joined to, in batches.</summary>
   Keys,

   /// <summary>Not at all: the fragment it's joined to had no keys, so none of its rows could matter.</summary>
   Skipped,

   /// <summary>A scalar subquery's value, worked out before the fragments that use it.</summary>
   Value,
}

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
   private Deadline? deadline;
   private CancellationTokenRegistration expiring;

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

   /// <summary>The query's deadline, which stops the command when the time is up; the result disposes it.</summary>
   internal void Expires(Deadline? time)
   {
      if (time == null) { return; }
      deadline = time;
      expiring = provider.StopOnCancel(command, time.Token);
   }

   public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
   {
      if (finished) { return false; }
      if (deadline?.Expired == true) { throw deadline.Exception(); }
      Watch(cancellationToken);
      bool read;
      try
      {
         read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (Exception e) when (deadline?.Expired == true && !cancellationToken.IsCancellationRequested && e is DbException or OperationCanceledException or InvalidOperationException)
      {
         // The command was stopped when the time was up.
         throw deadline.Exception();
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
         if (!TryRead(provider, reader, i, columns[i].Type, out row[i], out Exception? error) && !lenient)
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
      watching = provider.StopOnCancel(command, cancellationToken);
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
      catch (Exception e) when (IsConversion(e))
      {
         value = null;
         error = e;
         return false;
      }
   }

   /// <summary>
   /// A column's value, read and converted; false, with null and the reason, when the provider can't read it (a
   /// PostgreSQL NaN has no decimal) or it doesn't convert.
   /// </summary>
   internal static bool TryRead(SourceProvider provider, DbDataReader reader, int ordinal, ScalarType type, out object? value, out Exception? error)
   {
      object? raw;
      try
      {
         raw = provider.ReadValue(reader, ordinal);
      }
      catch (Exception e) when (IsConversion(e))
      {
         value = null;
         error = e;
         return false;
      }
      return TryConvert(raw, type, out value, out error);
   }

   private static bool IsConversion(Exception e) => e is FormatException or InvalidCastException or OverflowException or ArgumentException;

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
      await expiring.DisposeAsync().ConfigureAwait(false);
      deadline?.Dispose();
      await reader.DisposeAsync().ConfigureAwait(false);
      await command.DisposeAsync().ConfigureAwait(false);
      await owner.DisposeAsync().ConfigureAwait(false);
   }
}
