using System;
using System.Linq;
using System.Text;
using GalaxyData.Query.Binding;

namespace GalaxyData.Query.Planning;

/// <summary>
/// Renders logical plans for tests and explain: one line per operator with its inputs indented below. Columns
/// print as <c>name#id</c>, so a reference can be traced to the operator that produces it.
/// </summary>
public static class PlanPrinter
{
   public static string Print(PlanNode node)
   {
      ArgumentNullException.ThrowIfNull(node);
      StringBuilder text = new();
      Node(text, node, 0);
      return text.ToString();
   }

   private static void Node(StringBuilder text, PlanNode node, int depth)
   {
      text.Append(' ', depth * 2);
      switch (node)
      {
         case ScanNode scan:
            text.Append("Scan ").Append(scan.Entity.DisplayName).Append(" (").AppendJoin(", ", scan.Output).AppendLine(")");
            break;
         case OneRowNode:
            text.AppendLine("OneRow");
            break;
         case FilterNode filter:
            text.Append("Filter ").AppendLine(Expr(filter.Predicate));
            break;
         case ProjectNode project:
            text.Append("Project ").AppendJoin(", ", project.Items.Select(Item)).AppendLine();
            break;
         case JoinNode join:
            text.Append("Join ").Append(join.Kind.ToString().ToLowerInvariant());
            if (join.Navigation != null) { text.Append(" via ").Append(join.Navigation.Name); }
            if (join.Condition != null) { text.Append(": ").Append(Expr(join.Condition)); }
            text.AppendLine();
            break;
         case SortNode sort:
            text.Append("Sort ").AppendJoin(", ", sort.Keys.Select(k => Expr(k.Expr) + (k.Descending ? " desc" : string.Empty))).AppendLine();
            break;
         case LimitNode limit:
            text.Append("Limit");
            if (limit.Offset != null) { text.Append(" skip ").Append(Expr(limit.Offset)); }
            if (limit.Count != null) { text.Append(" take ").Append(Expr(limit.Count)); }
            text.AppendLine();
            break;
         case DistinctNode:
            text.AppendLine("Distinct");
            break;
         default:
            text.AppendLine(node.GetType().Name);
            break;
      }
      foreach (PlanNode input in node.Inputs) { Node(text, input, depth + 1); }
   }

   private static string Item(ProjectItem item) => item.IsPassThrough ? item.Column.ToString() : $"{item.Column} = {Expr(item.Expr)}";

   public static string Expr(PlanExpr expr) => expr switch
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
      _ => expr.GetType().Name,
   };

   private static string Operand(PlanExpr expr) =>
      expr is PlanBinary or PlanConditional or PlanIsNull or PlanInList ? "(" + Expr(expr) + ")" : Expr(expr);
}
