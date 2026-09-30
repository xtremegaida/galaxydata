using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Execution;

/// <summary>
/// Runs a query that combines sources: fetches each fragment from its source into a table of a merge session, a few
/// at a time, then runs the merge SQL over the tables and streams its rows. The session lasts as long as the result.
/// A fragment waits for the ones it depends on: the values its SQL takes as parameters, and, when it is fetched by
/// keys, the fragment whose table has them. A fragment of a source kept in the merge engine's database (an Excel
/// folder) is copied into its table by a query of the merge engine's own.
/// </summary>
internal sealed class FederatedExecution
{
   private readonly QueryEngine engine;
   private readonly PreparedQuery query;
   private readonly ExecutionStats stats;
   private readonly IMergeEngine merge;
   private readonly QueryEngineOptions options;
   private long fetched;

   /// <summary>
   /// Values of unknown types go into the merge engine as their text; the values themselves, by that text, so a
   /// result column of an unknown type (which the merge SQL only passes on) gives back what the source gave.
   /// </summary>
   private readonly ConcurrentDictionary<string, object> unknowns = new(StringComparer.Ordinal);

   /// <summary>The values of the scalar subqueries that run first, by name.</summary>
   private readonly ConcurrentDictionary<string, object?> values = new(StringComparer.Ordinal);

   public FederatedExecution(QueryEngine engine, PreparedQuery query, ExecutionStats stats)
   {
      this.engine = engine;
      this.query = query;
      this.stats = stats;
      options = engine.Options;
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
         query.Bind(command, query.Merge!, provider.Dialect, provider, stats.Started, values);
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
                                options.LenientConversion, stats)
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

   /// <summary>
   /// Fetches every fragment, each after those it depends on, at most a few at a time; the first to fail stops the
   /// others, and its error is the one reported.
   /// </summary>
   private async Task FetchAllAsync(IMergeSession session, CancellationToken cancellationToken)
   {
      using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      using SemaphoreSlim gate = new(Math.Max(1, options.MaxParallelFetches));
      Dictionary<string, QueryFragment> byValue = query.Fragments.Where(f => f.Value != null).ToDictionary(f => f.Value!, StringComparer.Ordinal);
      Dictionary<QueryFragment, Task> tasks = [];

      Task Start(QueryFragment fragment)
      {
         if (tasks.TryGetValue(fragment, out Task? task)) { return task; }
         List<Task> before = [.. Dependencies(fragment, byValue).Select(Start)];
         task = Task.Run(async () =>
         {
            await Task.WhenAll(before).ConfigureAwait(false);
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
         }, failed.Token);
         tasks[fragment] = task;
         return task;
      }

      foreach (QueryFragment fragment in query.Fragments) { _ = Start(fragment); }
      Task[] all = [.. tasks.Values];
      try
      {
         await Task.WhenAll(all).ConfigureAwait(false);
      }
      catch when (!cancellationToken.IsCancellationRequested)
      {
         // The others were stopped because of the first failure; that failure is the one to report.
         Exception? cause = all.Where(f => f.IsFaulted).Select(f => f.Exception!.InnerException).FirstOrDefault(e => e is not OperationCanceledException);
         if (cause != null) { ExceptionDispatchInfo.Throw(cause); }
         throw;
      }
   }

   /// <summary>What a fragment waits for: the values its SQL takes, and the fragment it's fetched by the keys of.</summary>
   private static IEnumerable<QueryFragment> Dependencies(QueryFragment fragment, Dictionary<string, QueryFragment> byValue)
   {
      foreach (SqlParameterSlot slot in fragment.Statement.Parameters)
      {
         if (slot.Source == ParameterSource.Runtime && byValue.TryGetValue(slot.ParameterName!, out QueryFragment? value)) { yield return value; }
      }
      if (fragment.BindJoin is { } bind) { yield return bind.Driver; }
   }

   private async Task FetchAsync(IMergeSession session, QueryFragment fragment, CancellationToken cancellationToken)
   {
      long started = options.Clock.GetTimestamp();
      await engine.PrepareReadAsync(fragment, cancellationToken).ConfigureAwait(false);
      if (fragment.Value != null)
      {
         await ValueAsync(fragment, cancellationToken).ConfigureAwait(false);
         stats.Add(new FragmentStats(fragment.Source.Alias, fragment.Value, 1, options.Clock.GetElapsedTime(started)) { Strategy = FetchStrategy.Value });
         return;
      }
      await using IMergeTableWriter writer = await session.CreateTableAsync(fragment.Table!, fragment.Columns, cancellationToken).ConfigureAwait(false);
      List<object>? keys = fragment.BindJoin is { } bind && options.BindJoins != BindJoinMode.Never
         ? await KeysAsync(session, fragment, bind, cancellationToken).ConfigureAwait(false)
         : null;
      FragmentStats result;
      if (keys == null)
      {
         long rows = await LoadAsync(fragment, fragment.Statement, writer, 0, cancellationToken).ConfigureAwait(false);
         result = new FragmentStats(fragment.Source.Alias, fragment.Table!, rows, TimeSpan.Zero) { Strategy = FetchStrategy.Full };
      }
      else if (keys.Count == 0)
      {
         // Nothing it's joined to has a key: no row of it can matter.
         result = new FragmentStats(fragment.Source.Alias, fragment.Table!, 0, TimeSpan.Zero) { Strategy = FetchStrategy.Skipped };
      }
      else if (keys.Count > BatchSize(fragment) && fragment.Planned!.BindJoin!.Key.Type.Kind == ScalarKind.String)
      {
         // A source may match text regardless of case or trailing spaces: one row could match keys in two batches
         // and be fetched twice, so text keys go in one statement or not at all.
         long rows = await LoadAsync(fragment, fragment.Statement, writer, 0, cancellationToken).ConfigureAwait(false);
         result = new FragmentStats(fragment.Source.Alias, fragment.Table!, rows, TimeSpan.Zero) { Strategy = FetchStrategy.Full };
      }
      else
      {
         int size = BatchSize(fragment);
         ScalarType type = fragment.Planned!.BindJoin!.Key.Type;
         long rows = 0;
         int batches = 0;
         for (int start = 0; start < keys.Count; start += size)
         {
            List<PlanExpr> batch = keys.Skip(start).Take(size).Select(k => (PlanExpr)new PlanLiteral(k, type)).ToList();
            rows = await LoadAsync(fragment, QueryEngine.BindBatch(fragment.Planned, batch), writer, rows, cancellationToken).ConfigureAwait(false);
            batches++;
         }
         result = new FragmentStats(fragment.Source.Alias, fragment.Table!, rows, TimeSpan.Zero) { Strategy = FetchStrategy.Keys, Keys = keys.Count, Batches = batches };
      }
      try
      {
         await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (DbException e)
      {
         throw Unloaded(fragment, null, e);
      }
      stats.Add(result with { Elapsed = options.Clock.GetElapsedTime(started) });
   }

   /// <summary>The most keys in one statement: whole-number keys are written into the SQL, but others are parameters, which sources limit.</summary>
   private int BatchSize(QueryFragment fragment) =>
      Math.Max(1, Math.Min(options.MaxBindBatch, fragment.Dialect.MaxParameters - fragment.Statement.Parameters.Count - 1));

   /// <summary>
   /// The keys to fetch a fragment by: the distinct values of the driver's column that can be the fragment's key, or
   /// null to fetch it in full (too many keys, or, when adaptive, as many as the rows it's expected to have).
   /// </summary>
   private async Task<List<object>?> KeysAsync(IMergeSession session, QueryFragment fragment, BindJoinInfo bind, CancellationToken cancellationToken)
   {
      SqlDialect dialect = merge.Provider.Dialect;
      string column = dialect.Identifier(bind.DriverColumn);
      string sql = $"SELECT DISTINCT {column} FROM {dialect.Identifier(bind.Driver.Table!)} WHERE {column} IS NOT NULL LIMIT " +
                   ((long)options.MaxBindKeys + 1).ToString(CultureInfo.InvariantCulture);
      ScalarType type = fragment.Planned!.BindJoin!.Key.Type;
      HashSet<object> keys = [];
      await using (DbConnection connection = await session.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
      await using (DbCommand command = connection.CreateCommand())
      {
         command.CommandText = sql;
         try
         {
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
               // A value that isn't one of the key's type can't equal a key.
               if (QueryResult.TryConvert(merge.Provider.ReadValue(reader, 0), type, out object? key, out _) && key != null) { keys.Add(key); }
            }
         }
         catch (DbException e)
         {
            cancellationToken.ThrowIfCancellationRequested();
            throw new QueryExecutionException($"The merge engine ({dialect.Name}) failed to read the keys of {bind.Driver.Table}.{bind.DriverColumn}: {e.Message}", e);
         }
      }
      if (keys.Count > options.MaxBindKeys) { return null; }
      if (options.BindJoins == BindJoinMode.Adaptive && keys.Count > 0 && fragment.Planned.Table is { Estimate: var expected } && keys.Count >= expected.Rows)
      {
         return null;
      }
      return [.. keys];
   }

   /// <summary>Runs a scalar subquery's SQL and keeps its value (null for no rows) for the SQL that uses it.</summary>
   private async Task ValueAsync(QueryFragment fragment, CancellationToken cancellationToken)
   {
      SourceProvider provider = engine.Provider(fragment.Source);
      string where = $"{fragment.Source.Alias} ({fragment.Dialect.Name})";
      await using DbConnection connection = await engine.OpenAsync(fragment.Source, cancellationToken).ConfigureAwait(false);
      await using DbCommand command = Command(connection, provider, fragment, fragment.Statement);
      object? value = null;
      using (cancellationToken.Register(Cancel, command))
      {
         await using DbDataReader reader = await Execute(command, where, cancellationToken).ConfigureAwait(false);
         if (await Read(reader, where, cancellationToken).ConfigureAwait(false))
         {
            ScalarType type = fragment.Columns[0].Type;
            if (!QueryResult.TryConvert(provider.ReadValue(reader, 0), type, out value, out Exception? error) && !options.LenientConversion)
            {
               throw QueryResult.Unconverted($"The value {fragment.Value} from {fragment.Source.Alias}: {fragment.Source.Alias}", type, error);
            }
         }
      }
      values[fragment.Value!] = value;
   }

   /// <summary>Runs one statement of a fragment in its source and loads its rows, as their logical types, into its table; the rows loaded so far.</summary>
   private async Task<long> LoadAsync(QueryFragment fragment, SqlStatement statement, IMergeTableWriter writer, long rows, CancellationToken cancellationToken)
   {
      if (fragment.InMergeEngine) { return rows + await CopyAsync(fragment, statement, writer, cancellationToken).ConfigureAwait(false); }
      SourceProvider provider = engine.Provider(fragment.Source);
      string where = $"{fragment.Source.Alias} ({fragment.Dialect.Name})";
      IReadOnlyList<MergeColumn> columns = fragment.Columns;
      await using DbConnection connection = await engine.OpenAsync(fragment.Source, cancellationToken).ConfigureAwait(false);
      await using DbCommand command = Command(connection, provider, fragment, statement);
      using (cancellationToken.Register(Cancel, command))
      {
         await using DbDataReader reader = await Execute(command, where, cancellationToken).ConfigureAwait(false);
         object?[] row = new object?[columns.Count];
         while (await Read(reader, where, cancellationToken).ConfigureAwait(false))
         {
            for (int i = 0; i < row.Length; i++)
            {
               if (!QueryResult.TryConvert(provider.ReadValue(reader, i), columns[i].Type, out row[i], out Exception? error) && !options.LenientConversion)
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
            if (Interlocked.Increment(ref fetched) > options.MaxFetchedRows) { throw TooManyRows(); }
         }
      }
      return rows;
   }

   /// <summary>
   /// Loads one statement's rows of a fragment of a source kept in the merge engine's database by a query of the
   /// merge engine's own, so they never leave it; how many.
   /// </summary>
   private async Task<long> CopyAsync(QueryFragment fragment, SqlStatement statement, IMergeTableWriter writer, CancellationToken cancellationToken)
   {
      SourceProvider provider = engine.Provider(fragment.Source);
      long copied;
      try
      {
         copied = await writer.AppendQueryAsync(command =>
         {
            query.Bind(command, statement, fragment.Dialect, provider, stats.Started, values);
            if (options.CommandTimeout is { } timeout) { command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds); }
         }, cancellationToken).ConfigureAwait(false);
      }
      catch (DbException e)
      {
         cancellationToken.ThrowIfCancellationRequested();
         throw new QueryExecutionException($"{fragment.Source.Alias} ({fragment.Dialect.Name}) failed to run its part of the query: {e.Message}", e);
      }
      if (Interlocked.Add(ref fetched, copied) > options.MaxFetchedRows) { throw TooManyRows(); }
      return copied;
   }

   private QueryExecutionException TooManyRows() =>
      new($"The query fetches more than {options.MaxFetchedRows!.Value.ToString("N0", CultureInfo.InvariantCulture)} rows from its sources into the merge engine, the most it may (MaxFetchedRows)");

   private DbCommand Command(DbConnection connection, SourceProvider provider, QueryFragment fragment, SqlStatement statement)
   {
      DbCommand command = connection.CreateCommand();
      provider.PrepareCommand(command);
      query.Bind(command, statement, fragment.Dialect, provider, stats.Started, values);
      if (options.CommandTimeout is { } timeout) { command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds); }
      return command;
   }

   private static async Task<DbDataReader> Execute(DbCommand command, string where, CancellationToken cancellationToken)
   {
      try
      {
         return await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (DbException e)
      {
         cancellationToken.ThrowIfCancellationRequested();
         throw new QueryExecutionException($"{where} failed to run its part of the query: {e.Message}", e);
      }
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
