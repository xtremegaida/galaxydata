using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Dml;
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

   /// <summary>Whether a fragment joined on a key is fetched by the keys of the fragment it's joined to: see <see cref="BindJoinMode"/>.</summary>
   public BindJoinMode BindJoins { get; init; } = BindJoinMode.Adaptive;

   /// <summary>The most keys a fragment is fetched by; with more, it is fetched in full.</summary>
   public int MaxBindKeys { get; init; } = 10_000;

   /// <summary>The most keys in one statement (fewer when the source takes fewer parameters).</summary>
   public int MaxBindBatch { get; init; } = 2_000;

   /// <summary>Runs before each connection commits changes; tests make it fail, as a commit can.</summary>
   internal Func<SourceInfo, ValueTask>? BeforeCommit { get; init; }
}

/// <summary>
/// How a fragment joined on a key to another is fetched. Its partner is fetched first; with no keys, the fragment
/// isn't fetched at all.
/// </summary>
public enum BindJoinMode : byte
{
   /// <summary>By the keys, in batches, when there are at most <see cref="QueryEngineOptions.MaxBindKeys"/> and fewer than the rows it's expected to have.</summary>
   Adaptive,

   /// <summary>By the keys whenever there are at most <see cref="QueryEngineOptions.MaxBindKeys"/>: to compare.</summary>
   Always,

   /// <summary>Always in full, alongside the others.</summary>
   Never,
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
         fragment = new QueryFragment(source, dialect, statement) { Tables = PlanAnalysis.Tables(plan.Root) };
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
         federated = FederationPlanner.Plan(plan, s => providers[s.ProviderKind].Dialect, mergeDialect, Options.PushDown,
                                            bindJoins: Options.BindJoins != BindJoinMode.Never);
      }
      catch (Exception e) when (e is SqlTranslationException or NotSupportedException)
      {
         diagnostics.Add(single ?? (e is SqlTranslationException untranslatable
            ? Untranslatable(text, untranslatable, $"the merge engine ({mergeDialect.Name})")
            : QueryDiagnostic.Error(DiagnosticCodes.NotTranslatable, $"This query can't be written as SQL for the merge engine yet: {e.Message}", 0, text.Length)));
         return new PreparedQuery(this, request, parameters, program, diagnostics, plan, []);
      }

      List<QueryFragment> fragments = [];
      Dictionary<PlannedFragment, QueryFragment> made = [];
      foreach (PlannedFragment planned in federated.Fragments)
      {
         long? estimate = PlanAnalysis.EstimateRows(planned.Root);
         QueryFragment fragment = new(planned.Source, planned.Dialect, planned.Statement)
         {
            Tables = PlanAnalysis.Tables(planned.Root),
            InMergeEngine = providers[planned.Source.ProviderKind].Host is { } host && ReferenceEquals(host, Merge),
            Table = planned.Table?.Table,
            Value = planned.Value,
            Columns = planned.Columns,
            EstimatedRows = estimate,
            Planned = planned,
            BindTemplate = planned.BindJoin == null ? null : BindTemplate(planned),
         };
         fragments.Add(fragment);
         made[planned] = fragment;
      }
      foreach (QueryFragment fragment in fragments)
      {
         if (fragment.Planned!.BindJoin is { } bind)
         {
            fragment.BindJoin = new BindJoinInfo(made[bind.Driver], bind.DriverColumn, bind.Key.Name);
         }
      }
      foreach (QueryFragment fragment in fragments)
      {
         PlannedFragment planned = fragment.Planned!;
         long? estimate = fragment.EstimatedRows;
         if (planned.Table != null && planned.BindJoin == null && estimate > Options.LargeFetchRows)
         {
            diagnostics.Add(QueryDiagnostic.Warning(DiagnosticCodes.LargeFetch,
               $"This query fetches about {estimate.Value.ToString("N0", CultureInfo.InvariantCulture)} rows from {planned.Source.Alias} into the merge engine", 0, text.Length));
         }
      }
      return new PreparedQuery(this, request, parameters, program, diagnostics, plan, fragments) { Merge = federated.Merge, MergeDialect = mergeDialect, Federation = federated };
   }

   /// <summary>A bound fragment's SQL for one batch of keys, with a parameter standing for the batch.</summary>
   private static SqlStatement BindTemplate(PlannedFragment fragment)
   {
      PlanColumn key = fragment.BindJoin!.Key;
      PlanParameter keys = new(ParameterSource.Runtime, $"keys of {fragment.BindJoin.Driver.Name}.{fragment.BindJoin.DriverColumn}", key.Type);
      return BindBatch(fragment, [keys]);
   }

   /// <summary>A bound fragment's SQL, keeping the rows whose key is one of <paramref name="keys"/>.</summary>
   internal static SqlStatement BindBatch(PlannedFragment fragment, IReadOnlyList<PlanExpr> keys)
   {
      PlanColumn key = fragment.BindJoin!.Key;
      PlanInList among = new(new PlanColumnRef(key), keys, negated: false, ScalarType.Boolean.WithNullable(key.Type.Nullable));
      return SqlBuilder.BuildFragment(new FilterNode(fragment.Root, among), fragment.Selected, fragment.Columns.Select(c => c.Name).ToList(),
                                      fragment.Dialect, FederationPlanner.BuildOptions(fragment.Source));
   }

   public async Task<QueryResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
      await Prepare(request).ExecuteAsync(cancellationToken).ConfigureAwait(false);

   /// <summary>Plans changes to rows of the catalog's tables, without running anything: see <see cref="DmlPlanner"/>.</summary>
   public DmlPlan PlanChanges(ChangeSet changes) =>
      DmlPlanner.Plan(changes, s => providers.TryGetValue(s.ProviderKind, out SourceProvider? provider) ? provider.Dialect : null);

   /// <summary>
   /// A script a person wrote or edited, to run on <paramref name="source"/>: split into statements, which may only
   /// change data (see <see cref="DmlGuard"/>) unless <paramref name="allowAnyStatement"/>. How many rows they change
   /// isn't checked. Throws <see cref="DmlScriptException"/> with the problems found.
   /// </summary>
   public DmlScript ParseScript(SourceInfo source, string text, bool allowAnyStatement = false)
   {
      ArgumentNullException.ThrowIfNull(source);
      ArgumentNullException.ThrowIfNull(text);
      if (!providers.TryGetValue(source.ProviderKind, out SourceProvider? provider))
      {
         throw new DmlScriptException(source, [new ScriptProblem($"{source.Alias} is a {source.ProviderKind} source, and no provider for those is registered", 0, 0, 1)]);
      }
      if (source.IsReadOnly || !source.SupportsDml)
      {
         throw new DmlScriptException(source, [new ScriptProblem($"{source.Alias} can't be changed: it is read-only", 0, 0, 1)]);
      }
      SqlDialect dialect = provider.Dialect;
      SplitScript script = SqlScriptSplitter.Split(text, dialect);
      List<ScriptProblem> problems = [.. DmlGuard.Check(script, dialect, allowAnyStatement)];
      if (script.Statements.Count == 0 && problems.Count == 0) { problems.Add(new ScriptProblem("The script has no statements", 0, text.Length, 1)); }
      if (problems.Count > 0) { throw new DmlScriptException(source, problems); }
      // Where the database's count of rows changed includes its triggers' (SQL Server), the statement's own is read after it.
      string? count = DmlPlanner.RowCountQuery(dialect);
      List<DmlStatement> statements = script.Statements.Select((s, i) => new DmlStatement(KindOf(s.Keyword), count == null ? s.Text : s.Text + ";" + Environment.NewLine + count, [], s.Text, s.Text)
      {
         Description = $"statement {(i + 1).ToString(CultureInfo.InvariantCulture)} (line {s.Line.ToString(CultureInfo.InvariantCulture)})",
         Counting = count == null ? DmlRowCount.Affected : DmlRowCount.Selected,
      }).ToList();
      return new DmlScript(source, dialect, statements, isEdited: true);
   }

   private static DmlStatementKind KindOf(string? keyword) => keyword switch
   {
      "INSERT" or "REPLACE" => DmlStatementKind.Insert,
      "UPDATE" => DmlStatementKind.Update,
      "DELETE" => DmlStatementKind.Delete,
      "MERGE" => DmlStatementKind.Merge,
      _ => DmlStatementKind.Other,
   };

   /// <summary>Runs a plan's scripts as one change (see <see cref="CommitAsync(IEnumerable{DmlScript}, CancellationToken)"/>); a plan with issues can't run.</summary>
   public Task<DmlResult> CommitAsync(DmlPlan plan, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(plan);
      if (!plan.Success) { throw new InvalidOperationException($"The changes can't be made: {plan.Issues[0]}"); }
      return CommitAsync(plan.Scripts, cancellationToken);
   }

   /// <summary>
   /// Runs scripts, one for each connection (planned ones, edited ones, or both), as one change: in a transaction on
   /// each connection, committed when every statement has run and changed the rows it had to. Failures are in the
   /// result, not thrown: nothing is committed, or, should a commit fail after another succeeded, only part.
   /// </summary>
   public Task<DmlResult> CommitAsync(IEnumerable<DmlScript> scripts, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(scripts);
      List<DmlScript> all = scripts.ToList();
      if (all.FirstOrDefault(s => !providers.ContainsKey(s.Source.ProviderKind)) is { } unserved)
      {
         throw new InvalidOperationException($"{unserved.Source.Alias} is a {unserved.Source.ProviderKind} source, and no provider for those is registered");
      }
      return new DmlExecutor(this).RunAsync(all, cancellationToken);
   }

   internal SourceProvider Provider(SourceInfo source) => providers[source.ProviderKind];

   /// <summary>Readies what a fragment reads before its statements run (an Excel folder loads the sheets that changed).</summary>
   internal ValueTask PrepareReadAsync(QueryFragment fragment, CancellationToken cancellationToken) =>
      Provider(fragment.Source).PrepareReadAsync(fragment.Source, fragment.Tables, Options, cancellationToken);

   /// <summary>A connection to a source, readied for queries; the caller disposes it.</summary>
   internal async ValueTask<DbConnection> OpenAsync(SourceInfo source, CancellationToken cancellationToken)
   {
      SourceProvider provider = Provider(source);
      DbConnection connection = await provider.OpenConnectionAsync(source, Connections, cancellationToken).ConfigureAwait(false);
      try
      {
         await provider.PrepareConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
         return connection;
      }
      catch
      {
         await connection.DisposeAsync().ConfigureAwait(false);
         throw;
      }
   }

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

   /// <summary>The tables the fragment reads.</summary>
   public IReadOnlyList<TableEntity> Tables { get; internal init; } = [];

   /// <summary>
   /// Whether the source's tables are in the merge engine's own database (an Excel folder's sheets), so its rows are
   /// copied into the merge table there, never leaving it.
   /// </summary>
   public bool InMergeEngine { get; internal init; }

   /// <summary>The merge table the rows are loaded into (<c>f1</c>); null when the fragment is the whole query or a value.</summary>
   public string? Table { get; internal init; }

   /// <summary>
   /// For a scalar subquery that runs before the fragments that use it: the name of the runtime value it gives
   /// (<c>s1</c>), which their SQL takes as a parameter.
   /// </summary>
   public string? Value { get; internal init; }

   /// <summary>Set when the fragment may be fetched by the keys of another: which, and on what.</summary>
   public BindJoinInfo? BindJoin { get; internal set; }

   /// <summary>For a fragment fetched by keys: its SQL for one batch of them, the batch as one parameter.</summary>
   public SqlStatement? BindTemplate { get; internal init; }

   internal PlannedFragment? Planned { get; init; }

   /// <summary>The merge table's columns, as the fragment's SQL selects them; empty when the fragment is the whole query.</summary>
   public IReadOnlyList<MergeColumn> Columns { get; internal init; } = [];

   /// <summary>A guess at how many rows the fragment fetches, when there is one to make.</summary>
   public long? EstimatedRows { get; internal init; }

   public override string ToString() =>
      $"-- {Source.Alias} ({Dialect.Name}){(Table != null ? " into " + Table : Value != null ? " for " + Value : string.Empty)}{Environment.NewLine}{Statement}";
}

/// <summary>
/// A fragment's rows only matter where its <see cref="Column"/> equals a value of <see cref="DriverColumn"/> in the
/// table of <see cref="Driver"/>, which is fetched first.
/// </summary>
public sealed record BindJoinInfo(QueryFragment Driver, string DriverColumn, string Column)
{
   public override string ToString() => $"{Column} by the keys of {Driver.Table}.{DriverColumn}";
}

/// <summary>A query's failures, as diagnostics.</summary>
public sealed class QueryException(IReadOnlyList<QueryDiagnostic> diagnostics)
   : Exception(diagnostics.FirstOrDefault(d => d.IsError)?.Message ?? "The query can't run")
{
   public IReadOnlyList<QueryDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>A query that failed while it ran: a database error, or a value that didn't convert.</summary>
public sealed class QueryExecutionException(string message, Exception? inner = null) : Exception(message, inner);
