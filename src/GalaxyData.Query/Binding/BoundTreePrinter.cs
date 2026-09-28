using System;
using System.Globalization;
using System.Linq;
using System.Text;
using GalaxyData.Query.Language;

namespace GalaxyData.Query.Binding;

/// <summary>
/// Renders bound trees for tests and diagnostics: one line per query operator with its input indented below,
/// expressions in query syntax. Dates, times and guids print as <c>'2026-01-05'::date</c>.
/// </summary>
public static class BoundTreePrinter
{
   public static string Print(BoundProgram program)
   {
      ArgumentNullException.ThrowIfNull(program);
      StringBuilder text = new();
      foreach (BoundLet let in program.Lets)
      {
         if (let.Value is BoundQuery query)
         {
            text.Append("let ").Append(let.Name).AppendLine(" =");
            Query(text, query, 1);
         }
         else
         {
            BoundExpr value = (BoundExpr)let.Value;
            text.Append("let ").Append(let.Name).Append(" = ").Append(Expr(value)).Append(" :: ").Append(value.Type).AppendLine();
         }
      }
      switch (program.Result)
      {
         case BoundQuery query:
            Query(text, query, 0);
            break;
         case BoundExpr value:
            text.Append("Value ").Append(Expr(value)).Append(" :: ").Append(value.Type).AppendLine();
            break;
         default:
            text.AppendLine("(no result)");
            break;
      }
      return text.ToString();
   }

   public static string Print(BoundQuery query)
   {
      StringBuilder text = new();
      Query(text, query, 0);
      return text.ToString();
   }

   private static void Query(StringBuilder text, BoundQuery query, int depth)
   {
      text.Append(' ', depth * 2);
      switch (query)
      {
         case BoundEntityScan scan:
            text.Append("Scan ").Append(scan.Entity.DisplayName).AppendLine();
            return;
         case BoundLetQuery let:
            text.Append("LetRef ").Append(let.Let.Name).AppendLine();
            return;
         case BoundWhere where:
            text.Append("Where(").Append(where.Row.Name).Append(") ").Append(Expr(where.Predicate)).AppendLine();
            Query(text, where.Input, depth + 1);
            return;
         case BoundSelect select:
            text.Append("Select ").AppendJoin(", ", select.Items.Select(Projection)).AppendLine();
            Query(text, select.Input, depth + 1);
            return;
         case BoundExtend extend:
            text.Append("Extend ").AppendJoin(", ", extend.Items.Select(Projection)).AppendLine();
            Query(text, extend.Input, depth + 1);
            return;
         case BoundOrderBy order:
            text.Append("OrderBy(").Append(order.Row.Name).Append(") ")
                .AppendJoin(", ", order.Keys.Select(k => Expr(k.Expr) + (k.Descending ? " desc" : string.Empty))).AppendLine();
            Query(text, order.Input, depth + 1);
            return;
         case BoundTake take:
            text.Append("Take ").Append(Expr(take.Count)).AppendLine();
            Query(text, take.Input, depth + 1);
            return;
         case BoundSkip skip:
            text.Append("Skip ").Append(Expr(skip.Count)).AppendLine();
            Query(text, skip.Input, depth + 1);
            return;
         case BoundDistinct distinct:
            text.AppendLine("Distinct");
            Query(text, distinct.Input, depth + 1);
            return;
         default:
            text.Append(query.GetType().Name).AppendLine();
            return;
      }
   }

   private static string Projection(BoundProjection item) => $"{Name(item.Name)}: {item.Expr.Type} = {Expr(item.Expr)}";

   private static string Name(string name) => QueryText.IsBareIdentifier(name) ? name : QueryText.QuoteString(name);

   public static string Expr(BoundExpr expr) => expr switch
   {
      BoundLiteral literal => Literal(literal.Value),
      BoundParameter parameter => "$" + parameter.Name,
      BoundRowRef row => row.Row.Name,
      BoundMemberAccess member => Member(member),
      BoundUnary unary => OperatorText.Symbol(unary.Op) + Operand(unary.Operand),
      BoundBinary binary => $"{Operand(binary.Left)} {OperatorText.Symbol(binary.Op)} {Operand(binary.Right)}",
      BoundIsNull isNull => $"{Operand(isNull.Operand)} is {(isNull.Negated ? "not " : string.Empty)}null",
      BoundInList inList => $"{Operand(inList.Operand)} {(inList.Negated ? "not in" : "in")} [{string.Join(", ", inList.Items.Select(Expr))}]",
      BoundConditional conditional => $"{Operand(conditional.Condition)} ? {Operand(conditional.WhenTrue)} : {Operand(conditional.WhenFalse)}",
      BoundFunctionCall call => $"{call.Function.Name}({string.Join(", ", call.Arguments.Select(Expr))})",
      BoundLetValue let => let.Let.Name,
      _ => expr.GetType().Name,
   };

   private static string Member(BoundMemberAccess member)
   {
      StringBuilder text = new(Expr(member.Target));
      return QueryText.AppendMember(text, member.Member.Name).ToString();
   }

   private static string Operand(BoundExpr expr) =>
      expr is BoundBinary or BoundConditional or BoundIsNull or BoundInList ? "(" + Expr(expr) + ")" : Expr(expr);

   private static string Literal(object? value) => value switch
   {
      null => "null",
      bool flag => flag ? "true" : "false",
      string text => QueryText.QuoteString(text),
      double number => number.ToString("R", CultureInfo.InvariantCulture),
      decimal number => number.ToString(CultureInfo.InvariantCulture),
      IFormattable number when value is long or int or short => number.ToString(null, CultureInfo.InvariantCulture),
      DateOnly date => $"'{date:yyyy-MM-dd}'::date",
      TimeOnly time => $"'{time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)}'::time",
      DateTime dateTime => $"'{dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)}'::datetime",
      DateTimeOffset offset => $"'{offset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)}'::datetimeoffset",
      Guid guid => $"'{guid}'::guid",
      _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?",
   };
}
