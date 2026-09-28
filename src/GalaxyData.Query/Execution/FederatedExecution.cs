using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Execution;

/// <summary>
/// Runs a query that combines sources: fetches each fragment from its source into a table of a merge session, a few
/// at a time, then runs the merge SQL over the tables and streams its rows. The session lasts as long as the result.
/// </summary>
internal sealed class FederatedExecution
{
   private readonly QueryEngine engine;
   private readonly PreparedQuery query;
   private readonly ExecutionStats stats;
   private readonly IMergeEngine merge;
   private long fetched;

   /// <summary>
   /// Values of unknown types go into the merge engine as their text; the values themselves, by that text, so a
   /// result column of an unknown type (which the merge SQL only passes on) gives back what the source gave.
   /// </summary>
   private readonly ConcurrentDictionary<string, object> unknowns = new(StringComparer.Ordinal);

   public FederatedExecution(QueryEngine engine, PreparedQuery query, ExecutionStats stats)
   {
      this.engine = engine;
      this.query = query;
      this.stats = stats;
      merge = engine.Merge ?? throw new InvalidOperationException("A query that combines sources needs a merge engine");
   }

   public async Task<QueryResult> RunAsync(CancellationToken cancellationToken)
   {
      IMergeSession session = await merge.OpenSessionAsync(cancellationToken).ConfigureAwait(false);
      DbCommand? command = null;
      try
      {
         await FetchAllAsync(session, cancellationToken).ConfigureAwait(false);
         SourceProvider provider = merge.Provider;
         command = session.CreateCommand();
         query.Bind(command, query.Merge!, provider.Dialect, provider, stats.Started);
         DbDataReader reader;
         try
         {
            using (cancellationToken.Register(Cancel, command))
            {
               reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            }
         }
         catch (DbException e)
         {
            cancellationToken.ThrowIfCancellationRequested();
            throw new QueryExecutionException($"The merge engine ({provider.Dialect.Name}) failed to run the query: {e.Message}", e);
         }
         return new QueryResult(query.Schema!, new RowSource("the merge engine", provider.Dialect.Name), provider, command, reader, session,
                                engine.Options.LenientConversion, stats)
         {
            RequiresRow = query.Plan!.RequiresRow,
            Unknowns = unknowns.IsEmpty ? null : unknowns,
         };
      }
      catch
      {
         if (command != null) { await command.DisposeAsync().ConfigureAwait(false); }
         await session.DisposeAsync().ConfigureAwait(false);
         throw;
      }
   }

   /// <summary>Fetches every fragment; the first to fail stops the others, and its error is the one reported.</summary>
   private async Task FetchAllAsync(IMergeSession session, CancellationToken cancellationToken)
   {
      using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      using SemaphoreSlim gate = new(Math.Max(1, engine.Options.MaxParallelFetches));
      Task[] fetches = query.Fragments.Select(fragment => Task.Run(async () =>
      {
         await gate.WaitAsync(failed.Token).ConfigureAwait(false);
         try
         {
            await FetchAsync(session, fragment, failed.Token).ConfigureAwait(false);
         }
         catch
         {
            await failed.CancelAsync().ConfigureAwait(false);
            throw;
         }
         finally
         {
            gate.Release();
         }
      }, failed.Token)).ToArray();
      try
      {
         await Task.WhenAll(fetches).ConfigureAwait(false);
      }
      catch when (!cancellationToken.IsCancellationRequested)
      {
         // The others were stopped because of the first failure; that failure is the one to report.
         Exception? cause = fetches.Where(f => f.IsFaulted).Select(f => f.Exception!.InnerException).FirstOrDefault(e => e is not OperationCanceledException);
         if (cause != null) { ExceptionDispatchInfo.Throw(cause); }
         throw;
      }
   }

   /// <summary>Runs a fragment's SQL in its source and loads the rows, converted to their logical types, into its table.</summary>
   private async Task FetchAsync(IMergeSession session, QueryFragment fragment, CancellationToken cancellationToken)
   {
      long started = engine.Options.Clock.GetTimestamp();
      SourceProvider provider = engine.Provider(fragment.Source);
      string where = $"{fragment.Source.Alias} ({fragment.Dialect.Name})";
      IReadOnlyList<MergeColumn> columns = fragment.Columns;
      await using DbConnection connection = await engine.Connections.OpenAsync(fragment.Source, cancellationToken).ConfigureAwait(false);
      await provider.PrepareConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
      await using DbCommand command = connection.CreateCommand();
      provider.PrepareCommand(command);
      query.Bind(command, fragment.Statement, fragment.Dialect, provider, stats.Started);
      if (engine.Options.CommandTimeout is { } timeout) { command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds); }
      await using IMergeTableWriter writer = await session.CreateTableAsync(fragment.Table!, columns, cancellationToken).ConfigureAwait(false);
      long rows = 0;
      using (cancellationToken.Register(Cancel, command))
      {
         DbDataReader reader;
         try
         {
            reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
         }
         catch (DbException e)
         {
            cancellationToken.ThrowIfCancellationRequested();
            throw new QueryExecutionException($"{where} failed to run its part of the query: {e.Message}", e);
         }
         await using (reader.ConfigureAwait(false))
         {
            object?[] row = new object?[columns.Count];
            while (await Read(reader, where, cancellationToken).ConfigureAwait(false))
            {
               for (int i = 0; i < row.Length; i++)
               {
                  if (!QueryResult.TryConvert(provider.ReadValue(reader, i), columns[i].Type, out row[i], out Exception? error) && !engine.Options.LenientConversion)
                  {
                     throw QueryResult.Unconverted($"Row {rows + 1} from {fragment.Source.Alias}, column '{columns[i].Name}': {fragment.Source.Alias}", columns[i].Type, error);
                  }
                  if (columns[i].Type.Kind == ScalarKind.Unknown && row[i] is { } value and not string)
                  {
                     string text = ValueConverter.UnknownText(value);
                     unknowns.TryAdd(text, value);
                     row[i] = text;
                  }
               }
               try
               {
                  writer.Append(row);
               }
               catch (Exception e) when (e is DbException or InvalidCastException or InvalidOperationException or OverflowException or ArgumentException)
               {
                  throw Unloaded(fragment, rows + 1, e);
               }
               rows++;
               if (Interlocked.Increment(ref fetched) > engine.Options.MaxFetchedRows)
               {
                  throw new QueryExecutionException(
                     $"The query fetches more than {engine.Options.MaxFetchedRows.Value.ToString("N0", CultureInfo.InvariantCulture)} rows from its sources into the merge engine, the most it may (MaxFetchedRows)");
               }
            }
         }
      }
      try
      {
         await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (DbException e)
      {
         throw Unloaded(fragment, null, e);
      }
      stats.Add(new FragmentStats(fragment.Source.Alias, fragment.Table!, rows, engine.Options.Clock.GetElapsedTime(started)));
   }

   private QueryExecutionException Unloaded(QueryFragment fragment, long? row, Exception e) =>
      new($"The merge engine ({merge.Provider.Dialect.Name}) failed to load {(row == null ? "the rows" : "row " + row.Value.ToString(CultureInfo.InvariantCulture))} from {fragment.Source.Alias}: {e.Message}", e);

   private static async ValueTask<bool> Read(DbDataReader reader, string where, CancellationToken cancellationToken)
   {
      try
      {
         return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (DbException e)
      {
         cancellationToken.ThrowIfCancellationRequested();
         throw new QueryExecutionException($"{where} failed while reading rows of its part of the query: {e.Message}", e);
      }
   }

   /// <summary>Stops a running command when the query is cancelled: most providers only check the token between rows.</summary>
   private static void Cancel(object? command) => QueryResult.Cancel(command);
}
