using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Explain;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Planning.Federation;
using GalaxyData.Query.Results;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Execution;

/// <summary>A bound and planned query with its SQL written; nothing has run yet.</summary>
public sealed class PreparedQuery
{
   private readonly QueryEngine engine;
   private readonly QueryParameters parameters;
   private readonly PageRequest? paging;
   private readonly TimeSpan? timeout;

   internal PreparedQuery(QueryEngine engine, QueryRequest request, QueryParameters parameters, BoundProgram? program,
                          IReadOnlyList<QueryDiagnostic> diagnostics, LogicalPlan? plan, IReadOnlyList<QueryFragment> fragments)
   {
      this.engine = engine;
      this.parameters = parameters;
      paging = request.Paging;
      timeout = request.Timeout;
      Program = program;
      Text = request.Text;
      Diagnostics = diagnostics;
      Plan = plan;
      Fragments = fragments;
   }

   public string Text { get; }

   internal QueryParameters Parameters => parameters;

   /// <summary>The request's own timeout, if it has one.</summary>
   internal TimeSpan? Timeout => timeout;

   internal BoundProgram? Program { get; }

   /// <summary>Made by <see cref="ForCount"/>: the plan counts the program's rows.</summary>
   internal bool IsCount { get; set; }

   public IReadOnlyList<QueryDiagnostic> Diagnostics { get; }

   public bool Success => Plan != null && (Fragments.Count > 0 || Merge != null) && !Diagnostics.Any(d => d.IsError);

   public LogicalPlan? Plan { get; }

   public ResultSchema? Schema => Plan?.Schema;

   /// <summary>The SQL each source runs: the whole query, or each source's part when the query combines sources.</summary>
   public IReadOnlyList<QueryFragment> Fragments { get; }

   /// <summary>The merge engine's SQL over the fragments' tables; null when one source runs the whole query.</summary>
   public SqlStatement? Merge { get; internal init; }

   /// <summary>The merge engine's dialect, which <see cref="Merge"/> is written in.</summary>
   public SqlDialect? MergeDialect { get; internal init; }

   /// <summary>How the plan is split between the sources and the merge engine; null when one source runs it.</summary>
   internal FederatedPlan? Federation { get; init; }

   /// <summary>
   /// The same query counting its rows instead (one row, one column <c>count</c>): without its final sort and without
   /// the request's paging, and with the joins that can't change the count left out. A <c>take(...)</c> in the
   /// query itself still counts.
   /// </summary>
   public PreparedQuery ForCount() => engine.PrepareCount(this);

   /// <summary>What the query would do, without running it; <paramref name="verbose"/> adds the plan after each optimizer phase.</summary>
   public QueryExplain Explain(bool verbose = false) => QueryExplainer.Explain(this, Program, paging, IsCount, engine.Options.Optimize, verbose);

   /// <summary>
   /// Runs the query: its result streams the rows. With a timeout (the request's, or the engine's), the query is
   /// stopped when it has run that long, reading included, and fails with <see cref="QueryTimeoutException"/>.
   /// </summary>
   public async Task<QueryResult> ExecuteAsync(CancellationToken cancellationToken = default)
   {
      if (!Success) { throw new QueryException(Diagnostics); }
      QueryEngineOptions options = engine.Options;
      Deadline? deadline = Deadline.Start(timeout ?? options.Timeout, options.Clock, cancellationToken);
      try
      {
         QueryResult result = await RunAsync(deadline?.Token ?? cancellationToken).ConfigureAwait(false);
         result.Expires(deadline);
         return result;
      }
      catch (Exception e) when (deadline?.Expired == true && e is OperationCanceledException or QueryExecutionException or DbException)
      {
         deadline.Dispose();
         throw deadline.Exception();
      }
      catch
      {
         deadline?.Dispose();
         throw;
      }
   }

   private async Task<QueryResult> RunAsync(CancellationToken cancellationToken)
   {
      QueryEngineOptions options = engine.Options;
      ExecutionStats stats = new(options.Clock.GetUtcNow(), options.Clock);
      if (Merge != null) { return await new FederatedExecution(engine, this, stats).RunAsync(cancellationToken).ConfigureAwait(false); }

      QueryFragment fragment = Fragments[0];
      SourceProvider provider = engine.Provider(fragment.Source);
      // What the statement reads stays as it is until the statement has started.
      using IDisposable? lease = await engine.PrepareReadAsync(fragment, cancellationToken).ConfigureAwait(false);
      DbConnection connection = await engine.OpenAsync(fragment.Source, cancellationToken).ConfigureAwait(false);
      DbCommand? command = null;
      try
      {
         command = connection.CreateCommand();
         provider.PrepareCommand(command);
         Bind(command, fragment.Statement, fragment.Dialect, provider, stats.Started, null);
         if (options.CommandTimeout is { } timeout) { command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds); }
         DbDataReader reader;
         try
         {
            // Most providers check the token only between rows; a statement busy computing the first is stopped.
            using (provider.StopOnCancel(command, cancellationToken))
            {
               reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            }
         }
         catch (DbException e)
         {
            cancellationToken.ThrowIfCancellationRequested();
            throw new QueryExecutionException($"{fragment.Source.Alias} ({fragment.Dialect.Name}) failed to run the query: {e.Message}", e);
         }
         return new QueryResult(Schema!, new RowSource(fragment.Source.Alias, fragment.Dialect.Name), provider, command, reader, connection, options.LenientConversion, stats)
         {
            RequiresRow = Plan!.RequiresRow,
         };
      }
      catch
      {
         if (command != null) { await command.DisposeAsync().ConfigureAwait(false); }
         await connection.DisposeAsync().ConfigureAwait(false);
         throw;
      }
   }

   /// <summary>
   /// Sets a command's text and parameters, with the values they have when the query runs; <paramref name="values"/>
   /// has the runtime values worked out so far.
   /// </summary>
   internal void Bind(DbCommand command, SqlStatement statement, SqlDialect dialect, SourceProvider provider, DateTimeOffset started,
                      IReadOnlyDictionary<string, object?>? values)
   {
      command.CommandText = statement.Text;
      foreach (SqlParameterSlot slot in statement.Parameters)
      {
         DbParameter parameter = command.CreateParameter();
         parameter.ParameterName = dialect.ParameterName(slot.Name);
         provider.BindParameter(parameter, Resolve(slot, started, values), slot);
         command.Parameters.Add(parameter);
      }
   }

   /// <summary>The value of a parameter when the query runs, converted to the type the SQL expects.</summary>
   private object? Resolve(SqlParameterSlot slot, DateTimeOffset started, IReadOnlyDictionary<string, object?>? values)
   {
      object? value = slot.Source switch
      {
         null => slot.Constant,
         ParameterSource.User => User(slot),
         ParameterSource.Now => started.UtcDateTime,
         ParameterSource.Today => DateOnly.FromDateTime(started.UtcDateTime),
         ParameterSource.Runtime => values != null && values.TryGetValue(slot.ParameterName!, out object? runtime)
            ? runtime
            : throw new InvalidOperationException($"The value {slot.ParameterName} isn't known yet"),
         _ => throw new InvalidOperationException($"Unexpected parameter source {slot.Source}"),
      };
      return slot.Pattern != null && value is string text ? slot.Pattern.Apply(text) : value;
   }

   private object? User(SqlParameterSlot slot)
   {
      if (!parameters.TryGet(slot.ParameterName!, out QueryParameter? parameter))
      {
         throw new QueryExecutionException($"No value was given for ${slot.ParameterName}");
      }
      if (parameter.Value == null || parameter.Type.Kind == slot.Type.Kind) { return parameter.Value; }
      return TypeRules.TryConvertConstant(parameter.Value, parameter.Type, slot.Type, out object? converted, out _, out string? error)
         ? converted
         : throw new QueryExecutionException($"${slot.ParameterName} can't be used as {TypeRules.Describe(slot.Type)}: {error}");
   }
}
