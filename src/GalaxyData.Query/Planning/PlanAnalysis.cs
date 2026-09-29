using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Planning.Optimizer;

namespace GalaxyData.Query.Planning;

/// <summary>Questions about plans: which columns expressions read, and which a (sub)plan reads from outside it.</summary>
internal static class PlanAnalysis
{
   /// <summary>The expressions a node evaluates itself, not those of its inputs.</summary>
   public static IEnumerable<PlanExpr> Expressions(PlanNode node) => node switch
   {
      FilterNode filter => [filter.Predicate],
      ProjectNode project => project.Items.Select(i => i.Expr),
      JoinNode { Condition: { } condition } => [condition],
      SortNode sort => sort.Keys.Select(k => k.Expr),
      LimitNode limit => new[] { limit.Count, limit.Offset }.OfType<PlanExpr>(),
      AggregateNode aggregate => aggregate.Keys.Select(k => k.Expr).Concat(aggregate.Aggregates.Select(a => a.Argument).OfType<PlanExpr>()),
      _ => [],
   };

   /// <summary>The columns an expression reads; for a subquery in it, the columns it reads from outside.</summary>
   public static void Columns(PlanExpr expr, ISet<PlanColumn> into)
   {
      switch (expr)
      {
         case PlanColumnRef reference:
            into.Add(reference.Column);
            break;
         case PlanUnary unary:
            Columns(unary.Operand, into);
            break;
         case PlanBinary binary:
            Columns(binary.Left, into);
            Columns(binary.Right, into);
            break;
         case PlanIsNull isNull:
            Columns(isNull.Operand, into);
            break;
         case PlanInList inList:
            Columns(inList.Operand, into);
            foreach (PlanExpr item in inList.Items) { Columns(item, into); }
            break;
         case PlanConditional conditional:
            Columns(conditional.Condition, into);
            Columns(conditional.WhenTrue, into);
            Columns(conditional.WhenFalse, into);
            break;
         case PlanFunction call:
            foreach (PlanExpr argument in call.Arguments) { Columns(argument, into); }
            break;
         case PlanSubquery subquery:
            if (subquery.Operand != null) { Columns(subquery.Operand, into); }
            into.UnionWith(FreeColumns(subquery.Plan));
            break;
      }
   }

   public static HashSet<PlanColumn> Columns(PlanExpr expr)
   {
      HashSet<PlanColumn> columns = [];
      Columns(expr, columns);
      return columns;
   }

   /// <summary>The columns a plan reads that none of its operators produce: its correlation with the plan around it.</summary>
   public static HashSet<PlanColumn> FreeColumns(PlanNode plan)
   {
      HashSet<PlanColumn> read = [];
      HashSet<PlanColumn> produced = [];
      Walk(plan, read, produced);
      read.ExceptWith(produced);
      return read;
   }

   private static void Walk(PlanNode node, HashSet<PlanColumn> read, HashSet<PlanColumn> produced)
   {
      produced.UnionWith(node.Output);
      if (node is ScanNode scan) { produced.UnionWith(scan.Output); }
      foreach (PlanExpr expr in Expressions(node)) { Columns(expr, read); }
      foreach (PlanNode input in node.Inputs) { Walk(input, read, produced); }
   }

   public static bool IsCorrelated(PlanNode plan) => FreeColumns(plan).Count > 0;

   /// <summary>The conjuncts of a condition: <c>a and (b and c)</c> is a, b, c.</summary>
   public static List<PlanExpr> Conjuncts(PlanExpr condition)
   {
      List<PlanExpr> parts = [];
      Split(condition, parts);
      return parts;
   }

   private static void Split(PlanExpr expr, List<PlanExpr> parts)
   {
      if (expr is PlanBinary { Op: Binding.BinaryOp.And } and)
      {
         Split(and.Left, parts);
         Split(and.Right, parts);
      }
      else
      {
         parts.Add(expr);
      }
   }

   /// <summary>
   /// The sort that orders a plan's rows, restated over the plan's output (through renaming projections): not sorted
   /// when nothing orders them, sorted with no keys when the order can't be restated.
   /// </summary>
   public static (bool Sorted, List<PlanSortKey>? Keys) OrderOf(PlanNode node)
   {
      switch (node)
      {
         case SortNode sort:
            return (true, [.. sort.Keys]);
         case FilterNode filter:
            return OrderOf(filter.Input);
         case LimitNode limit:
            return OrderOf(limit.Input);
         case JoinNode { Kind: JoinKind.Inner or JoinKind.Left or JoinKind.Semi or JoinKind.Anti } join:
            // The left side's rows pass through the join in their order (a semi or anti join only filters them).
            return OrderOf(join.Left);
         case ProjectNode project:
         {
            (bool sorted, List<PlanSortKey>? keys) = OrderOf(project.Input);
            if (keys == null) { return (sorted, null); }
            Dictionary<PlanColumn, PlanExpr> renamed = [];
            foreach (ProjectItem item in project.Items)
            {
               if (item.Expr is PlanColumnRef reference) { renamed.TryAdd(reference.Column, new PlanColumnRef(item.Column)); }
            }
            List<PlanSortKey> restated = [];
            foreach (PlanSortKey key in keys)
            {
               if (!Columns(key.Expr).All(renamed.ContainsKey)) { return (true, null); }
               restated.Add(key with { Expr = PlanRewriter.Substitute(key.Expr, renamed) });
            }
            return (true, restated);
         }
         default:
            return (false, null);
      }
   }

   /// <summary>The sources a plan reads, its subqueries' included, each once in the order first read.</summary>
   public static List<SourceInfo> Sources(PlanNode plan)
   {
      List<SourceInfo> sources = [];
      AddSources(plan, sources);
      return sources;
   }

   private static void AddSources(PlanNode node, List<SourceInfo> sources)
   {
      if (node is ScanNode scan && !sources.Any(s => string.Equals(s.Alias, scan.Entity.Source.Alias, StringComparison.Ordinal)))
      {
         sources.Add(scan.Entity.Source);
      }
      foreach (PlanNode subquery in Subqueries(node)) { AddSources(subquery, sources); }
      foreach (PlanNode input in node.Inputs) { AddSources(input, sources); }
   }

   /// <summary>The plans of the subqueries in a node's own expressions.</summary>
   public static List<PlanNode> Subqueries(PlanNode node)
   {
      List<PlanNode> plans = [];
      foreach (PlanExpr expr in Expressions(node))
      {
         PlanRewriter.Map(expr, e =>
         {
            if (e is PlanSubquery subquery) { plans.Add(subquery.Plan); }
            return null;
         });
      }
      return plans;
   }

   /// <summary>A guess at how many rows a plan produces, when it rests on sizes the sources reported (see <see cref="Cardinality"/>).</summary>
   public static long? EstimateRows(PlanNode node) => Cardinality.Estimate(node) is { Known: true } estimate ? estimate.Rows : null;

   /// <summary>
   /// Whether a join gives each left row once, whatever the right side holds: a left join that finds at most one
   /// right row, or an inner join along a navigation every row has (an enforced foreign key of non-null columns).
   /// </summary>
   public static bool KeepsLeftRows(JoinNode join)
   {
      if (IsCorrelated(join.Right) && FreeColumns(join.Right).Overlaps(join.Left.Output)) { return false; }
      return join.Kind switch
      {
         JoinKind.Left => MatchesAtMostOnce(join),
         JoinKind.Inner => join.Navigation is { Multiplicity: Multiplicity.One },
         _ => false,
      };
   }

   /// <summary>
   /// Whether each left row finds at most one right row: the join follows a many-to-one navigation, or the right side
   /// is grouped and the condition equates each of its keys with a value of the left row.
   /// </summary>
   public static bool MatchesAtMostOnce(JoinNode join)
   {
      if (join.Navigation is { IsCollection: false }) { return true; }
      if (join.Right is not AggregateNode { Keys.Count: > 0 } group || join.Condition == null) { return false; }
      HashSet<PlanColumn> left = [.. join.Left.Output];
      HashSet<PlanColumn> matched = [];
      foreach (PlanExpr part in Conjuncts(join.Condition))
      {
         if (part is not PlanBinary { Op: Binding.BinaryOp.Equal, Left: PlanColumnRef a, Right: PlanColumnRef b }) { continue; }
         if (left.Contains(a.Column)) { matched.Add(b.Column); }
         else if (left.Contains(b.Column)) { matched.Add(a.Column); }
      }
      return group.Keys.All(k => matched.Contains(k.Column));
   }

   /// <summary>The conjuncts joined with <c>and</c>; null for none.</summary>
   public static PlanExpr? Conjunction(IEnumerable<PlanExpr> parts)
   {
      PlanExpr? result = null;
      foreach (PlanExpr part in parts)
      {
         result = result == null
            ? part
            : new PlanBinary(Binding.BinaryOp.And, result, part, Types.ScalarType.Boolean.WithNullable(result.Type.Nullable || part.Type.Nullable));
      }
      return result;
   }
}
