using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;

namespace GalaxyData.Query.Planning.Optimizer;

/// <summary>
/// Drops what nothing above uses: projected items, aggregates, scanned columns, and joins whose right side's
/// columns aren't read when the join can't change the rows: many-to-one navigations (a left join keeps every row,
/// and an inner one through a required foreign key finds exactly one), and left joins to rows grouped by the join
/// key (at most one match each), such as the group joins aggregates are decorrelated into.
/// </summary>
internal static class ColumnPruner
{
   public static PlanNode Prune(PlanNode root) => Prune(root, [.. root.Output]);

   private static PlanNode Prune(PlanNode node, HashSet<PlanColumn> required)
   {
      switch (node)
      {
         case ScanNode scan:
         {
            List<ScanColumn> columns = scan.Columns.Where(c => required.Contains(c.Output)).ToList();
            return columns.Count == scan.Columns.Count ? scan : new ScanNode(scan.Entity, columns);
         }
         case FilterNode filter:
            return Subqueries(new FilterNode(Prune(filter.Input, With(required, filter.Predicate)), filter.Predicate));
         case ProjectNode project:
         {
            List<ProjectItem> items = project.Items.Where(i => required.Contains(i.Column)).ToList();
            HashSet<PlanColumn> below = [];
            foreach (ProjectItem item in items) { PlanAnalysis.Columns(item.Expr, below); }
            PlanNode input = Prune(project.Input, below);
            // A projection that now passes its input through as it is can go; so can one of no columns, which only
            // counts rows, as its input does.
            if (items.Count == 0 || (items.Count == input.Output.Count && items.Zip(input.Output).All(p => p.First.IsPassThrough && ReferenceEquals(p.First.Column, p.Second))))
            {
               return input;
            }
            return Subqueries(new ProjectNode(input, items));
         }
         case SortNode sort:
            return Subqueries(new SortNode(Prune(sort.Input, With(required, sort.Keys.Select(k => k.Expr))), sort.Keys));
         case LimitNode limit:
            return new LimitNode(Prune(limit.Input, required), limit.Count, limit.Offset);
         case DistinctNode distinct:
            return new DistinctNode(Prune(distinct.Input, [.. distinct.Input.Output]));
         case AggregateNode aggregate:
         {
            List<AggregateItem> aggregates = aggregate.Aggregates.Where(a => required.Contains(a.Column)).ToList();
            List<ProjectItem> keys = aggregate.Keys.Where(k => !aggregate.DependentKeys.Contains(k.Column) || required.Contains(k.Column)).ToList();
            HashSet<PlanColumn> below = [];
            foreach (ProjectItem key in keys) { PlanAnalysis.Columns(key.Expr, below); }
            foreach (AggregateItem item in aggregates.Where(a => a.Argument != null)) { PlanAnalysis.Columns(item.Argument!, below); }
            return Subqueries(new AggregateNode(Prune(aggregate.Input, below), keys, aggregates, aggregate.DependentKeys));
         }
         case SetOpNode { Operation: SetOperation.UnionAll } set when !set.Output.All(required.Contains):
         {
            // Without duplicate removal the columns are independent: keep the ones used, on both sides.
            List<int> kept = Enumerable.Range(0, set.Output.Count).Where(i => required.Contains(set.Output[i])).ToList();
            // The rows still count when no column is used, so each side keeps one.
            if (kept.Count == 0) { kept.Add(0); }
            PlanNode Side(PlanNode side)
            {
               List<PlanColumn> columns = kept.Select(i => side.Output[i]).ToList();
               return Prune(new ProjectNode(side, columns.Select(c => new ProjectItem(c, new PlanColumnRef(c)))), [.. columns]);
            }
            return new SetOpNode(set.Operation, Side(set.Left), Side(set.Right), kept.Select(i => set.Output[i]).ToList());
         }
         case SetOpNode set:
            return new SetOpNode(set.Operation, Prune(set.Left, [.. set.Left.Output]), Prune(set.Right, [.. set.Right.Output]), set.Output);
         case JoinNode join:
            return Join(join, required);
         default:
            return node;
      }
   }

   private static PlanNode Join(JoinNode join, HashSet<PlanColumn> required)
   {
      HashSet<PlanColumn> rightOutput = [.. join.Right.Output];
      if (join.Navigation is { } navigation && join.Kind is JoinKind.Left or JoinKind.Inner &&
          (join.Kind == JoinKind.Left || navigation.Multiplicity == Multiplicity.One) && !required.Overlaps(rightOutput))
      {
         return Prune(join.Left, required);
      }
      if (join.Kind == JoinKind.Left && !required.Overlaps(rightOutput) && MatchesAtMostOnce(join))
      {
         return Prune(join.Left, required);
      }
      HashSet<PlanColumn> all = join.Condition == null ? [.. required] : With(required, join.Condition);
      // A right side that still depends on the left row needs those left columns kept.
      all.UnionWith(PlanAnalysis.FreeColumns(join.Right));
      HashSet<PlanColumn> leftOutput = [.. join.Left.Output];
      PlanNode left = Prune(join.Left, [.. all.Where(leftOutput.Contains)]);
      PlanNode right = Prune(join.Right, [.. all.Where(rightOutput.Contains)]);
      return Subqueries(new JoinNode(join.Kind, left, right, join.Condition, join.Navigation));
   }

   /// <summary>
   /// Whether each left row finds at most one right row: the right side is grouped, and the condition equates each
   /// of its keys with a value of the left row.
   /// </summary>
   private static bool MatchesAtMostOnce(JoinNode join)
   {
      if (join.Right is not AggregateNode { Keys.Count: > 0 } group || join.Condition == null) { return false; }
      HashSet<PlanColumn> left = [.. join.Left.Output];
      HashSet<PlanColumn> matched = [];
      foreach (PlanExpr part in PlanAnalysis.Conjuncts(join.Condition))
      {
         if (part is not PlanBinary { Op: Binding.BinaryOp.Equal, Left: PlanColumnRef a, Right: PlanColumnRef b }) { continue; }
         if (left.Contains(a.Column)) { matched.Add(b.Column); }
         else if (left.Contains(b.Column)) { matched.Add(a.Column); }
      }
      return group.Keys.All(k => matched.Contains(k.Column));
   }

   /// <summary>The node with the plans of its subqueries pruned to what they give.</summary>
   private static PlanNode Subqueries(PlanNode node) => PlanRewriter.MapSubqueries(node, plan => Prune(plan, [.. plan.Output]));

   private static HashSet<PlanColumn> With(HashSet<PlanColumn> required, PlanExpr expr) => With(required, [expr]);

   private static HashSet<PlanColumn> With(HashSet<PlanColumn> required, IEnumerable<PlanExpr> exprs)
   {
      HashSet<PlanColumn> result = [.. required];
      foreach (PlanExpr expr in exprs) { PlanAnalysis.Columns(expr, result); }
      return result;
   }
}
