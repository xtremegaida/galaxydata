using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Sql;

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
}

/// <summary>
/// Runs queries against a catalog: <see cref="Prepare(QueryRequest)"/> binds and plans them and writes their SQL
/// without touching a database; the prepared query then runs in the source it reads from.
/// </summary>
public sealed class QueryEngine
{
   private readonly Dictionary<string, SourceProvider> providers = new(StringComparer.OrdinalIgnoreCase);

   public QueryEngine(ICatalog catalog, IConnectionFactory connections, IEnumerable<SourceProvider> providers, QueryEngineOptions? options = null)
   {
      ArgumentNullException.ThrowIfNull(catalog);
      ArgumentNullException.ThrowIfNull(connections);
      ArgumentNullException.ThrowIfNull(providers);
      Catalog = catalog;
      Connections = connections;
      Options = options ?? QueryEngineOptions.Default;
      foreach (SourceProvider provider in providers) { this.providers[provider.ProviderKind] = provider; }
   }

   public ICatalog Catalog { get; }

   public QueryEngineOptions Options { get; }

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
      List<QueryFragment> fragments = [];
      QueryFragment? fragment = Fragment(request.Text, plan, diagnostics);
      if (fragment != null) { fragments.Add(fragment); }
      return new PreparedQuery(this, request, parameters, program, diagnostics, plan, fragments);
   }

   public async Task<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
      await Prepare(request).ExecuteAsync(cancellationToken).ConfigureAwait(false);

   internal SourceProvider Provider(SourceInfo source) => providers[source.ProviderKind];

   /// <summary>The SQL for a plan that reads from one source; a diagnostic when it can't run in one.</summary>
   private QueryFragment? Fragment(string text, LogicalPlan plan, List<QueryDiagnostic> diagnostics)
   {
      List<SourceInfo> sources = [];
      CollectSources(plan.Root, sources);
      if (sources.Count > 1)
      {
         diagnostics.Add(QueryDiagnostic.Error(DiagnosticCodes.CrossSourceQuery,
            $"This query reads from {string.Join(" and ", sources.Select(s => s.Alias))}; queries that combine sources are not supported yet", 0, text.Length));
         return null;
      }
      SourceInfo? source = sources.Count == 1 ? sources[0] : Catalog.Sources.FirstOrDefault(s => providers.ContainsKey(s.ProviderKind));
      if (source == null)
      {
         diagnostics.Add(QueryDiagnostic.Error(DiagnosticCodes.NoSource, "There is no source to run this query in", 0, text.Length));
         return null;
      }
      if (!providers.TryGetValue(source.ProviderKind, out SourceProvider? provider))
      {
         diagnostics.Add(QueryDiagnostic.Error(DiagnosticCodes.NoProvider,
            $"'{source.Alias}' is a {source.ProviderKind} source, and no provider for those is registered", 0, text.Length));
         return null;
      }
      try
      {
         SqlStatement statement = SqlBuilder.Build(plan, provider.Dialect, new SqlBuildOptions { DefaultSchema = source.DefaultSchema });
         return new QueryFragment(source, provider.Dialect, statement);
      }
      catch (SqlTranslationException e)
      {
         SourceSpan span = e.Function.Span ?? new SourceSpan(0, text.Length);
         diagnostics.Add(QueryDiagnostic.Error(DiagnosticCodes.NotTranslatable,
            $"{e.Function.Function.Name}(...) can't run in {source.Alias}, a {e.Dialect.Name} source", span.Start, span.End));
         return null;
      }
      catch (NotSupportedException e)
      {
         diagnostics.Add(QueryDiagnostic.Error(DiagnosticCodes.NotTranslatable, $"This query can't be written as SQL for {source.Alias} yet: {e.Message}", 0, text.Length));
         return null;
      }
   }

   private static void CollectSources(PlanNode node, List<SourceInfo> sources)
   {
      if (node is ScanNode scan && !sources.Any(s => string.Equals(s.Alias, scan.Entity.Source.Alias, StringComparison.Ordinal)))
      {
         sources.Add(scan.Entity.Source);
      }
      foreach (PlanNode input in node.Inputs) { CollectSources(input, sources); }
   }
}

/// <summary>The SQL one source runs for a query.</summary>
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

   public override string ToString() => $"-- {Source.Alias} ({Dialect.Name}){Environment.NewLine}{Statement}";
}

/// <summary>A query's failures, as diagnostics.</summary>
public sealed class QueryException(IReadOnlyList<QueryDiagnostic> diagnostics)
   : Exception(diagnostics.FirstOrDefault(d => d.IsError)?.Message ?? "The query can't run")
{
   public IReadOnlyList<QueryDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>A query that failed while it ran: a database error, or a value that didn't convert.</summary>
public sealed class QueryExecutionException(string message, Exception? inner = null) : Exception(message, inner);
