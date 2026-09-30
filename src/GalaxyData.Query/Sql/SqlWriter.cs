using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GalaxyData.Query.Sql;

/// <summary>Writes a SQL tree as text in one dialect: one clause per line, derived tables indented.</summary>
internal sealed class SqlWriter
{
   private const int IndentSize = 3;

   private readonly SqlDialect dialect;
   private readonly StringBuilder text = new();
   private readonly List<SqlParameterSlot> written = [];
   private readonly Dictionary<SqlParameterSlot, SqlParameterSlot> named = new(ReferenceEqualityComparer.Instance);
   private int indent;

   private SqlWriter(SqlDialect dialect) { this.dialect = dialect; }

   /// <summary>
   /// Writes the query and names its parameters p0, p1, ... in the order they appear. A parameter written twice is one
   /// parameter, except where the database types each from its first use (DuckDB): there every use is its own.
   /// </summary>
   public static string Write(SqlQuery query, SqlDialect dialect, out IReadOnlyList<SqlParameterSlot> parameters)
   {
      SqlWriter writer = new(dialect);
      writer.Query(query);
      parameters = writer.written;
      return writer.text.ToString();
   }

   public static string Write(SqlQuery query, SqlDialect dialect) => Write(query, dialect, out _);

   private SqlParameterSlot Named(SqlParameterSlot slot)
   {
      if (dialect.SharesParameters && named.TryGetValue(slot, out SqlParameterSlot? existing)) { return existing; }
      SqlParameterSlot renamed = new("p" + written.Count, slot.Type, slot.Source, slot.Constant, slot.ParameterName, slot.Pattern);
      written.Add(renamed);
      named[slot] = renamed;
      return renamed;
   }

   private void Query(SqlQuery query)
   {
      switch (query)
      {
         case SqlSelect select:
            Select(select);
            break;
         case SqlCompound compound:
            Select(compound.Left);
            NewLine();
            text.Append(compound.Operator switch
            {
               SqlSetOperator.Union => "UNION",
               SqlSetOperator.UnionAll => "UNION ALL",
               SqlSetOperator.Intersect => "INTERSECT",
               _ => "EXCEPT",
            });
            NewLine();
            Select(compound.Right);
            break;
      }
   }

   /// <summary>A query inside an expression or FROM: in parentheses, indented on the lines between.</summary>
   private void Nested(SqlQuery query)
   {
      text.Append('(');
      indent++;
      NewLine();
      Query(query);
      indent--;
      NewLine();
      text.Append(')');
   }

   public static string Write(SqlExpr expr, SqlDialect dialect)
   {
      SqlWriter writer = new(dialect);
      writer.Expr(expr);
      return writer.text.ToString();
   }

   private void NewLine() => text.AppendLine().Append(' ', indent * IndentSize);

   private void Select(SqlSelect select)
   {
      text.Append("SELECT ");
      if (select.Distinct) { text.Append("DISTINCT "); }
      // FETCH NEXT must be at least 1, so no rows at all is TOP (0), whatever the offset.
      bool top = dialect.Paging == PagingStyle.TopOrOffsetFetch && select.Limit != null &&
         (select.Offset == null || select.Limit is SqlLiteral { Value: 0L or 0 });
      if (top)
      {
         text.Append("TOP (");
         Expr(select.Limit!);
         text.Append(") ");
      }
      for (int i = 0; i < select.Items.Count; i++)
      {
         if (i > 0) { text.Append(", "); }
         SqlSelectItem item = select.Items[i];
         Expr(item.Expr);
         if (item.Alias != null && !(item.Expr is SqlColumn column && column.Column == item.Alias))
         {
            text.Append(" AS ").Append(dialect.Identifier(item.Alias));
         }
      }
      if (select.From != null)
      {
         NewLine();
         text.Append("FROM ");
         From(select.From);
      }
      if (select.Where != null)
      {
         NewLine();
         text.Append("WHERE ");
         Expr(select.Where);
      }
      if (select.GroupBy.Count > 0)
      {
         NewLine();
         text.Append("GROUP BY ");
         List(select.GroupBy);
      }
      if (select.Having != null)
      {
         NewLine();
         text.Append("HAVING ");
         Expr(select.Having);
      }
      OrderBy(select, offset: !top && select.Offset != null);
      if (!top) { Paging(select); }
   }

   private void OrderBy(SqlSelect select, bool offset)
   {
      if (select.OrderBy.Count == 0)
      {
         // OFFSET ... FETCH needs an ORDER BY; this one keeps whatever order the rows come in.
         if (dialect.Paging == PagingStyle.TopOrOffsetFetch && offset)
         {
            NewLine();
            text.Append("ORDER BY (SELECT NULL)");
         }
         return;
      }
      NewLine();
      text.Append("ORDER BY ");
      for (int i = 0; i < select.OrderBy.Count; i++)
      {
         if (i > 0) { text.Append(", "); }
         SqlOrderItem key = select.OrderBy[i];
         Expr(key.Expr);
         if (key.Descending) { text.Append(" DESC"); }
         if (key.Nullable && dialect.NullOrdering(key.Descending) is { } nulls) { text.Append(' ').Append(nulls); }
      }
   }

   private void Paging(SqlSelect select)
   {
      if (select.Limit == null && select.Offset == null) { return; }
      NewLine();
      switch (dialect.Paging)
      {
         case PagingStyle.LimitOffset:
            if (select.Limit != null)
            {
               text.Append("LIMIT ");
               Expr(select.Limit);
               if (select.Offset != null) { text.Append(' '); }
            }
            if (select.Offset != null)
            {
               text.Append("OFFSET ");
               Expr(select.Offset);
            }
            break;
         case PagingStyle.SqliteLimitOffset:
            text.Append("LIMIT ");
            if (select.Limit != null) { Expr(select.Limit); }
            else { text.Append("-1"); }
            if (select.Offset != null)
            {
               text.Append(" OFFSET ");
               Expr(select.Offset);
            }
            break;
         case PagingStyle.TopOrOffsetFetch:
            text.Append("OFFSET ");
            Expr(select.Offset!);
            text.Append(" ROWS");
            if (select.Limit != null)
            {
               text.Append(" FETCH NEXT ");
               Expr(select.Limit);
               text.Append(" ROWS ONLY");
            }
            break;
      }
   }

   private void From(SqlTableSource source)
   {
      switch (source)
      {
         case SqlTable table:
            if (table.Catalog != null) { text.Append(dialect.Identifier(table.Catalog)).Append('.'); }
            if (table.Schema != null) { text.Append(dialect.Identifier(table.Schema)).Append('.'); }
            text.Append(dialect.Identifier(table.Name));
            if (!string.Equals(table.Name, table.Alias, StringComparison.Ordinal)) { text.Append(" AS ").Append(dialect.Identifier(table.Alias)); }
            break;
         case SqlDerivedTable derived:
            Nested(derived.Query);
            text.Append(" AS ").Append(dialect.Identifier(derived.Alias));
            break;
         case SqlJoin join:
            From(join.Left);
            NewLine();
            // Only SQLite takes an inner join without ON; with no condition it is a cross join everywhere.
            text.Append(join.Kind switch
            {
               SqlJoinKind.Inner when join.Condition != null => "INNER JOIN ",
               SqlJoinKind.Left => "LEFT JOIN ",
               _ => "CROSS JOIN ",
            });
            if (join.Right is SqlJoin)
            {
               text.Append('(');
               From(join.Right);
               text.Append(')');
            }
            else
            {
               From(join.Right);
            }
            if (join.Condition != null)
            {
               text.Append(" ON ");
               Expr(join.Condition);
            }
            else if (join.Kind == SqlJoinKind.Left)
            {
               text.Append(" ON 1 = 1");
            }
            break;
      }
   }

   private void Expr(SqlExpr expr)
   {
      switch (expr)
      {
         case SqlColumn column:
            if (column.Table != null) { text.Append(dialect.Identifier(column.Table)).Append('.'); }
            text.Append(dialect.Identifier(column.Column));
            break;
         case SqlParameterRef parameter:
            text.Append(dialect.Placeholder(Named(parameter.Slot).Name));
            break;
         case SqlLiteral literal:
            dialect.WriteLiteral(text, literal.Value, literal.Type);
            break;
         case SqlRaw raw:
            text.Append(raw.Text);
            break;
         case SqlUnary unary:
            text.Append(unary.Op == SqlUnaryOp.Not ? "NOT " : "-");
            Wrapped(unary.Operand, unary.Operand.Precedence < SqlPrecedence.Atom);
            break;
         case SqlBinary binary:
            Binary(binary);
            break;
         case SqlIsNull isNull:
            Operand(isNull.Operand);
            text.Append(isNull.Negated ? " IS NOT NULL" : " IS NULL");
            break;
         case SqlIn inList:
            Operand(inList.Operand);
            text.Append(inList.Negated ? " NOT IN (" : " IN (");
            List(inList.Items);
            text.Append(')');
            break;
         case SqlBetween between:
            Operand(between.Operand);
            text.Append(" BETWEEN ");
            Operand(between.Low);
            text.Append(" AND ");
            Operand(between.High);
            break;
         case SqlLike like:
            Operand(like.Operand);
            text.Append(like.CaseInsensitive ? " ILIKE " : " LIKE ");
            Operand(like.Pattern);
            if (like.Escape != null) { text.Append(" ESCAPE '").Append(like.Escape.Value).Append('\''); }
            break;
         case SqlCase caseExpr:
            text.Append("CASE");
            foreach (SqlWhen when in caseExpr.Whens)
            {
               text.Append(" WHEN ");
               Expr(when.Condition);
               text.Append(" THEN ");
               Expr(when.Result);
            }
            if (caseExpr.Else != null)
            {
               text.Append(" ELSE ");
               Expr(caseExpr.Else);
            }
            text.Append(" END");
            break;
         case SqlCast cast:
            text.Append("CAST(");
            Expr(cast.Operand);
            text.Append(" AS ").Append(cast.TypeName).Append(')');
            break;
         case SqlFunctionCall call:
            text.Append(call.Name).Append('(');
            List(call.Arguments);
            text.Append(')');
            break;
         case SqlTemplate template:
            Template(template);
            break;
         case SqlAggregate aggregate:
            text.Append(aggregate.Name).Append('(');
            if (aggregate.Argument == null) { text.Append('*'); }
            else
            {
               if (aggregate.Distinct) { text.Append("DISTINCT "); }
               Expr(aggregate.Argument);
            }
            text.Append(')');
            break;
         case SqlExists exists:
            text.Append(exists.Negated ? "NOT EXISTS " : "EXISTS ");
            Nested(exists.Query);
            break;
         case SqlScalarSubquery scalar:
            Nested(scalar.Query);
            break;
         case SqlInSubquery inQuery:
            Operand(inQuery.Operand);
            text.Append(" IN ");
            Nested(inQuery.Query);
            break;
      }
   }

   private void List(IReadOnlyList<SqlExpr> items)
   {
      for (int i = 0; i < items.Count; i++)
      {
         if (i > 0) { text.Append(", "); }
         Expr(items[i]);
      }
   }

   /// <summary>An operand of IS NULL, IN, BETWEEN or LIKE: parenthesized unless it binds tighter than a comparison.</summary>
   private void Operand(SqlExpr operand) => Wrapped(operand, operand.Precedence <= SqlPrecedence.Comparison);

   private void Wrapped(SqlExpr expr, bool parentheses)
   {
      if (parentheses) { text.Append('('); }
      Expr(expr);
      if (parentheses) { text.Append(')'); }
   }

   private void Binary(SqlBinary binary)
   {
      int precedence = binary.Precedence;
      bool concat = binary.Op == SqlBinaryOp.Concat;
      bool comparison = precedence == SqlPrecedence.Comparison;

      // Concatenation binds differently across databases (tightest of all in SQLite), so anything but an atom is wrapped.
      bool left = concat ? binary.Left.Precedence < SqlPrecedence.Atom && !IsSame(binary.Left, binary.Op)
                         : binary.Left.Precedence < precedence || (binary.Left.Precedence == precedence && comparison);
      bool right = concat ? binary.Right.Precedence < SqlPrecedence.Atom
                          : binary.Right.Precedence < precedence || (binary.Right.Precedence == precedence && !IsAssociative(binary, binary.Right));
      Wrapped(binary.Left, left);
      text.Append(' ').Append(Symbol(binary.Op)).Append(' ');
      Wrapped(binary.Right, right);
   }

   private static bool IsSame(SqlExpr expr, SqlBinaryOp op) => expr is SqlBinary binary && binary.Op == op;

   private static bool IsAssociative(SqlBinary parent, SqlExpr child) =>
      parent.Op is SqlBinaryOp.And or SqlBinaryOp.Or or SqlBinaryOp.Add or SqlBinaryOp.Multiply && IsSame(child, parent.Op);

   private string Symbol(SqlBinaryOp op) => op switch
   {
      SqlBinaryOp.Add => "+",
      SqlBinaryOp.Subtract => "-",
      SqlBinaryOp.Multiply => "*",
      SqlBinaryOp.Divide => "/",
      SqlBinaryOp.Modulo => "%",
      SqlBinaryOp.Concat => dialect.ConcatOperator,
      SqlBinaryOp.Equal => "=",
      SqlBinaryOp.NotEqual => "<>",
      SqlBinaryOp.Less => "<",
      SqlBinaryOp.LessOrEqual => "<=",
      SqlBinaryOp.Greater => ">",
      SqlBinaryOp.GreaterOrEqual => ">=",
      SqlBinaryOp.And => "AND",
      SqlBinaryOp.Or => "OR",
      SqlBinaryOp.Glob => "GLOB",
      _ => op.ToString(),
   };

   private void Template(SqlTemplate template)
   {
      string format = template.Format;
      for (int i = 0; i < format.Length; i++)
      {
         char c = format[i];
         int close = c == '{' ? format.IndexOf('}', i) : -1;
         if (close > i && int.TryParse(format.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < template.Arguments.Count)
         {
            SqlExpr argument = template.Arguments[index];
            Wrapped(argument, argument.Precedence < SqlPrecedence.Atom);
            i = close;
         }
         else
         {
            text.Append(c);
         }
      }
   }
}
