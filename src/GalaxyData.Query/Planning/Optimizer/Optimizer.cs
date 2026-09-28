using System;
using System.Collections.Generic;
using System.Linq;

namespace GalaxyData.Query.Planning.Optimizer;

/// <summary>A rewrite of one operator whose inputs have been rewritten already; null when it doesn't apply.</summary>
internal interface IRewriteRule
{
   string Name { get; }

   PlanNode? Apply(PlanNode node);
}

/// <summary>What the optimizer did, phase by phase: the rules that fired and the plan after each phase.</summary>
public sealed class OptimizerTrace
{
   private readonly List<(string Phase, List<string> Rules, string Plan)> phases = [];

   public IReadOnlyList<(string Phase, IReadOnlyList<string> Rules, string Plan)> Phases =>
      phases.Select(p => (p.Phase, (IReadOnlyList<string>)p.Rules, p.Plan)).ToList();

   internal void Add(string phase, List<string> rules, PlanNode plan) => phases.Add((phase, rules, PlanPrinter.Print(plan)));
}

/// <summary>
/// Rewrites a logical plan in phases, each run to a fixpoint: normalize (simplify, decorrelate subqueries into
/// joins), push filters down, then drop the columns and joins nothing uses. The result computes the same rows.
/// </summary>
public static class PlanOptimizer
{
   private const int MaxPasses = 32;

   private static readonly (string Name, IRewriteRule[] Rules)[] Phases =
   [
      ("normalize",
      [
         new SimplifyPredicates(), new MergeFilters(), new DecorrelateExists(), new DecorrelateIn(),
         new DecorrelateScalarAggregates(), new DecorrelateJoins(), new RemoveUselessSorts(),
      ]),
      ("pushdown",
      [
         new SimplifyPredicates(), new MergeFilters(), new OuterToInnerJoin(), new PushFilterIntoJoin(), new PushFilterThroughProject(),
         new PushFilterThroughAggregate(), new PushFilterThroughSetOp(), new PushFilterThroughSort(),
      ]),
   ];

   public static LogicalPlan Optimize(LogicalPlan plan, OptimizerTrace? trace = null)
   {
      ArgumentNullException.ThrowIfNull(plan);
      PlanNode root = plan.Root;
      foreach ((string name, IRewriteRule[] rules) in Phases)
      {
         List<string> fired = [];
         for (int pass = 0; pass < MaxPasses; pass++)
         {
            int before = fired.Count;
            root = Rewrite(root, rules, fired);
            if (fired.Count == before) { break; }
         }
         trace?.Add(name, fired, root);
      }
      root = ColumnPruner.Prune(root);
      trace?.Add("prune", [], root);
      return plan.WithRoot(root);
   }

   /// <summary>One bottom-up pass: inputs and subqueries first, then the first rule that applies to the node.</summary>
   private static PlanNode Rewrite(PlanNode node, IRewriteRule[] rules, List<string> fired)
   {
      PlanNode rebuilt = PlanRewriter.WithInputs(node, node.Inputs.Select(i => Rewrite(i, rules, fired)).ToList());
      rebuilt = PlanRewriter.MapSubqueries(rebuilt, plan => Rewrite(plan, rules, fired));
      foreach (IRewriteRule rule in rules)
      {
         PlanNode? result = rule.Apply(rebuilt);
         if (result != null && !ReferenceEquals(result, rebuilt))
         {
            fired.Add(rule.Name);
            return result;
         }
      }
      return rebuilt;
   }
}
