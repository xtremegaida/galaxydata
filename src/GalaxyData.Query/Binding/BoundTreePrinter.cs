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
         case BoundNavigationQuery navigation:
            text.Append("Navigate ").Append(Expr(navigation.Owner)).Append('.').Append(navigation.Navigation.Name).AppendLine();
            return;
         case BoundGroupBy group:
            text.Append("GroupBy(").Append(group.Row.Name).Append(") ").AppendJoin(", ", group.Keys.Select(Projection)).AppendLine();
            Query(text, group.Input, depth + 1);
            return;
         case BoundJoin join:
            text.Append(join.Kind == BoundJoinKind.Left ? "LeftJoin" : "Join").Append(" on ").Append(Expr(join.Condition))
                .Append(" => ").AppendJoin(", ", join.Items.Select(Projection)).AppendLine();
            Query(text, join.Left, depth + 1);
            Query(text, join.Right, depth + 1);
            return;
         case BoundSelectMany many:
            text.Append("SelectMany(").Append(many.Row.Name).Append(')');
            if (many.Items != null) { text.Append(" => ").AppendJoin(", ", many.Items.Select(Projection)); }
            text.AppendLine();
            Query(text, many.Input, depth + 1);
            Query(text, many.Collection, depth + 1);
            return;
         case BoundSetOperation set:
            text.AppendLine(set.Kind.ToString());
            Query(text, set.Left, depth + 1);
            Query(text, set.Right, depth + 1);
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
      BoundGroupAggregate aggregate => $"{Aggregate(aggregate.Kind)}({(aggregate.Argument == null ? string.Empty : Expr(aggregate.Argument))})",
      BoundQueryAggregate aggregate => $"{Chain(aggregate.Source)}.{Aggregate(aggregate.Kind)}({(aggregate.Argument == null ? string.Empty : Expr(aggregate.Argument))})",
      BoundExists exists => $"{Chain(exists.Source)}.{(exists.Negated ? "none" : "any")}()",
      BoundInQuery inQuery => $"{Operand(inQuery.Operand)} in {Chain(inQuery.Source)}",
      _ => expr.GetType().Name,
   };

   private static string Aggregate(AggregateKind kind) => kind switch
   {
      AggregateKind.CountDistinct => "countDistinct",
      _ => kind.ToString().ToLowerInvariant(),
   };

   /// <summary>A query on one line, as a method chain: for the subqueries inside expressions.</summary>
   public static string Chain(BoundQuery query) => query switch
   {
      BoundEntityScan scan => scan.Entity.DisplayName,
      BoundLetQuery let => let.Let.Name,
      BoundNavigationQuery navigation => $"{Expr(navigation.Owner)}.{navigation.Navigation.Name}",
      BoundWhere where => $"{Chain(where.Input)}.where({where.Row.Name} => {Expr(where.Predicate)})",
      BoundSelect select => $"{Chain(select.Input)}.select({string.Join(", ", select.Items.Select(i => $"{Name(i.Name)}: {Expr(i.Expr)}"))})",
      BoundExtend extend => $"{Chain(extend.Input)}.extend({string.Join(", ", extend.Items.Select(i => $"{Name(i.Name)}: {Expr(i.Expr)}"))})",
      BoundOrderBy order => $"{Chain(order.Input)}.orderBy({string.Join(", ", order.Keys.Select(k => k.Descending ? $"desc({Expr(k.Expr)})" : Expr(k.Expr)))})",
      BoundTake take => $"{Chain(take.Input)}.take({Expr(take.Count)})",
      BoundSkip skip => $"{Chain(skip.Input)}.skip({Expr(skip.Count)})",
      BoundDistinct distinct => $"{Chain(distinct.Input)}.distinct()",
      BoundGroupBy group => $"{Chain(group.Input)}.groupBy({string.Join(", ", group.Keys.Select(k => $"{Name(k.Name)}: {Expr(k.Expr)}"))})",
      BoundSetOperation set => $"{Chain(set.Left)}.{set.Kind.ToString().ToLowerInvariant()}({Chain(set.Right)})",
      BoundJoin join => $"{Chain(join.Left)}.{(join.Kind == BoundJoinKind.Left ? "leftJoin" : "join")}({Chain(join.Right)}, {Expr(join.Condition)})",
      BoundSelectMany many => $"{Chain(many.Input)}.selectMany({Chain(many.Collection)})",
      _ => query.GetType().Name,
   };

   private static string Member(BoundMemberAccess member)
   {
      StringBuilder text = new(Expr(member.Target));
      return QueryText.AppendMember(text, member.Member.Name).ToString();
   }

   private static string Operand(BoundExpr expr) =>
      expr is BoundBinary or BoundConditional or BoundIsNull or BoundInList ? "(" + Expr(expr) + ")" : Expr(expr);

   internal static string Literal(object? value) => value switch
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
