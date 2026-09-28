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
   public MergeTableNode(int number, IReadOnlyList<PlanColumn> output, IReadOnlyList<string> names)
   {
      Number = number;
      Output = output;
      Names = names;
   }

   public int Number { get; }

   public string Table => "f" + Number.ToString(CultureInfo.InvariantCulture);

   /// <summary>The table's column names, one for each output column.</summary>
   public IReadOnlyList<string> Names { get; }

   public override IReadOnlyList<PlanColumn> Output { get; }

   public override IReadOnlyList<PlanNode> Inputs => [];
}

/// <summary>A part of a plan one source runs whole; its rows are loaded into <see cref="Table"/>.</summary>
internal sealed class PlannedFragment(SourceInfo source, SqlDialect dialect, PlanNode root, MergeTableNode table)
{
   public SourceInfo Source { get; } = source;

   public SqlDialect Dialect { get; } = dialect;

   public PlanNode Root { get; } = root;

   public MergeTableNode Table { get; } = table;

   /// <summary>The SQL, selecting only the columns the merge SQL reads.</summary>
   public SqlStatement Statement { get; set; } = null!;

   /// <summary>The merge table's columns, as the SQL selects them: a placeholder when the merge SQL reads none.</summary>
   public IReadOnlyList<MergeColumn> Columns { get; set; } = [];
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
internal sealed class FederationPlanner
{
   public const string MergeSite = "merge";

   private readonly Func<SourceInfo, SqlDialect?> dialects;
   private readonly Func<PlanNode, SourceInfo, bool>? runs;
   private readonly bool pushDown;
   private readonly List<PlannedFragment> fragments = [];
   private readonly Dictionary<PlanNode, string> sites = [];
   private readonly Dictionary<PlanNode, bool> builds = [];

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
   /// lacks a function its dialect has). Throws <see cref="SqlTranslationException"/> or
   /// <see cref="NotSupportedException"/> when the merge engine's SQL can't express the rest.
   /// </summary>
   public static FederatedPlan Plan(LogicalPlan plan, Func<SourceInfo, SqlDialect?> dialects, SqlDialect mergeDialect, bool pushDown,
                                    Func<PlanNode, SourceInfo, bool>? runs = null)
   {
      ArgumentNullException.ThrowIfNull(plan);
      FederationPlanner planner = new(dialects, runs, pushDown);
      PlanNode root = planner.Cut(plan.Root);
      Dictionary<MergeTableNode, HashSet<PlanColumn>> reads = [];
      SqlStatement merge = SqlBuilder.BuildMerge(plan.WithRoot(root), mergeDialect, reads);
      foreach (PlannedFragment fragment in planner.fragments) { Finish(fragment, reads.GetValueOrDefault(fragment.Table) ?? []); }
      return new FederatedPlan { Fragments = planner.fragments, MergeRoot = root, Merge = merge, Sites = planner.sites };
   }

   /// <summary>Writes a fragment's SQL for the columns the merge SQL reads; rows without columns still need one in a table.</summary>
   private static void Finish(PlannedFragment fragment, HashSet<PlanColumn> read)
   {
      MergeTableNode table = fragment.Table;
      List<int> kept = Enumerable.Range(0, table.Output.Count).Where(i => read.Contains(table.Output[i])).ToList();
      List<PlanColumn> columns = kept.Select(i => table.Output[i]).ToList();
      List<string> names = kept.Select(i => table.Names[i]).ToList();
      fragment.Statement = SqlBuilder.BuildFragment(fragment.Root, columns, names, fragment.Dialect, BuildOptions(fragment.Source));
      fragment.Columns = columns.Count == 0 ? [new MergeColumn("one", ScalarType.Int64)] : columns.Select((c, i) => new MergeColumn(names[i], c.Type)).ToList();
   }

   public static SqlBuildOptions BuildOptions(SourceInfo source) => new() { DefaultSchema = source.DefaultSchema };

   /// <summary>The node as the merge engine sees it: a fragment's table, or the node over its inputs' parts.</summary>
   private PlanNode Cut(PlanNode node)
   {
      if (Fragment(node) is { } fragment) { return fragment; }
      sites[node] = MergeSite;
      if (node is FilterNode filter && Split(filter) is { } split) { return split; }
      PlanNode rebuilt = PlanRewriter.WithInputs(node, node.Inputs.Select(Cut).ToList());
      return PlanRewriter.MapSubqueries(rebuilt, Cut);
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
      MergeTableNode table = new(fragments.Count + 1, node.Output, Names(node.Output));
      fragments.Add(new PlannedFragment(site.Source, site.Dialect, node, table));
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
