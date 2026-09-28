using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Planning.Federation;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Execution;

public sealed record QueryRequest(string Text)
{
   public QueryParameters? Parameters { get; init; }

   /// <summary>Pages the result: skip <see cref="PageRequest.Offset"/> rows, then take at most <see cref="PageRequest.Limit"/>.</summary>
   public PageRequest? Paging { get; init; }
}

public sealed record PageRequest(long Offset, long Limit);

public sealed class QueryEngineOptions
{
   public static QueryEngineOptions Default { get; } = new();

   /// <summary>The clock for <c>now()</c> and <c>today()</c>.</summary>
   public TimeProvider Clock { get; init; } = TimeProvider.System;

   /// <summary>Read values that don't convert to their column's type as null instead of failing the query.</summary>
   public bool LenientConversion { get; init; }

   /// <summary>How long a database command may run; null leaves the provider's default.</summary>
   public TimeSpan? CommandTimeout { get; init; }

   /// <summary>Rewrite plans before writing their SQL (decorrelation, pushdown, pruning); off only to compare.</summary>
   public bool Optimize { get; init; } = true;

   /// <summary>
   /// Run operators in the sources wherever their SQL can express them. Off, sources only read tables and every
   /// other operator runs in the merge engine: slower, and only to compare; it needs a merge engine.
   /// </summary>
   public bool PushDown { get; init; } = true;

   /// <summary>How many of a query's fragments may be fetched at the same time.</summary>
   public int MaxParallelFetches { get; init; } = 4;

   /// <summary>The most rows one query may fetch from its sources into the merge engine; null for no limit.</summary>
   public long? MaxFetchedRows { get; init; }

   /// <summary>A fragment expected to fetch more rows than this gets a warning.</summary>
   public long LargeFetchRows { get; init; } = 1_000_000;
}

/// <summary>
/// Runs queries against a catalog: <see cref="Prepare(QueryRequest)"/> binds and plans them and writes their SQL
/// without touching a database. A query of one source runs in it; a query that combines sources runs in parts, one
/// for each source, whose rows the merge engine combines.
/// </summary>
public sealed class QueryEngine
{
   private readonly Dictionary<string, SourceProvider> providers = new(StringComparer.OrdinalIgnoreCase);

   public QueryEngine(ICatalog catalog, IConnectionFactory connections, IEnumerable<SourceProvider> providers, IMergeEngine? merge = null,
                      QueryEngineOptions? options = null)
   {
      ArgumentNullException.ThrowIfNull(catalog);
      ArgumentNullException.ThrowIfNull(connections);
      ArgumentNullException.ThrowIfNull(providers);
      Catalog = catalog;
      Connections = connections;
      Merge = merge;
      Options = options ?? QueryEngineOptions.Default;
      foreach (SourceProvider provider in providers) { this.providers[provider.ProviderKind] = provider; }
   }

   public ICatalog Catalog { get; }

   public QueryEngineOptions Options { get; }

   /// <summary>Where queries that combine sources are finished; without one they can't run.</summary>
   public IMergeEngine? Merge { get; }

   internal IConnectionFactory Connections { get; }

   public PreparedQuery Prepare(string text, QueryParameters? parameters = null) => Prepare(new QueryRequest(text) { Parameters = parameters });

   public PreparedQuery Prepare(QueryRequest request)
   {
      ArgumentNullException.ThrowIfNull(request);
      QueryParameters parameters = request.Parameters ?? QueryParameters.Empty;
      BoundProgram program = Binder.Bind(request.Text, Catalog, parameters);
      if (!program.Success) { return new PreparedQuery(this, request, parameters, program, [.. program.Diagnostics], null, []); }
      LogicalPlan plan;
      try
      {
         plan = Lowerer.Lower(program, request.Paging?.Offset, request.Paging?.Limit);
      }
      catch (NotSupportedException e)
      {
         return Unplanned(request, parameters, program, e);
      }
      return Plan(request, parameters, program, plan);
   }

   /// <summary>A query that binds but can't be planned yet: the reason as a diagnostic.</summary>
   private PreparedQuery Unplanned(QueryRequest request, QueryParameters parameters, BoundProgram program, NotSupportedException e) =>
      new(this, request, parameters, program,
          [.. program.Diagnostics, QueryDiagnostic.Error(DiagnosticCodes.NotTranslatable, $"This query can't be planned yet: {e.Message}", 0, request.Text.Length)],
          null, []);

   /// <summary>The count of a prepared query's rows, from the same binding: see <see cref="PreparedQuery.ForCount"/>.</summary>
   internal PreparedQuery PrepareCount(PreparedQuery query)
   {
      QueryRequest request = new(query.Text) { Parameters = query.Parameters };
      if (query.Program is not { Success: true } program) { return new PreparedQuery(this, request, query.Parameters, query.Program, query.Diagnostics, null, []) { IsCount = true }; }
      LogicalPlan plan;
      try
      {
         plan = Lowerer.LowerCount(program);
      }
      catch (NotSupportedException e)
      {
         return Unplanned(request, query.Parameters, program, e);
      }
      PreparedQuery count = Plan(request, query.Parameters, program, plan);
      count.IsCount = true;
      return count;
   }

   private PreparedQuery Plan(QueryRequest request, QueryParameters parameters, BoundProgram program, LogicalPlan plan)
   {
      List<QueryDiagnostic> diagnostics = [.. program.Diagnostics];
      if (Options.Optimize) { plan = Planning.Optimizer.PlanOptimizer.Optimize(plan); }
      string text = request.Text;
      PreparedQuery Failed(QueryDiagnostic diagnostic)
      {
         diagnostics.Add(diagnostic);
         return new PreparedQuery(this, request, parameters, program, diagnostics, plan, []);
      }

      List<SourceInfo> sources = PlanAnalysis.Sources(plan.Root);
      if (sources.FirstOrDefault(s => !providers.ContainsKey(s.ProviderKind)) is { } unserved)
      {
         return Failed(QueryDiagnostic.Error(DiagnosticCodes.NoProvider,
            $"'{unserved.Alias}' is a {unserved.ProviderKind} source, and no provider for those is registered", 0, text.Length));
      }
      QueryDiagnostic? single = null;
      if (Options.PushDown && sources.Count <= 1)
      {
         // A query of no table runs in any source.
         SourceInfo? source = sources.Count == 1 ? sources[0] : Catalog.Sources.FirstOrDefault(s => providers.ContainsKey(s.ProviderKind));
         if (source != null)
         {
            single = Single(text, plan, source, out QueryFragment? fragment);
            if (fragment != null) { return new PreparedQuery(this, request, parameters, program, diagnostics, plan, [fragment]); }
         }
         // What the source's SQL can't express, the merge engine can run.
         if (Merge == null)
         {
            return Failed(single ?? QueryDiagnostic.Error(DiagnosticCodes.NoSource, "There is no source to run this query in", 0, text.Length));
         }
      }
      else if (Merge == null)
      {
         return Failed(QueryDiagnostic.Error(DiagnosticCodes.CrossSourceQuery, sources.Count > 1
            ? $"This query reads from {List(sources.Select(s => s.Alias))}; queries that combine sources need a merge engine, and none is configured"
            : "Without push-down every query runs in the merge engine, and none is configured", 0, text.Length));
      }
      return Federate(request, parameters, program, plan, diagnostics, single);
   }

   /// <summary>The SQL for a plan that one source runs whole; otherwise why it can't.</summary>
   private QueryDiagnostic? Single(string text, LogicalPlan plan, SourceInfo source, out QueryFragment? fragment)
   {
      fragment = null;
      SqlDialect dialect = providers[source.ProviderKind].Dialect;
      try
      {
         SqlStatement statement = SqlBuilder.Build(plan, dialect, new SqlBuildOptions { DefaultSchema = source.DefaultSchema });
         fragment = new QueryFragment(source, dialect, statement);
         return null;
      }
      catch (SqlTranslationException e)
      {
         return Untranslatable(text, e, $"{source.Alias}, a {e.Dialect.Name} source");
      }
      catch (NotSupportedException e)
      {
         return QueryDiagnostic.Error(DiagnosticCodes.NotTranslatable, $"This query can't be written as SQL for {source.Alias} yet: {e.Message}", 0, text.Length);
      }
   }

   private static QueryDiagnostic Untranslatable(string text, SqlTranslationException e, string where)
   {
      SourceSpan span = e.Function.Span ?? new SourceSpan(0, text.Length);
      return QueryDiagnostic.Error(DiagnosticCodes.NotTranslatable, $"{e.Function.Function.Name}(...) can't run in {where}", span.Start, span.End);
   }

   /// <summary>
   /// Splits the plan into fragments for its sources and the merge SQL over their rows. <paramref name="single"/> is
   /// why the query's one source couldn't run it whole, which says more than the merge engine's reason, if it has one.
   /// </summary>
   private PreparedQuery Federate(QueryRequest request, QueryParameters parameters, BoundProgram program, LogicalPlan plan,
                                  List<QueryDiagnostic> diagnostics, QueryDiagnostic? single)
   {
      string text = request.Text;
      SqlDialect mergeDialect = Merge!.Provider.Dialect;
      FederatedPlan federated;
      try
      {
         federated = FederationPlanner.Plan(plan, s => providers[s.ProviderKind].Dialect, mergeDialect, Options.PushDown);
      }
      catch (Exception e) when (e is SqlTranslationException or NotSupportedException)
      {
         diagnostics.Add(single ?? (e is SqlTranslationException untranslatable
            ? Untranslatable(text, untranslatable, $"the merge engine ({mergeDialect.Name})")
            : QueryDiagnostic.Error(DiagnosticCodes.NotTranslatable, $"This query can't be written as SQL for the merge engine yet: {e.Message}", 0, text.Length)));
         return new PreparedQuery(this, request, parameters, program, diagnostics, plan, []);
      }

      List<QueryFragment> fragments = [];
      foreach (PlannedFragment planned in federated.Fragments)
      {
         long? estimate = PlanAnalysis.EstimateRows(planned.Root);
         fragments.Add(new QueryFragment(planned.Source, planned.Dialect, planned.Statement) { Table = planned.Table.Table, Columns = planned.Columns, EstimatedRows = estimate });
         if (estimate > Options.LargeFetchRows)
         {
            diagnostics.Add(QueryDiagnostic.Warning(DiagnosticCodes.LargeFetch,
               $"This query fetches about {estimate.Value.ToString("N0", CultureInfo.InvariantCulture)} rows from {planned.Source.Alias} into the merge engine", 0, text.Length));
         }
      }
      return new PreparedQuery(this, request, parameters, program, diagnostics, plan, fragments) { Merge = federated.Merge, MergeDialect = mergeDialect, Federation = federated };
   }

   public async Task<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
      await Prepare(request).ExecuteAsync(cancellationToken).ConfigureAwait(false);

   internal SourceProvider Provider(SourceInfo source) => providers[source.ProviderKind];

   private static string List(IEnumerable<string> items)
   {
      List<string> all = items.ToList();
      return all.Count <= 2 ? string.Join(" and ", all) : string.Join(", ", all.Take(all.Count - 1)) + " and " + all[^1];
   }
}

/// <summary>
/// The SQL one source runs for a query: the whole query, or, when the query combines sources, the source's part,
/// whose rows are loaded into a table of the merge engine.
/// </summary>
public sealed class QueryFragment
{
   internal QueryFragment(SourceInfo source, SqlDialect dialect, SqlStatement statement)
   {
      Source = source;
      Dialect = dialect;
      Statement = statement;
   }

   public SourceInfo Source { get; }

   public SqlDialect Dialect { get; }

   public SqlStatement Statement { get; }

   public string Sql => Statement.Text;

   /// <summary>The merge table the rows are loaded into (<c>f1</c>); null when the fragment is the whole query.</summary>
   public string? Table { get; internal init; }

   /// <summary>The merge table's columns, as the fragment's SQL selects them; empty when the fragment is the whole query.</summary>
   public IReadOnlyList<MergeColumn> Columns { get; internal init; } = [];

   /// <summary>A guess at how many rows the fragment fetches, when there is one to make.</summary>
   public long? EstimatedRows { get; internal init; }

   public override string ToString() => $"-- {Source.Alias} ({Dialect.Name}){(Table != null ? " into " + Table : string.Empty)}{Environment.NewLine}{Statement}";
}

/// <summary>A query's failures, as diagnostics.</summary>
public sealed class QueryException(IReadOnlyList<QueryDiagnostic> diagnostics)
   : Exception(diagnostics.FirstOrDefault(d => d.IsError)?.Message ?? "The query can't run")
{
   public IReadOnlyList<QueryDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>A query that failed while it ran: a database error, or a value that didn't convert.</summary>
public sealed class QueryExecutionException(string message, Exception? inner = null) : Exception(message, inner);
