using System;
using System.Collections.Generic;
using System.Linq;

namespace GalaxyData.Query.Planning.Optimizer;

/// <summary>Rebuilding plan nodes with other inputs or expressions, and rewriting expressions.</summary>
internal static class PlanRewriter
{
   /// <summary>The node over other inputs, in the order of <see cref="PlanNode.Inputs"/>.</summary>
   public static PlanNode WithInputs(PlanNode node, IReadOnlyList<PlanNode> inputs)
   {
      if (inputs.Count == node.Inputs.Count && inputs.Zip(node.Inputs).All(p => ReferenceEquals(p.First, p.Second))) { return node; }
      return node switch
      {
         FilterNode filter => new FilterNode(inputs[0], filter.Predicate),
         ProjectNode project => new ProjectNode(inputs[0], project.Items),
         JoinNode join => new JoinNode(join.Kind, inputs[0], inputs[1], join.Condition, join.Navigation),
         SortNode sort => new SortNode(inputs[0], sort.Keys),
         LimitNode limit => new LimitNode(inputs[0], limit.Count, limit.Offset),
         DistinctNode => new DistinctNode(inputs[0]),
         AggregateNode aggregate => new AggregateNode(inputs[0], aggregate.Keys, aggregate.Aggregates),
         SetOpNode set => new SetOpNode(set.Operation, inputs[0], inputs[1], set.Output),
         _ => node,
      };
   }

   /// <summary>The node with each of its own expressions mapped; the same node when nothing changed.</summary>
   public static PlanNode MapExpressions(PlanNode node, Func<PlanExpr, PlanExpr> map)
   {
      switch (node)
      {
         case FilterNode filter:
         {
            PlanExpr predicate = map(filter.Predicate);
            return ReferenceEquals(predicate, filter.Predicate) ? node : new FilterNode(filter.Input, predicate);
         }
         case ProjectNode project:
         {
            List<ProjectItem> items = project.Items.Select(i => Item(i, map)).ToList();
            return items.Zip(project.Items).All(p => ReferenceEquals(p.First, p.Second)) ? node : new ProjectNode(project.Input, items);
         }
         case JoinNode { Condition: { } condition } join:
         {
            PlanExpr mapped = map(condition);
            return ReferenceEquals(mapped, condition) ? node : new JoinNode(join.Kind, join.Left, join.Right, mapped, join.Navigation);
         }
         case SortNode sort:
         {
            List<PlanSortKey> keys = sort.Keys.Select(k => map(k.Expr) is var e && !ReferenceEquals(e, k.Expr) ? k with { Expr = e } : k).ToList();
            return keys.Zip(sort.Keys).All(p => ReferenceEquals(p.First, p.Second)) ? node : new SortNode(sort.Input, keys);
         }
         case AggregateNode aggregate:
         {
            List<ProjectItem> keys = aggregate.Keys.Select(k => Item(k, map)).ToList();
            List<AggregateItem> aggregates = aggregate.Aggregates
               .Select(a => a.Argument != null && map(a.Argument) is var e && !ReferenceEquals(e, a.Argument) ? a with { Argument = e } : a).ToList();
            bool same = keys.Zip(aggregate.Keys).All(p => ReferenceEquals(p.First, p.Second)) && aggregates.Zip(aggregate.Aggregates).All(p => ReferenceEquals(p.First, p.Second));
            return same ? node : new AggregateNode(aggregate.Input, keys, aggregates);
         }
         default:
            return node;
      }
   }

   private static ProjectItem Item(ProjectItem item, Func<PlanExpr, PlanExpr> map)
   {
      PlanExpr mapped = map(item.Expr);
      return ReferenceEquals(mapped, item.Expr) ? item : item with { Expr = mapped };
   }

   /// <summary>
   /// Rewrites an expression bottom up: <paramref name="replace"/> sees each node after its operands were rewritten
   /// and returns a replacement, or null to keep it. Subqueries' plans are left alone.
   /// </summary>
   public static PlanExpr Map(PlanExpr expr, Func<PlanExpr, PlanExpr?> replace)
   {
      PlanExpr rebuilt = expr switch
      {
         PlanUnary unary => Same(unary.Operand, Map(unary.Operand, replace), out PlanExpr operand) ? expr : new PlanUnary(unary.Op, operand, unary.Type),
         PlanBinary binary => Rebuild2(binary.Left, binary.Right, replace, out PlanExpr left, out PlanExpr right) ? expr : new PlanBinary(binary.Op, left, right, binary.Type),
         PlanIsNull isNull => Same(isNull.Operand, Map(isNull.Operand, replace), out PlanExpr operand) ? expr : new PlanIsNull(operand, isNull.Negated),
         PlanInList inList => MapList([inList.Operand, .. inList.Items], replace) is { } items ? new PlanInList(items[0], items.Skip(1).ToList(), inList.Negated, inList.Type) : expr,
         PlanConditional conditional => MapList([conditional.Condition, conditional.WhenTrue, conditional.WhenFalse], replace) is { } parts
            ? new PlanConditional(parts[0], parts[1], parts[2], conditional.Type) : expr,
         PlanFunction call => MapList(call.Arguments, replace) is { } arguments ? new PlanFunction(call.Function, arguments, call.Type, call.Span) : expr,
         PlanSubquery { Operand: { } operand } subquery => Same(operand, Map(operand, replace), out PlanExpr mapped)
            ? expr : new PlanSubquery(subquery.Kind, subquery.Plan, mapped, subquery.Negated, subquery.Type),
         _ => expr,
      };
      return replace(rebuilt) ?? rebuilt;
   }

   private static bool Same(PlanExpr before, PlanExpr after, out PlanExpr result)
   {
      result = after;
      return ReferenceEquals(before, after);
   }

   private static bool Rebuild2(PlanExpr a, PlanExpr b, Func<PlanExpr, PlanExpr?> replace, out PlanExpr left, out PlanExpr right)
   {
      left = Map(a, replace);
      right = Map(b, replace);
      return ReferenceEquals(a, left) && ReferenceEquals(b, right);
   }

   /// <summary>The mapped list, or null when every item is unchanged.</summary>
   private static List<PlanExpr>? MapList(IReadOnlyList<PlanExpr> items, Func<PlanExpr, PlanExpr?> replace)
   {
      List<PlanExpr> mapped = items.Select(i => Map(i, replace)).ToList();
      return mapped.Zip(items).All(p => ReferenceEquals(p.First, p.Second)) ? null : mapped;
   }

   /// <summary>The expression with references to the given columns replaced by the expressions that compute them.</summary>
   public static PlanExpr Substitute(PlanExpr expr, IReadOnlyDictionary<PlanColumn, PlanExpr> values) =>
      Map(expr, e => e is PlanColumnRef reference && values.TryGetValue(reference.Column, out PlanExpr? value) ? value : null);

   /// <summary>Every subquery plan in the node's expressions rewritten with <paramref name="rewrite"/>.</summary>
   public static PlanNode MapSubqueries(PlanNode node, Func<PlanNode, PlanNode> rewrite) =>
      MapExpressions(node, expr => Map(expr, e => e is PlanSubquery subquery && rewrite(subquery.Plan) is var plan && !ReferenceEquals(plan, subquery.Plan)
         ? new PlanSubquery(subquery.Kind, plan, subquery.Operand, subquery.Negated, subquery.Type)
         : null));

   /// <summary>Whether an expression is the same value everywhere (no columns, no subqueries).</summary>
   public static bool IsConstant(PlanExpr expr) => expr is PlanLiteral or PlanParameter ||
      (expr is not PlanSubquery && PlanAnalysis.Columns(expr).Count == 0 && !ContainsSubquery(expr));

   public static bool ContainsSubquery(PlanExpr expr)
   {
      bool found = false;
      Map(expr, e =>
      {
         found |= e is PlanSubquery;
         return null;
      });
      return found;
   }
}
