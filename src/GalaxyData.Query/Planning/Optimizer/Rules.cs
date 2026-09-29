using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Planning.Optimizer;

#region Normalize

/// <summary><c>x and true</c> is x, <c>x or true</c> is true, <c>not not x</c> is x; a filter that is always true goes.</summary>
internal sealed class SimplifyPredicates : IRewriteRule
{
   public string Name => "simplify predicates";

   public PlanNode? Apply(PlanNode node)
   {
      if (node is not FilterNode filter) { return null; }
      PlanExpr predicate = Simplify(filter.Predicate);
      if (predicate is PlanLiteral { Value: true }) { return filter.Input; }
      return ReferenceEquals(predicate, filter.Predicate) ? null : new FilterNode(filter.Input, predicate);
   }

   public static PlanExpr Simplify(PlanExpr expr) => PlanRewriter.Map(expr, e => e switch
   {
      PlanBinary { Op: BinaryOp.And, Left: PlanLiteral { Value: true } } and => and.Right,
      PlanBinary { Op: BinaryOp.And, Right: PlanLiteral { Value: true } } and => and.Left,
      PlanBinary { Op: BinaryOp.And, Left: PlanLiteral { Value: false } } => False,
      PlanBinary { Op: BinaryOp.And, Right: PlanLiteral { Value: false } } => False,
      PlanBinary { Op: BinaryOp.Or, Left: PlanLiteral { Value: false } } or => or.Right,
      PlanBinary { Op: BinaryOp.Or, Right: PlanLiteral { Value: false } } or => or.Left,
      PlanBinary { Op: BinaryOp.Or, Left: PlanLiteral { Value: true } } => True,
      PlanBinary { Op: BinaryOp.Or, Right: PlanLiteral { Value: true } } => True,
      PlanUnary { Op: UnaryOp.Not, Operand: PlanLiteral { Value: bool value } } => value ? False : True,
      PlanUnary { Op: UnaryOp.Not, Operand: PlanUnary { Op: UnaryOp.Not } inner } => inner.Operand,
      _ => null,
   });

   private static PlanLiteral True => new(true, ScalarType.Boolean.AsNonNullable());

   private static PlanLiteral False => new(false, ScalarType.Boolean.AsNonNullable());
}

/// <summary>Two filters in a row are one, with both conditions.</summary>
internal sealed class MergeFilters : IRewriteRule
{
   public string Name => "merge filters";

   public PlanNode? Apply(PlanNode node) =>
      node is FilterNode { Input: FilterNode inner } outer
         ? new FilterNode(inner.Input, PlanAnalysis.Conjunction([inner.Predicate, outer.Predicate])!)
         : null;
}

/// <summary>
/// Separating a correlated subquery from the rows it depends on: the conditions that refer to outer columns are
/// taken out of the filters (through projections, and past sorts, which don't matter to the caller), leaving a
/// plan that no longer depends on the outer row.
/// </summary>
internal static class Correlation
{
   public static bool TryExtract(PlanNode plan, HashSet<PlanColumn> outer, bool rowsOnly, out PlanNode core, out List<PlanExpr> correlated)
   {
      correlated = [];
      core = plan;
      if (!Extract(plan, outer, rowsOnly, correlated, out PlanNode? rest)) { return false; }
      core = rest!;
      return !PlanAnalysis.FreeColumns(core).Overlaps(outer);
   }

   private static bool Extract(PlanNode node, HashSet<PlanColumn> outer, bool rowsOnly, List<PlanExpr> correlated, out PlanNode? core)
   {
      core = null;
      switch (node)
      {
         case FilterNode filter:
         {
            List<PlanExpr> kept = [];
            foreach (PlanExpr conjunct in PlanAnalysis.Conjuncts(filter.Predicate))
            {
               if (PlanAnalysis.Columns(conjunct).Overlaps(outer))
               {
                  if (PlanRewriter.ContainsSubquery(conjunct)) { return false; }
                  correlated.Add(conjunct);
               }
               else
               {
                  kept.Add(conjunct);
               }
            }
            if (!Extract(filter.Input, outer, rowsOnly, correlated, out PlanNode? below)) { return false; }
            core = kept.Count == 0 ? below : new FilterNode(below!, PlanAnalysis.Conjunction(kept)!);
            return true;
         }
         case ProjectNode project:
         {
            if (project.Items.Any(i => PlanAnalysis.Columns(i.Expr).Overlaps(outer))) { return false; }
            int before = correlated.Count;
            if (!Extract(project.Input, outer, rowsOnly, correlated, out PlanNode? below)) { return false; }
            // The conditions taken out below need their columns above, so they pass through.
            HashSet<PlanColumn> output = [.. project.Output];
            List<ProjectItem> items = [.. project.Items];
            foreach (PlanExpr condition in correlated.Skip(before))
            {
               foreach (PlanColumn column in PlanAnalysis.Columns(condition))
               {
                  if (!outer.Contains(column) && output.Add(column)) { items.Add(new ProjectItem(column, new PlanColumnRef(column))); }
               }
            }
            core = new ProjectNode(below!, items);
            return true;
         }
         case SortNode sort:
            return Extract(sort.Input, outer, rowsOnly, correlated, out core);
         case DistinctNode distinct when rowsOnly:
            return Extract(distinct.Input, outer, rowsOnly, correlated, out core);
         default:
            core = node;
            return !PlanAnalysis.FreeColumns(node).Overlaps(outer);
      }
   }
}

/// <summary><c>where(exists(...))</c> is a semi join, <c>where(not exists(...))</c> an anti join, on the correlated conditions.</summary>
internal sealed class DecorrelateExists : IRewriteRule
{
   public string Name => "exists to semi join";

   public PlanNode? Apply(PlanNode node)
   {
      if (node is not FilterNode filter) { return null; }
      List<PlanExpr> conjuncts = PlanAnalysis.Conjuncts(filter.Predicate);
      HashSet<PlanColumn> outer = [.. filter.Input.Output];
      for (int i = 0; i < conjuncts.Count; i++)
      {
         (PlanSubquery? subquery, bool negated) = conjuncts[i] switch
         {
            PlanSubquery { Kind: SubqueryKind.Exists } exists => (exists, exists.Negated),
            PlanUnary { Op: UnaryOp.Not, Operand: PlanSubquery { Kind: SubqueryKind.Exists } exists } => (exists, !exists.Negated),
            _ => (null, false),
         };
         if (subquery == null || !PlanAnalysis.FreeColumns(subquery.Plan).IsSubsetOf(outer)) { continue; }
         if (!Correlation.TryExtract(subquery.Plan, outer, rowsOnly: true, out PlanNode core, out List<PlanExpr> correlated)) { continue; }
         JoinNode join = new(negated ? JoinKind.Anti : JoinKind.Semi, filter.Input, core, PlanAnalysis.Conjunction(correlated), null);
         List<PlanExpr> rest = [.. conjuncts.Take(i), .. conjuncts.Skip(i + 1)];
         return rest.Count == 0 ? join : new FilterNode(join, PlanAnalysis.Conjunction(rest)!);
      }
      return null;
   }
}

/// <summary><c>where(x in (subquery))</c> is a semi join on <c>x == value</c> and the correlated conditions.</summary>
internal sealed class DecorrelateIn : IRewriteRule
{
   public string Name => "in to semi join";

   public PlanNode? Apply(PlanNode node)
   {
      if (node is not FilterNode filter) { return null; }
      List<PlanExpr> conjuncts = PlanAnalysis.Conjuncts(filter.Predicate);
      HashSet<PlanColumn> outer = [.. filter.Input.Output];
      for (int i = 0; i < conjuncts.Count; i++)
      {
         if (conjuncts[i] is not PlanSubquery { Kind: SubqueryKind.In, Negated: false, Operand: { } operand } subquery) { continue; }
         if (!PlanAnalysis.FreeColumns(subquery.Plan).IsSubsetOf(outer) || !PlanAnalysis.Columns(operand).IsSubsetOf(outer)) { continue; }
         PlanColumn value = subquery.Plan.Output[0];
         if (!Correlation.TryExtract(subquery.Plan, outer, rowsOnly: true, out PlanNode core, out List<PlanExpr> correlated)) { continue; }
         PlanExpr equal = new PlanBinary(BinaryOp.Equal, operand, new PlanColumnRef(value), ScalarType.Boolean.WithNullable(operand.Type.Nullable || value.Type.Nullable));
         JoinNode join = new(JoinKind.Semi, filter.Input, core, PlanAnalysis.Conjunction([equal, .. correlated]), null);
         List<PlanExpr> rest = [.. conjuncts.Take(i), .. conjuncts.Skip(i + 1)];
         return rest.Count == 0 ? join : new FilterNode(join, PlanAnalysis.Conjunction(rest)!);
      }
      return null;
   }
}

/// <summary>
/// A correlated aggregate (<c>customer.orders.sum(total)</c>) is a left join to the aggregate grouped by the
/// correlated columns (a group join); counts and sums of no rows are 0. Aggregates over the same table and
/// correlation share one group join.
/// </summary>
internal sealed class DecorrelateScalarAggregates : IRewriteRule
{
   public string Name => "aggregate to group join";

   public PlanNode? Apply(PlanNode node)
   {
      PlanNode input;
      switch (node)
      {
         case ProjectNode project:
            input = project.Input;
            break;
         case FilterNode filter:
            input = filter.Input;
            break;
         default:
            return null;
      }
      HashSet<PlanColumn> outer = [.. input.Output];
      PlanSubquery? target = null;
      GroupJoin? plan = null;
      foreach (PlanExpr expr in PlanAnalysis.Expressions(node))
      {
         PlanRewriter.Map(expr, e =>
         {
            if (target == null && e is PlanSubquery { Kind: SubqueryKind.Scalar } subquery && Plan(subquery, input, outer) is { } found)
            {
               target = subquery;
               plan = found;
            }
            return null;
         });
         if (target != null) { break; }
      }
      if (target == null || plan == null) { return null; }

      PlanExpr value = plan.Value;
      PlanNode rewritten = PlanRewriter.MapExpressions(node, e => PlanRewriter.Map(e, x => ReferenceEquals(x, target) ? value : null));
      rewritten = PlanRewriter.WithInputs(rewritten, [plan.Join]);
      // A filter keeps its output; the group join's columns stay below.
      return node is FilterNode ? new ProjectNode(rewritten, node.Output.Select(c => new ProjectItem(c, new PlanColumnRef(c)))) : rewritten;
   }

   private sealed record GroupJoin(JoinNode Join, PlanExpr Value);

   private static GroupJoin? Plan(PlanSubquery subquery, PlanNode input, HashSet<PlanColumn> outer)
   {
      if (subquery.Plan is not AggregateNode { Keys.Count: 0, Aggregates: [AggregateItem aggregate] } node) { return null; }
      HashSet<PlanColumn> free = PlanAnalysis.FreeColumns(node);
      if (free.Count == 0 || !free.IsSubsetOf(outer)) { return null; }
      // An argument that reads the outer row can't be aggregated before the join.
      if (aggregate.Argument != null && PlanAnalysis.Columns(aggregate.Argument).Overlaps(outer)) { return null; }
      if (!Correlation.TryExtract(node.Input, outer, rowsOnly: false, out PlanNode core, out List<PlanExpr> correlated)) { return null; }
      HashSet<PlanColumn> coreOutput = [.. core.Output];
      List<(PlanColumnRef Outer, PlanColumnRef Inner)> pairs = [];
      foreach (PlanExpr condition in correlated)
      {
         if (condition is not PlanBinary { Op: BinaryOp.Equal } equal) { return null; }
         (PlanExpr a, PlanExpr b) = (equal.Left, equal.Right);
         if (a is PlanColumnRef x && b is PlanColumnRef y)
         {
            if (outer.Contains(x.Column) && coreOutput.Contains(y.Column)) { pairs.Add((x, y)); continue; }
            if (outer.Contains(y.Column) && coreOutput.Contains(x.Column)) { pairs.Add((y, x)); continue; }
         }
         return null;
      }

      PlanExpr Result(PlanColumn column)
      {
         PlanColumnRef reference = new(column, column.Type.AsNullable());
         if (aggregate.Function is not (AggregateFunction.Count or AggregateFunction.CountRows or AggregateFunction.CountDistinct or AggregateFunction.Sum)) { return reference; }
         FunctionRegistry.Default.TryGet("coalesce", out FunctionDef coalesce);
         return new PlanFunction(coalesce, [reference, new PlanLiteral(0L, ScalarType.Int64.AsNonNullable())], subquery.Type.AsNonNullable(), null);
      }

      // Another aggregate over the same table and correlation: add this one to its group join.
      if (input is JoinNode { Kind: JoinKind.Left, Navigation: null, Right: AggregateNode { Input: ScanNode existingScan } group } join &&
          core is ScanNode scan && ReferenceEquals(existingScan.Entity, scan.Entity) && SameCorrelation(join, group, pairs, existingScan, scan))
      {
         Dictionary<PlanColumn, PlanExpr> remap = scan.Columns.ToDictionary(c => c.Output, c => (PlanExpr)new PlanColumnRef(existingScan.Columns.First(e => ReferenceEquals(e.Column, c.Column)).Output));
         AggregateItem moved = aggregate.Argument == null ? aggregate : aggregate with { Argument = PlanRewriter.Substitute(aggregate.Argument, remap) };
         AggregateNode merged = new(group.Input, group.Keys, [.. group.Aggregates, moved]);
         return new GroupJoin(new JoinNode(JoinKind.Left, join.Left, merged, join.Condition, null), Result(moved.Column));
      }

      List<PlanColumn> keys = pairs.Select(p => p.Inner.Column).Distinct().ToList();
      AggregateNode grouped = new(core, keys.Select(k => new ProjectItem(k, new PlanColumnRef(k))), [aggregate]);
      PlanExpr joinCondition = PlanAnalysis.Conjunction(pairs.Select(p =>
         (PlanExpr)new PlanBinary(BinaryOp.Equal, p.Outer, new PlanColumnRef(p.Inner.Column), ScalarType.Boolean.WithNullable(p.Outer.Type.Nullable || p.Inner.Type.Nullable))))!;
      return new GroupJoin(new JoinNode(JoinKind.Left, input, grouped, joinCondition, null), Result(aggregate.Column));
   }

   /// <summary>Whether a group join already correlates on the same outer columns and the same table columns.</summary>
   private static bool SameCorrelation(JoinNode join, AggregateNode group, List<(PlanColumnRef Outer, PlanColumnRef Inner)> pairs, ScanNode existing, ScanNode scan)
   {
      if (join.Condition == null || group.Keys.Count != pairs.Select(p => p.Inner.Column).Distinct().Count()) { return false; }
      List<PlanExpr> existingPairs = PlanAnalysis.Conjuncts(join.Condition);
      if (existingPairs.Count != pairs.Count) { return false; }
      foreach ((PlanColumnRef outer, PlanColumnRef inner) in pairs)
      {
         Catalog.ColumnDef? column = scan.Columns.FirstOrDefault(c => ReferenceEquals(c.Output, inner.Column))?.Column;
         PlanColumn? existingColumn = existing.Columns.FirstOrDefault(c => ReferenceEquals(c.Column, column))?.Output;
         bool found = existingPairs.Any(p => p is PlanBinary { Op: BinaryOp.Equal, Left: PlanColumnRef l, Right: PlanColumnRef r } &&
            ReferenceEquals(l.Column, outer.Column) && ReferenceEquals(r.Column, existingColumn));
         if (!found) { return false; }
      }
      return true;
   }
}

/// <summary>A join whose right side depends on the left row (a flattened collection) joins on those conditions instead.</summary>
internal sealed class DecorrelateJoins : IRewriteRule
{
   public string Name => "correlated join to join";

   public PlanNode? Apply(PlanNode node)
   {
      if (node is not JoinNode { Kind: JoinKind.Inner or JoinKind.Left } join) { return null; }
      HashSet<PlanColumn> outer = [.. join.Left.Output];
      if (!PlanAnalysis.FreeColumns(join.Right).Overlaps(outer)) { return null; }
      if (!Correlation.TryExtract(join.Right, outer, rowsOnly: false, out PlanNode core, out List<PlanExpr> correlated)) { return null; }
      PlanExpr? condition = PlanAnalysis.Conjunction(join.Condition == null ? correlated : [join.Condition, .. correlated]);
      return new JoinNode(join.Kind, join.Left, core, condition, null);
   }
}

/// <summary>A sort whose order nothing keeps goes: below an aggregate, DISTINCT, a set operation, a semi join's right side, or another sort.</summary>
internal sealed class RemoveUselessSorts : IRewriteRule
{
   public string Name => "remove useless sort";

   public PlanNode? Apply(PlanNode node) => node switch
   {
      AggregateNode aggregate when Strip(aggregate.Input) is { } input => PlanRewriter.WithInputs(aggregate, [input]),
      DistinctNode distinct when Strip(distinct.Input) is { } input => new DistinctNode(input),
      SortNode sort when Strip(sort.Input) is { } input => new SortNode(input, sort.Keys),
      SetOpNode set when Strip(set.Left) is { } || Strip(set.Right) is { } =>
         new SetOpNode(set.Operation, Strip(set.Left) ?? set.Left, Strip(set.Right) ?? set.Right, set.Output),
      JoinNode { Kind: JoinKind.Semi or JoinKind.Anti } join when Strip(join.Right) is { } right =>
         new JoinNode(join.Kind, join.Left, right, join.Condition, join.Navigation),
      _ => null,
   };

   /// <summary>The plan without a sort at its top (under filters, projections and joins' left sides); null when there is none.</summary>
   private static PlanNode? Strip(PlanNode node) => node switch
   {
      SortNode sort => sort.Input,
      FilterNode filter when Strip(filter.Input) is { } input => new FilterNode(input, filter.Predicate),
      ProjectNode project when Strip(project.Input) is { } input => new ProjectNode(input, project.Items),
      JoinNode { Kind: JoinKind.Inner or JoinKind.Left } join when Strip(join.Left) is { } left =>
         new JoinNode(join.Kind, left, join.Right, join.Condition, join.Navigation),
      _ => null,
   };
}

#endregion

#region Pushdown

/// <summary>
/// A left join whose right side a filter above requires (a condition that can't hold when the right row is
/// missing, such as <c>inner.city == 'x'</c>) is an inner join.
/// </summary>
internal sealed class OuterToInnerJoin : IRewriteRule
{
   public string Name => "outer to inner join";

   public PlanNode? Apply(PlanNode node)
   {
      if (node is not FilterNode { Input: JoinNode { Kind: JoinKind.Left } join } filter) { return null; }
      HashSet<PlanColumn> right = [.. join.Right.Output];
      if (!PlanAnalysis.Conjuncts(filter.Predicate).Any(c => RejectsNull(c, right))) { return null; }
      return new FilterNode(new JoinNode(JoinKind.Inner, join.Left, join.Right, join.Condition, join.Navigation), filter.Predicate);
   }

   /// <summary>Whether the condition is null or false whenever the right side's columns are all null.</summary>
   private static bool RejectsNull(PlanExpr condition, HashSet<PlanColumn> right) => condition switch
   {
      PlanBinary { Op: BinaryOp.And } and => RejectsNull(and.Left, right) || RejectsNull(and.Right, right),
      PlanBinary { Op: BinaryOp.Or } or => RejectsNull(or.Left, right) && RejectsNull(or.Right, right),
      PlanBinary binary when OperatorText.IsComparison(binary.Op) => Strict(binary.Left, right) || Strict(binary.Right, right),
      PlanIsNull { Negated: true } isNull => Strict(isNull.Operand, right),
      PlanInList inList => Strict(inList.Operand, right),
      PlanFunction { Function.Id: FunctionId.StartsWith or FunctionId.EndsWith or FunctionId.Contains or FunctionId.IContains or FunctionId.Like or FunctionId.ILike or FunctionId.Between } call
         => Strict(call.Arguments[0], right),
      _ => false,
   };

   /// <summary>Whether the value is null when the right side's columns are.</summary>
   private static bool Strict(PlanExpr expr, HashSet<PlanColumn> right) => expr switch
   {
      PlanColumnRef reference => right.Contains(reference.Column),
      PlanUnary { Op: UnaryOp.Negate or UnaryOp.Plus } unary => Strict(unary.Operand, right),
      PlanBinary binary when !OperatorText.IsComparison(binary.Op) && binary.Op is not (BinaryOp.And or BinaryOp.Or) =>
         Strict(binary.Left, right) || Strict(binary.Right, right),
      // between(x, null, 10) is false, not null, when x > 10; like the others below it can give a value for null input.
      PlanFunction call when call.Function.Id is not (FunctionId.Coalesce or FunctionId.Iif or FunctionId.Concat or FunctionId.NullIf or FunctionId.ToText or FunctionId.Between) =>
         call.Arguments.Any(a => Strict(a, right)),
      _ => false,
   };
}

/// <summary>
/// A filter above a join goes to the side it is about; conditions on both sides of an inner join join it. A
/// constant comparison with a joined column also filters the other side: <c>o.customer_id == 7</c> gives <c>c.id == 7</c>.
/// </summary>
internal sealed class PushFilterIntoJoin : IRewriteRule
{
   public string Name => "push filter into join";

   public PlanNode? Apply(PlanNode node)
   {
      if (node is not FilterNode { Input: JoinNode join } filter) { return null; }
      HashSet<PlanColumn> left = [.. join.Left.Output];
      HashSet<PlanColumn> right = [.. join.Right.Output];
      List<PlanExpr> toLeft = [], toRight = [], toCondition = [], kept = [];
      foreach (PlanExpr conjunct in PlanAnalysis.Conjuncts(filter.Predicate))
      {
         // A subquery's columns from outside it count as the conjunct's: one that reads only a side's goes to that side.
         HashSet<PlanColumn> columns = PlanAnalysis.Columns(conjunct);
         bool subquery = PlanRewriter.ContainsSubquery(conjunct);
         if (columns.Count == 0) { kept.Add(conjunct); }
         else if (columns.IsSubsetOf(left)) { toLeft.Add(conjunct); }
         else if (join.Kind == JoinKind.Inner && columns.IsSubsetOf(right)) { toRight.Add(conjunct); }
         else if (join.Kind == JoinKind.Inner && !subquery && columns.IsSubsetOf(left.Union(right))) { toCondition.Add(conjunct); }
         else { kept.Add(conjunct); }
      }
      if (toLeft.Count + toRight.Count + toCondition.Count == 0) { return null; }
      if (join.Kind is JoinKind.Inner or JoinKind.Left && join.Condition != null) { toRight.AddRange(Transitive(toLeft, join.Condition, left, right)); }

      PlanNode newLeft = toLeft.Count == 0 ? join.Left : new FilterNode(join.Left, PlanAnalysis.Conjunction(toLeft)!);
      PlanNode newRight = toRight.Count == 0 ? join.Right : new FilterNode(join.Right, PlanAnalysis.Conjunction(toRight)!);
      PlanExpr? condition = toCondition.Count == 0 ? join.Condition : PlanAnalysis.Conjunction(join.Condition == null ? toCondition : [join.Condition, .. toCondition]);
      // A navigation join that now filters its target or joins on more is not a plain lookup any more.
      Catalog.NavigationDef? navigation = toRight.Count == 0 && toCondition.Count == 0 ? join.Navigation : null;
      JoinNode rewritten = new(join.Kind, newLeft, newRight, condition, navigation);
      return kept.Count == 0 ? rewritten : new FilterNode(rewritten, PlanAnalysis.Conjunction(kept)!);
   }

   private static IEnumerable<PlanExpr> Transitive(List<PlanExpr> leftFilters, PlanExpr condition, HashSet<PlanColumn> left, HashSet<PlanColumn> right)
   {
      List<(PlanColumn Left, PlanColumn Right)> equal = [];
      foreach (PlanExpr part in PlanAnalysis.Conjuncts(condition))
      {
         if (part is PlanBinary { Op: BinaryOp.Equal, Left: PlanColumnRef a, Right: PlanColumnRef b })
         {
            if (left.Contains(a.Column) && right.Contains(b.Column)) { equal.Add((a.Column, b.Column)); }
            else if (left.Contains(b.Column) && right.Contains(a.Column)) { equal.Add((b.Column, a.Column)); }
         }
      }
      foreach (PlanExpr filter in leftFilters)
      {
         if (filter is not PlanBinary { Op: BinaryOp.Equal } comparison) { continue; }
         (PlanColumnRef? column, PlanExpr? constant) = comparison switch
         {
            { Left: PlanColumnRef c, Right: var k } when PlanRewriter.IsConstant(k) => (c, k),
            { Right: PlanColumnRef c, Left: var k } when PlanRewriter.IsConstant(k) => (c, k),
            _ => (null, null),
         };
         if (column == null) { continue; }
         foreach ((PlanColumn l, PlanColumn r) in equal.Where(e => ReferenceEquals(e.Left, column.Column)))
         {
            yield return new PlanBinary(BinaryOp.Equal, new PlanColumnRef(r), constant!, comparison.Type);
         }
      }
   }
}

/// <summary>A filter moves below a projection, with the projected expressions in place of the columns.</summary>
internal sealed class PushFilterThroughProject : IRewriteRule
{
   public string Name => "push filter through projection";

   public PlanNode? Apply(PlanNode node)
   {
      if (node is not FilterNode { Input: ProjectNode project } filter || PlanRewriter.ContainsSubquery(filter.Predicate)) { return null; }
      HashSet<PlanColumn> used = PlanAnalysis.Columns(filter.Predicate);
      Dictionary<PlanColumn, PlanExpr> values = [];
      foreach (ProjectItem item in project.Items)
      {
         if (!used.Contains(item.Column)) { continue; }
         if (PlanRewriter.ContainsSubquery(item.Expr)) { return null; }
         values[item.Column] = item.Expr;
      }
      return new ProjectNode(new FilterNode(project.Input, PlanRewriter.Substitute(filter.Predicate, values)), project.Items);
   }
}

/// <summary>Conditions on group keys filter the rows before grouping instead of the groups after.</summary>
internal sealed class PushFilterThroughAggregate : IRewriteRule
{
   public string Name => "push filter through aggregate";

   public PlanNode? Apply(PlanNode node)
   {
      if (node is not FilterNode { Input: AggregateNode aggregate } filter || aggregate.Keys.Count == 0) { return null; }
      Dictionary<PlanColumn, PlanExpr> keys = aggregate.Keys.ToDictionary(k => k.Column, k => k.Expr);
      List<PlanExpr> below = [], kept = [];
      foreach (PlanExpr conjunct in PlanAnalysis.Conjuncts(filter.Predicate))
      {
         HashSet<PlanColumn> columns = PlanAnalysis.Columns(conjunct);
         if (columns.Count > 0 && !PlanRewriter.ContainsSubquery(conjunct) && columns.All(keys.ContainsKey)) { below.Add(PlanRewriter.Substitute(conjunct, keys)); }
         else { kept.Add(conjunct); }
      }
      if (below.Count == 0) { return null; }
      PlanNode grouped = new AggregateNode(new FilterNode(aggregate.Input, PlanAnalysis.Conjunction(below)!), aggregate.Keys, aggregate.Aggregates, aggregate.DependentKeys);
      return kept.Count == 0 ? grouped : new FilterNode(grouped, PlanAnalysis.Conjunction(kept)!);
   }
}

/// <summary>A filter over a set operation filters both sides.</summary>
internal sealed class PushFilterThroughSetOp : IRewriteRule
{
   public string Name => "push filter through set operation";

   public PlanNode? Apply(PlanNode node)
   {
      if (node is not FilterNode { Input: SetOpNode set } filter || PlanRewriter.ContainsSubquery(filter.Predicate)) { return null; }
      PlanExpr Side(PlanNode side)
      {
         Dictionary<PlanColumn, PlanExpr> map = [];
         for (int i = 0; i < set.Output.Count; i++) { map[set.Output[i]] = new PlanColumnRef(side.Output[i]); }
         return PlanRewriter.Substitute(filter.Predicate, map);
      }
      return new SetOpNode(set.Operation, new FilterNode(set.Left, Side(set.Left)), new FilterNode(set.Right, Side(set.Right)), set.Output);
   }
}

/// <summary>
/// A limit goes below a projection, with the sort that picks its rows (its keys become the projection's expressions
/// for them): the projection computes only the rows kept, and the limit meets the joins below.
/// </summary>
internal sealed class PushLimitThroughProject : IRewriteRule
{
   public string Name => "push limit through project";

   public PlanNode? Apply(PlanNode node)
   {
      switch (node)
      {
         case LimitNode { Input: ProjectNode project } limit:
            return new ProjectNode(new LimitNode(project.Input, limit.Count, limit.Offset), project.Items);
         case LimitNode { Input: SortNode { Input: ProjectNode project } sort } limit:
         {
            Dictionary<PlanColumn, PlanExpr> values = project.Items.ToDictionary(i => i.Column, i => i.Expr);
            List<PlanSortKey> keys = [];
            foreach (PlanSortKey key in sort.Keys)
            {
               PlanExpr below = PlanRewriter.Substitute(key.Expr, values);
               // A key that is a subquery's value would compute it twice.
               if (PlanRewriter.ContainsSubquery(below)) { return null; }
               keys.Add(key with { Expr = below });
            }
            return new ProjectNode(new LimitNode(new SortNode(project.Input, keys), limit.Count, limit.Offset), project.Items);
         }
         default:
            return null;
      }
   }
}

/// <summary>
/// A limit goes below the joins that give each left row once (a navigation, or a grouped right side joined on its
/// keys), with the sort that picks its rows when the keys are the left side's: only the rows kept are joined, and
/// across sources only their keys are looked up. The left side's order is the joins'. The row count stays capped
/// above the joins too, in case a right side has a key twice after all (a virtual entity's declared key, or group
/// keys a source tells apart that the merge engine doesn't).
/// </summary>
internal sealed class PushLimitThroughJoin : IRewriteRule
{
   public string Name => "push limit through join";

   public PlanNode? Apply(PlanNode node) => node switch
   {
      LimitNode { Input: JoinNode join } limit => Push(limit, null, join),
      LimitNode { Input: SortNode { Input: JoinNode join } sort } limit => Push(limit, sort, join),
      _ => null,
   };

   /// <summary>
   /// The limit (and sort) below the chain of such joins on the left, as far down as the sort's keys go, with one
   /// cap above the chain; null when there's nothing to do or it was done (the bottom is a limit already).
   /// </summary>
   private static PlanNode? Push(LimitNode limit, SortNode? sort, JoinNode top)
   {
      List<JoinNode> chain = [];
      PlanNode bottom = top;
      while (bottom is JoinNode join && PlanAnalysis.KeepsLeftRows(join) && (sort == null || OnLeft(sort, join)))
      {
         chain.Add(join);
         bottom = join.Left;
      }
      if (chain.Count == 0 || bottom is LimitNode or SortNode { Input: LimitNode }) { return null; }
      PlanNode rebuilt = new LimitNode(sort == null ? bottom : new SortNode(bottom, sort.Keys), limit.Count, limit.Offset);
      for (int i = chain.Count - 1; i >= 0; i--)
      {
         JoinNode join = chain[i];
         rebuilt = new JoinNode(join.Kind, rebuilt, join.Right, join.Condition, join.Navigation);
      }
      // The row count stays capped above the joins, in case a right side has a key twice after all.
      return limit.Count == null ? rebuilt : new LimitNode(rebuilt, limit.Count, null);
   }

   private static bool OnLeft(SortNode sort, JoinNode join)
   {
      HashSet<PlanColumn> left = [.. join.Left.Output];
      return sort.Keys.All(k => !PlanRewriter.ContainsSubquery(k.Expr) && PlanAnalysis.Columns(k.Expr).All(left.Contains));
   }
}

/// <summary>Filtering commutes with sorting; filtering first sorts fewer rows and meets the other pushdowns.</summary>
internal sealed class PushFilterThroughSort : IRewriteRule
{
   public string Name => "push filter through sort";

   public PlanNode? Apply(PlanNode node) =>
      node is FilterNode { Input: SortNode sort } filter ? new SortNode(new FilterNode(sort.Input, filter.Predicate), sort.Keys) : null;
}

#endregion
