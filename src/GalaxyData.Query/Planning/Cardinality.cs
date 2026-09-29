using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Results;

namespace GalaxyData.Query.Planning;

/// <summary>A guess at how many rows a plan produces; <see cref="Known"/> when it rests on sizes the sources reported.</summary>
internal readonly record struct RowEstimate(long Rows, bool Known);

/// <summary>
/// Guesses at how many rows plans produce, for deciding how to run them. Tables are the size their source reported
/// (<see cref="DefaultTableRows"/> when it reported none); a filter keeps a share of its input by the kind of each
/// condition (one row for the key equal to a value); a join along a navigation keeps its left side's rows; a limit
/// caps its input.
/// </summary>
internal static class Cardinality
{
   public const long DefaultTableRows = 10_000;

   public static RowEstimate Estimate(PlanNode node)
   {
      switch (node)
      {
         case ScanNode scan:
            return scan.Entity.RowCountEstimate is { } reported ? new RowEstimate(reported, true) : new RowEstimate(DefaultTableRows, false);
         case OneRowNode:
            return new RowEstimate(1, true);
         case Federation.MergeTableNode table:
            return table.Estimate;
         case ProjectNode or SortNode:
            return Estimate(node.Inputs[0]);
         case FilterNode filter:
         {
            RowEstimate input = Estimate(filter.Input);
            if (PlanAnalysis.Conjuncts(filter.Predicate).Any(IsKeyLookup)) { return input with { Rows = Math.Min(input.Rows, 1) }; }
            return Scale(input, Selectivity(filter.Predicate));
         }
         case LimitNode limit:
         {
            RowEstimate input = Estimate(limit.Input);
            long offset = limit.Offset is PlanLiteral { Value: long skip } ? skip : 0;
            long rows = Math.Max(0, input.Rows - offset);
            return limit.Count is PlanLiteral { Value: long count } ? new RowEstimate(Math.Min(rows, count), input.Known || count <= rows) : input with { Rows = rows };
         }
         case DistinctNode distinct:
            return Scale(Estimate(distinct.Input), 0.5);
         case AggregateNode aggregate:
            return aggregate.Keys.Count == 0 ? new RowEstimate(1, true) : Scale(Estimate(aggregate.Input), 0.1);
         case JoinNode join:
            return Join(join);
         case SetOpNode set:
         {
            RowEstimate left = Estimate(set.Left);
            RowEstimate right = Estimate(set.Right);
            bool known = left.Known && right.Known;
            return set.Operation switch
            {
               SetOperation.UnionAll => new RowEstimate(left.Rows + right.Rows, known),
               SetOperation.Union => Scale(new RowEstimate(left.Rows + right.Rows, known), 0.9),
               SetOperation.Intersect => Scale(new RowEstimate(Math.Min(left.Rows, right.Rows), known), 0.5),
               _ => Scale(left with { Known = known }, 0.5),
            };
         }
         default:
            return new RowEstimate(DefaultTableRows, false);
      }
   }

   private static RowEstimate Join(JoinNode join)
   {
      RowEstimate left = Estimate(join.Left);
      RowEstimate right = Estimate(join.Right);
      bool known = left.Known && right.Known;
      if (PlanAnalysis.KeepsLeftRows(join)) { return left; }
      return join.Kind switch
      {
         JoinKind.Semi or JoinKind.Anti => Scale(left, 0.5),
         JoinKind.Left when join.Navigation != null => left,
         _ when join.Condition == null => new RowEstimate(Saturate(left.Rows, right.Rows), known),
         // An equality is most likely a foreign key: a row of the larger side each, at most.
         _ when PlanAnalysis.Conjuncts(join.Condition).Any(c => c is PlanBinary { Op: BinaryOp.Equal }) =>
            new RowEstimate(join.Kind == JoinKind.Left ? Math.Max(left.Rows, right.Rows) : Math.Max(Math.Min(left.Rows, right.Rows), 1), known),
         _ => Scale(new RowEstimate(Saturate(left.Rows, right.Rows), known), 0.33),
      };
   }

   /// <summary>A condition that the key of the rows equals a value picks at most one of them.</summary>
   private static bool IsKeyLookup(PlanExpr condition)
   {
      if (condition is not PlanBinary { Op: BinaryOp.Equal } equal) { return false; }
      PlanColumnRef? column = (equal.Left, equal.Right) switch
      {
         (PlanColumnRef c, PlanLiteral or PlanParameter) => c,
         (PlanLiteral or PlanParameter, PlanColumnRef c) => c,
         _ => null,
      };
      if (column?.Column.Lineage is not { Kind: LineageKind.Direct, Sources: [{ } source] }) { return false; }
      EntityDef owner = source.Column.Owner;
      return owner.Key is { Columns: [{ } only] } && ReferenceEquals(only, source.Column);
   }

   /// <summary>The share of rows a condition keeps.</summary>
   private static double Selectivity(PlanExpr condition) => condition switch
   {
      PlanBinary { Op: BinaryOp.And } and => Selectivity(and.Left) * Selectivity(and.Right),
      PlanBinary { Op: BinaryOp.Or } or => Math.Min(1, Selectivity(or.Left) + Selectivity(or.Right) - Selectivity(or.Left) * Selectivity(or.Right)),
      PlanUnary { Op: UnaryOp.Not } not => 1 - Selectivity(not.Operand),
      PlanBinary { Op: BinaryOp.Equal } => 0.1,
      PlanBinary { Op: BinaryOp.NotEqual } => 0.9,
      PlanBinary { Op: BinaryOp.Less or BinaryOp.LessOrEqual or BinaryOp.Greater or BinaryOp.GreaterOrEqual } => 0.33,
      PlanIsNull isNull => isNull.Negated ? 0.9 : 0.1,
      PlanInList inList => inList.Negated ? 0.9 : Math.Min(0.5, 0.1 * inList.Items.Count),
      PlanFunction { Function.Id: FunctionId.Like or FunctionId.ILike or FunctionId.StartsWith or FunctionId.EndsWith or FunctionId.Contains or FunctionId.IContains } => 0.25,
      PlanLiteral { Value: true } => 1,
      PlanLiteral { Value: false or null } => 0,
      _ => 0.5,
   };

   private static RowEstimate Scale(RowEstimate estimate, double share) =>
      estimate with { Rows = estimate.Rows == 0 ? 0 : Math.Max(1, (long)Math.Round(estimate.Rows * share)) };

   private static long Saturate(long a, long b) => a > 0 && b > long.MaxValue / a ? long.MaxValue : a * b;
}
