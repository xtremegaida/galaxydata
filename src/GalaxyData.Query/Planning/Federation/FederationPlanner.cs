using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Planning.Optimizer;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Planning.Federation;

/// <summary>A fragment's rows as a table of the merge engine (<c>f1</c>), with the fragment root's own columns.</summary>
internal sealed class MergeTableNode : PlanNode
{
   public MergeTableNode(int number, IReadOnlyList<PlanColumn> output, IReadOnlyList<string> names, RowEstimate estimate)
   {
      Number = number;
      Output = output;
      Names = names;
      Estimate = estimate;
   }

   public int Number { get; }

   public string Table => "f" + Number.ToString(CultureInfo.InvariantCulture);

   /// <summary>The table's column names, one for each output column.</summary>
   public IReadOnlyList<string> Names { get; }

   /// <summary>How many rows the fragment is expected to give.</summary>
   public RowEstimate Estimate { get; }

   public override IReadOnlyList<PlanColumn> Output { get; }

   public override IReadOnlyList<PlanNode> Inputs => [];
}

/// <summary>
/// A fragment fetched by the keys of another: its rows only matter where <see cref="Key"/> equals a value of the
/// driver's column, so once the driver's table is loaded its distinct values are looked up in batches.
/// </summary>
internal sealed record PlannedBindJoin(PlannedFragment Driver, string DriverColumn, PlanColumn Key);

/// <summary>
/// A part of a plan one source runs whole: rows loaded into <see cref="Table"/>, or, for a scalar subquery that runs
/// first, the value of the runtime parameter <see cref="Value"/>.
/// </summary>
internal sealed class PlannedFragment(SourceInfo source, SqlDialect dialect, PlanNode root, MergeTableNode? table, string? value)
{
   public SourceInfo Source { get; } = source;

   public SqlDialect Dialect { get; } = dialect;

   public PlanNode Root { get; } = root;

   public MergeTableNode? Table { get; } = table;

   public string? Value { get; } = value;

   public PlannedBindJoin? BindJoin { get; set; }

   /// <summary>The SQL, selecting only the columns the merge SQL reads.</summary>
   public SqlStatement Statement { get; set; } = null!;

   /// <summary>The columns the SQL selects (their plan columns), in order; empty for a placeholder.</summary>
   public IReadOnlyList<PlanColumn> Selected { get; set; } = [];

   /// <summary>The merge table's columns (or the value's), as the SQL selects them: a placeholder when the merge SQL reads none.</summary>
   public IReadOnlyList<MergeColumn> Columns { get; set; } = [];

   public string Name => Table?.Table ?? Value!;
}

/// <summary>A plan split into the fragments its sources run and the plan the merge engine runs over their rows.</summary>
internal sealed class FederatedPlan
{
   public required IReadOnlyList<PlannedFragment> Fragments { get; init; }

   /// <summary>The plan with each fragment replaced by its merge table.</summary>
   public required PlanNode MergeRoot { get; init; }

   /// <summary>The merge engine's SQL for <see cref="MergeRoot"/>.</summary>
   public required SqlStatement Merge { get; init; }

   /// <summary>Where each operator of the original plan runs: a source's alias, or <see cref="FederationPlanner.MergeSite"/>.</summary>
   public required IReadOnlyDictionary<PlanNode, string> Sites { get; init; }
}

/// <summary>
/// Splits a plan between its sources and the merge engine. From the root down, the largest parts that read one
/// source, depend on nothing around them and can be written in that source's SQL become fragments; the operators
/// above them run in the merge engine over the fragments' rows. A filter on the boundary leaves the conditions the
/// source can check in the fragment. A fragment whose rows are ordered is sorted again in the merge engine, since
/// loading rows into a table loses their order.
/// </summary>
/// <remarks>
/// Two things let sources do more of the work. A scalar subquery of one source in an operator that reads others
/// runs first, and the operator gets its value as a runtime parameter, so it may run in a source itself. And a
/// fragment on the side of a join whose rows only matter when they match (the right of a left, semi or anti join,
/// the larger side of an inner join) is marked to be fetched by the keys of the fragment its partner's key comes
/// from: execution decides from the number of keys whether to look them up.
/// </remarks>
internal sealed class FederationPlanner
{
   public const string MergeSite = "merge";

   private readonly Func<SourceInfo, SqlDialect?> dialects;
   private readonly Func<PlanNode, SourceInfo, bool>? runs;
   private readonly bool pushDown;
   private readonly List<PlannedFragment> fragments = [];
   private readonly Dictionary<PlanNode, string> sites = [];
   private readonly Dictionary<PlanNode, bool> builds = [];
   private readonly Dictionary<MergeTableNode, PlannedFragment> tables = [];
   private int values;

   private FederationPlanner(Func<SourceInfo, SqlDialect?> dialects, Func<PlanNode, SourceInfo, bool>? runs, bool pushDown)
   {
      this.dialects = dialects;
      this.runs = runs;
      this.pushDown = pushDown;
   }

   /// <summary>
   /// Splits a plan and writes its SQL; <paramref name="dialects"/> gives the SQL each source speaks (null for none).
   /// Without <paramref name="pushDown"/>, fragments only read tables and every other operator runs in the merge
   /// engine. <paramref name="runs"/> can keep parts from a source whose SQL could express them (a source that
   /// lacks a function its dialect has). <paramref name="bindJoins"/> marks fragments to fetch by keys. Throws
   /// <see cref="SqlTranslationException"/> or <see cref="NotSupportedException"/> when the merge engine's SQL
   /// can't express the rest.
   /// </summary>
   public static FederatedPlan Plan(LogicalPlan plan, Func<SourceInfo, SqlDialect?> dialects, SqlDialect mergeDialect, bool pushDown,
                                    Func<PlanNode, SourceInfo, bool>? runs = null, bool bindJoins = true)
   {
      ArgumentNullException.ThrowIfNull(plan);
      FederationPlanner planner = new(dialects, runs, pushDown);
      PlanNode root = planner.Cut(plan.Root);
      if (bindJoins && pushDown) { planner.BindJoins(root); }
      Dictionary<MergeTableNode, HashSet<PlanColumn>> reads = [];
      SqlStatement merge = SqlBuilder.BuildMerge(plan.WithRoot(root), mergeDialect, reads);
      foreach (PlannedFragment fragment in planner.fragments)
      {
         Finish(fragment, fragment.Table == null ? [.. fragment.Root.Output] : reads.GetValueOrDefault(fragment.Table) ?? []);
      }
      // The keys a bound fragment is fetched by must be in its driver's table.
      foreach (PlannedFragment fragment in planner.fragments.Where(f => f.BindJoin != null))
      {
         PlannedBindJoin bind = fragment.BindJoin!;
         if (!bind.Driver.Columns.Any(c => c.Name == bind.DriverColumn) || !fragment.Selected.Contains(bind.Key)) { fragment.BindJoin = null; }
      }
      return new FederatedPlan { Fragments = planner.fragments, MergeRoot = root, Merge = merge, Sites = planner.sites };
   }

   public static SqlBuildOptions BuildOptions(SourceInfo source) => new() { DefaultSchema = source.DefaultSchema };

   /// <summary>Writes a fragment's SQL for the columns the merge SQL reads; rows without columns still need one in a table.</summary>
   private static void Finish(PlannedFragment fragment, HashSet<PlanColumn> read)
   {
      IReadOnlyList<PlanColumn> output = fragment.Root.Output;
      IReadOnlyList<string> allNames = fragment.Table?.Names ?? Names(output);
      // A bound fragment's key is fetched too: the lookup filters on it.
      if (fragment.BindJoin is { } bind) { read.Add(bind.Key); }
      List<int> kept = Enumerable.Range(0, output.Count).Where(i => read.Contains(output[i])).ToList();
      List<PlanColumn> columns = kept.Select(i => output[i]).ToList();
      List<string> names = kept.Select(i => allNames[i]).ToList();
      fragment.Statement = SqlBuilder.BuildFragment(fragment.Root, columns, names, fragment.Dialect, BuildOptions(fragment.Source));
      fragment.Selected = columns;
      fragment.Columns = columns.Count == 0 ? [new MergeColumn("one", ScalarType.Int64)] : columns.Select((c, i) => new MergeColumn(names[i], c.Type)).ToList();
   }

   /// <summary>The node as the merge engine sees it: a fragment's table, or the node over its inputs' parts.</summary>
   private PlanNode Cut(PlanNode node)
   {
      PlanNode original = node;
      node = Hoist(node);
      if (Fragment(node) is { } fragment)
      {
         if (!ReferenceEquals(node, original)) { sites[original] = sites[node]; }
         return fragment;
      }
      sites[original] = MergeSite;
      if (node is FilterNode filter && Split(filter) is { } split) { return split; }
      PlanNode rebuilt = PlanRewriter.WithInputs(node, node.Inputs.Select(Cut).ToList());
      return PlanRewriter.MapSubqueries(rebuilt, Cut);
   }

   /// <summary>
   /// The node with the scalar subqueries it evaluates replaced by runtime parameters, when it reads more than one
   /// source and a subquery reads one source on its own: the subquery runs first, as a fragment of its own.
   /// </summary>
   private PlanNode Hoist(PlanNode node)
   {
      if (!pushDown || PlanAnalysis.Sources(node).Count <= 1) { return node; }
      return PlanRewriter.MapExpressions(node, expr => PlanRewriter.Map(expr, e =>
      {
         if (e is not PlanSubquery { Kind: SubqueryKind.Scalar } subquery || PlanAnalysis.IsCorrelated(subquery.Plan)) { return null; }
         if (Candidate(subquery.Plan) is not { } site) { return null; }
         string name = "s" + (++values).ToString(CultureInfo.InvariantCulture);
         fragments.Add(new PlannedFragment(site.Source, site.Dialect, subquery.Plan, null, name));
         Mark(subquery.Plan, site.Source.Alias);
         return new PlanParameter(ParameterSource.Runtime, name, subquery.Type);
      }));
   }

   /// <summary>A filter whose input is a fragment and some of whose conditions the source can check: those go in the fragment.</summary>
   private PlanNode? Split(FilterNode filter)
   {
      if (!pushDown) { return null; }
      List<PlanExpr> conditions = PlanAnalysis.Conjuncts(filter.Predicate);
      List<PlanExpr> local = conditions.Where(c => Candidate(new FilterNode(filter.Input, c)) != null).ToList();
      if (local.Count == 0 || local.Count == conditions.Count) { return null; }
      if (Fragment(new FilterNode(filter.Input, PlanAnalysis.Conjunction(local)!)) is not { } below) { return null; }
      FilterNode rest = new(below, PlanAnalysis.Conjunction(conditions.Where(c => !local.Contains(c)))!);
      return PlanRewriter.MapSubqueries(rest, Cut);
   }

   /// <summary>The node as a fragment's table (sorted again when its rows are ordered), when one source can run it whole.</summary>
   private PlanNode? Fragment(PlanNode node)
   {
      if (Candidate(node) is not { } site) { return null; }
      MergeTableNode table = new(fragments.Count(f => f.Table != null) + 1, node.Output, Names(node.Output), Cardinality.Estimate(node));
      PlannedFragment fragment = new(site.Source, site.Dialect, node, table, null);
      fragments.Add(fragment);
      tables[table] = fragment;
      Mark(node, site.Source.Alias);
      (bool sorted, List<PlanSortKey>? keys) = PlanAnalysis.OrderOf(node);
      return sorted ? new SortNode(table, keys!) : table;
   }

   private (SourceInfo Source, SqlDialect Dialect)? Candidate(PlanNode node)
   {
      if (!pushDown && node is not ScanNode) { return null; }
      List<SourceInfo> read = PlanAnalysis.Sources(node);
      if (read.Count != 1 || dialects(read[0]) is not { } dialect || PlanAnalysis.IsCorrelated(node)) { return null; }
      // The order must survive as a sort over the fragment's own columns.
      (bool sorted, List<PlanSortKey>? keys) = PlanAnalysis.OrderOf(node);
      if (sorted && (keys == null || keys.Any(k => PlanRewriter.ContainsSubquery(k.Expr)))) { return null; }
      if (runs != null && !runs(node, read[0])) { return null; }
      return Builds(node, read[0], dialect) ? (read[0], dialect) : null;
   }

   /// <summary>Whether the source's SQL can express the node: its functions, and shapes such as correlated joins.</summary>
   private bool Builds(PlanNode node, SourceInfo source, SqlDialect dialect)
   {
      if (!builds.TryGetValue(node, out bool ok))
      {
         try
         {
            SqlBuilder.BuildFragment(node, node.Output, Names(node.Output), dialect, BuildOptions(source));
            ok = true;
         }
         catch (Exception e) when (e is SqlTranslationException or NotSupportedException)
         {
            ok = false;
         }
         builds[node] = ok;
      }
      return ok;
   }

   private void Mark(PlanNode node, string site)
   {
      sites[node] = site;
      foreach (PlanNode subquery in PlanAnalysis.Subqueries(node)) { Mark(subquery, site); }
      foreach (PlanNode input in node.Inputs) { Mark(input, site); }
   }

   #region Bind joins

   /// <summary>
   /// Marks the fragments to fetch by the keys of others, from the joins of the merge plan and its subqueries: the right side of a left,
   /// semi or anti join (the left side's rows are kept whatever it holds), or the larger side of an inner join. The
   /// key must come straight from a fragment's column on each side.
   /// </summary>
   private void BindJoins(PlanNode node)
   {
      // A join in a subquery too: the driver's whole table has every key the subquery could look for.
      foreach (PlanNode subquery in PlanAnalysis.Subqueries(node)) { BindJoins(subquery); }
      foreach (PlanNode input in node.Inputs) { BindJoins(input); }
      if (node is not JoinNode { Kind: JoinKind.Inner or JoinKind.Left or JoinKind.Semi or JoinKind.Anti, Condition: { } condition } join) { return; }
      HashSet<PlanColumn> left = [.. join.Left.Output];
      HashSet<PlanColumn> right = [.. join.Right.Output];
      foreach (PlanExpr part in PlanAnalysis.Conjuncts(condition))
      {
         if (part is not PlanBinary { Op: Binding.BinaryOp.Equal, Left: PlanColumnRef a, Right: PlanColumnRef b }) { continue; }
         (PlanColumn? leftKey, PlanColumn? rightKey) = left.Contains(a.Column) && right.Contains(b.Column) ? (a.Column, b.Column)
            : left.Contains(b.Column) && right.Contains(a.Column) ? (b.Column, a.Column)
            : (null, null);
         if (leftKey == null || rightKey == null) { continue; }
         // The right side, looked up by the left's keys; for an inner join, the other way round when the left is larger.
         bool reverse = join.Kind == JoinKind.Inner && Cardinality.Estimate(join.Left).Rows > Cardinality.Estimate(join.Right).Rows;
         if ((reverse ? Bind(join.Left, leftKey, join.Right, rightKey) : Bind(join.Right, rightKey, join.Left, leftKey)) ||
             (join.Kind == JoinKind.Inner && (reverse ? Bind(join.Right, rightKey, join.Left, leftKey) : Bind(join.Left, leftKey, join.Right, rightKey))))
         {
            return;
         }
      }
   }

   /// <summary>Marks the fragment <paramref name="targetKey"/> comes from to be fetched by the values <paramref name="driverKey"/> comes from.</summary>
   private bool Bind(PlanNode target, PlanColumn targetKey, PlanNode driver, PlanColumn driverKey)
   {
      if (Target(target, targetKey) is not (MergeTableNode table, PlanColumn key) || tables[table] is not { BindJoin: null } fragment) { return false; }
      // The target's source must find exactly the rows a key matches in the merge engine (or more).
      if (!fragment.Dialect.ComparesExactly(key.Type) || !SqlDialect.DuckDb.ComparesExactly(driverKey.Type)) { return false; }
      if (Driver(driver, driverKey) is not (MergeTableNode from, string column) || tables[from] is not { } source) { return false; }
      fragment.BindJoin = new PlannedBindJoin(source, column, key);
      return true;
   }

   /// <summary>
   /// The merge table and its column a key of the target side is, through operators that keep or drop whole rows by
   /// their own values (so dropping the rows no key matches first changes nothing): filters, projections, sorts, DISTINCT.
   /// </summary>
   private static (MergeTableNode, PlanColumn)? Target(PlanNode node, PlanColumn key) => node switch
   {
      MergeTableNode table when table.Output.Contains(key) => (table, key),
      FilterNode filter => Target(filter.Input, key),
      SortNode sort => Target(sort.Input, key),
      DistinctNode distinct => Target(distinct.Input, key),
      ProjectNode project when Below(project, key) is { } below => Target(project.Input, below),
      _ => null,
   };

   /// <summary>The merge table and the name of its column that a key of the driver side's rows was read from: whatever rows the side keeps.</summary>
   private static (MergeTableNode, string)? Driver(PlanNode node, PlanColumn key)
   {
      switch (node)
      {
         case MergeTableNode table:
         {
            int index = IndexOf(table.Output, key);
            return index < 0 ? null : (table, table.Names[index]);
         }
         case ProjectNode project:
            return Below(project, key) is { } below ? Driver(project.Input, below) : null;
         case JoinNode join:
            return join.Left.Output.Contains(key) ? Driver(join.Left, key) : join.Right.Output.Contains(key) ? Driver(join.Right, key) : null;
         case FilterNode or SortNode or LimitNode or DistinctNode:
            return Driver(node.Inputs[0], key);
         default:
            return null;
      }
   }

   /// <summary>The input column a projection's column passes on, renamed or not; null when it computes the value.</summary>
   private static PlanColumn? Below(ProjectNode project, PlanColumn column) =>
      project.Items.FirstOrDefault(i => ReferenceEquals(i.Column, column))?.Expr is PlanColumnRef reference ? reference.Column : null;

   private static int IndexOf(IReadOnlyList<PlanColumn> columns, PlanColumn column)
   {
      for (int i = 0; i < columns.Count; i++)
      {
         if (ReferenceEquals(columns[i], column)) { return i; }
      }
      return -1;
   }

   #endregion

   /// <summary>Names for a fragment's columns, unique in any case: <c>id</c>, <c>name</c>, <c>name_2</c>.</summary>
   public static List<string> Names(IReadOnlyList<PlanColumn> columns)
   {
      HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
      List<string> names = [];
      foreach (PlanColumn column in columns)
      {
         string stem = column.Name.Length == 0 ? "value" : column.Name;
         string name = stem;
         for (int n = 2; !taken.Add(name); n++) { name = stem + "_" + n.ToString(CultureInfo.InvariantCulture); }
         names.Add(name);
      }
      return names;
   }
}
