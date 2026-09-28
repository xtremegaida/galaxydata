using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GalaxyData.Query.Binding;

namespace GalaxyData.Query.Planning;

/// <summary>
/// Renders logical plans for tests and explain: one line per operator with its inputs indented below. Columns
/// print as <c>name#id</c>, so a reference can be traced to the operator that produces it. A subquery prints as
/// <c>exists[1]</c> in its expression, with its plan below the operator as <c>[1]</c>.
/// </summary>
public static class PlanPrinter
{
   public static string Print(PlanNode node) => Print(node, null);

   /// <summary>A plan with where each operator runs after it (<c>@shop</c>), as <paramref name="site"/> says.</summary>
   internal static string Print(PlanNode node, Func<PlanNode, string?>? site)
   {
      ArgumentNullException.ThrowIfNull(node);
      StringBuilder text = new();
      new Writer(text, site).Node(node, 0);
      return text.ToString();
   }

   /// <summary>An expression on one line; subqueries print as <c>exists[…]</c>.</summary>
   public static string Expr(PlanExpr expr) => new Writer(null, null).Expr(expr);

   private sealed class Writer(StringBuilder? text, Func<PlanNode, string?>? site)
   {
      private readonly List<PlanSubquery> pending = [];
      private int numbered;

      public void Node(PlanNode node, int depth)
      {
         StringBuilder line = new();
         line.Append(' ', depth * 2);
         switch (node)
         {
            case ScanNode scan:
               line.Append("Scan ").Append(scan.Entity.DisplayName).Append(" (").AppendJoin(", ", scan.Output).Append(')');
               break;
            case OneRowNode:
               line.Append("OneRow");
               break;
            case FilterNode filter:
               line.Append("Filter ").Append(Expr(filter.Predicate));
               break;
            case ProjectNode project:
               line.Append("Project ").AppendJoin(", ", project.Items.Select(Item));
               break;
            case JoinNode join:
               line.Append("Join ").Append(join.Kind.ToString().ToLowerInvariant());
               if (join.Navigation != null) { line.Append(" via ").Append(join.Navigation.Name); }
               if (join.Condition != null) { line.Append(": ").Append(Expr(join.Condition)); }
               break;
            case SortNode sort:
               line.Append("Sort ").AppendJoin(", ", sort.Keys.Select(k => Expr(k.Expr) + (k.Descending ? " desc" : string.Empty)));
               break;
            case LimitNode limit:
               line.Append("Limit");
               if (limit.Offset != null) { line.Append(" skip ").Append(Expr(limit.Offset)); }
               if (limit.Count != null) { line.Append(" take ").Append(Expr(limit.Count)); }
               break;
            case DistinctNode:
               line.Append("Distinct");
               break;
            case AggregateNode aggregate:
               line.Append("Aggregate");
               if (aggregate.Keys.Count > 0) { line.Append(" by ").AppendJoin(", ", aggregate.Keys.Select(Item)); }
               if (aggregate.Aggregates.Count > 0) { line.Append(": ").AppendJoin(", ", aggregate.Aggregates.Select(Aggregate)); }
               break;
            case SetOpNode set:
               line.Append(set.Operation.ToString()).Append(" (").AppendJoin(", ", set.Output).Append(')');
               break;
            case Federation.MergeTableNode table:
               line.Append("MergeTable ").Append(table.Table).Append(" (").AppendJoin(", ", table.Output).Append(')');
               break;
            default:
               line.Append(node.GetType().Name);
               break;
         }
         if (site?.Invoke(node) is { } where) { line.Append("  @").Append(where); }
         text!.Append(line).AppendLine();
         List<PlanSubquery> subqueries = [.. pending];
         pending.Clear();
         foreach (PlanSubquery subquery in subqueries)
         {
            text.Append(' ', depth * 2 + 2).Append('[').Append(Number(subquery)).Append("] ").AppendLine(subquery.Kind.ToString().ToLowerInvariant());
            Node(subquery.Plan, depth + 2);
         }
         foreach (PlanNode input in node.Inputs) { Node(input, depth + 1); }
      }

      private readonly Dictionary<PlanSubquery, int> numbers = new(ReferenceEqualityComparer.Instance);

      private int Number(PlanSubquery subquery)
      {
         if (!numbers.TryGetValue(subquery, out int number))
         {
            number = ++numbered;
            numbers[subquery] = number;
         }
         return number;
      }

      private string Item(ProjectItem item) => item.IsPassThrough ? item.Column.ToString() : $"{item.Column} = {Expr(item.Expr)}";

      private string Aggregate(AggregateItem item)
      {
         string function = item.Function switch
         {
            AggregateFunction.CountRows => "count(*)",
            AggregateFunction.CountDistinct => $"countDistinct({Expr(item.Argument!)})",
            _ => $"{item.Function.ToString().ToLowerInvariant()}({Expr(item.Argument!)})",
         };
         return $"{item.Column} = {function}";
      }

      public string Expr(PlanExpr expr) => expr switch
      {
         PlanColumnRef reference => reference.Column.ToString(),
         PlanLiteral literal => BoundTreePrinter.Literal(literal.Value),
         PlanParameter { Source: ParameterSource.User } parameter => "$" + parameter.Name,
         PlanParameter parameter => parameter.Name + "()",
         PlanUnary unary => OperatorText.Symbol(unary.Op) + Operand(unary.Operand),
         PlanBinary binary => $"{Operand(binary.Left)} {OperatorText.Symbol(binary.Op)} {Operand(binary.Right)}",
         PlanIsNull isNull => $"{Operand(isNull.Operand)} is {(isNull.Negated ? "not " : string.Empty)}null",
         PlanInList inList => $"{Operand(inList.Operand)} {(inList.Negated ? "not in" : "in")} [{string.Join(", ", inList.Items.Select(Expr))}]",
         PlanConditional conditional => $"{Operand(conditional.Condition)} ? {Operand(conditional.WhenTrue)} : {Operand(conditional.WhenFalse)}",
         PlanFunction call => $"{call.Function.Name}({string.Join(", ", call.Arguments.Select(Expr))})",
         PlanSubquery subquery => Subquery(subquery),
         _ => expr.GetType().Name,
      };

      private string Subquery(PlanSubquery subquery)
      {
         string reference = text == null ? "…" : Number(subquery).ToString(System.Globalization.CultureInfo.InvariantCulture);
         if (text != null) { pending.Add(subquery); }
         return subquery.Kind switch
         {
            SubqueryKind.Exists => $"{(subquery.Negated ? "not exists" : "exists")}[{reference}]",
            SubqueryKind.In => $"{Operand(subquery.Operand!)} in [{reference}]",
            _ => $"value[{reference}]",
         };
      }

      private string Operand(PlanExpr expr) =>
         expr is PlanBinary or PlanConditional or PlanIsNull or PlanInList ? "(" + Expr(expr) + ")" : Expr(expr);
   }
}
