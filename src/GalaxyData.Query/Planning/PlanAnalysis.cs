using System.Collections.Generic;
using System.Linq;

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
