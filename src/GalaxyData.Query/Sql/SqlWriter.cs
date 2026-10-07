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

   /// <summary>The column a parameter being written is compared with.</summary>
   private SqlColumn? comparedWith;

   /// <summary>Set when the query has more values than the database takes parameters: its constants are written into the SQL.</summary>
   private bool inlineConstants;
   private int indent;

   /// <summary>What columns of no table are written after, in SQL Server's OUTPUT clause: <c>INSERTED</c>.</summary>
   private string? columnPrefix;

   private SqlWriter(SqlDialect dialect) { this.dialect = dialect; }

   /// <summary>
   /// Writes the query and names its parameters p0, p1, ... in the order they appear. A parameter written twice is one
   /// parameter, except where the database types each from its first use (DuckDB): there every use is its own.
   /// </summary>
   public static string Write(SqlQuery query, SqlDialect dialect, out IReadOnlyList<SqlParameterSlot> parameters)
   {
      SqlWriter writer = new(dialect);
      writer.Query(query);
      if (writer.written.Count > dialect.MaxParameters)
      {
         writer = new SqlWriter(dialect) { inlineConstants = true };
         writer.Query(query);
      }
      parameters = writer.written;
      return writer.text.ToString();
   }

   public static string Write(SqlQuery query, SqlDialect dialect) => Write(query, dialect, out _);

   /// <summary>
   /// Writes a data change, with the rows it gives back when <paramref name="returning"/> is set, and then
   /// <paramref name="then"/> in the same batch (SQL Server reads an inserted row back, or the count of rows changed).
   /// With <paramref name="inline"/>, values are written in as constants: the text people read, and may edit and run.
   /// </summary>
   public static string Write(SqlDml statement, SqlSelect? then, SqlDialect dialect, bool returning, bool inline, out IReadOnlyList<SqlParameterSlot> parameters)
   {
      SqlWriter writer = new(dialect) { inlineConstants = inline };
      writer.Dml(statement, then, returning);
      if (writer.written.Count > dialect.MaxParameters)
      {
         writer = new SqlWriter(dialect) { inlineConstants = true };
         writer.Dml(statement, then, returning);
      }
      parameters = writer.written;
      return writer.text.ToString();
   }

   private void Dml(SqlDml statement, SqlSelect? then, bool returning)
   {
      switch (statement)
      {
         case SqlInsert insert:
            Insert(insert, returning && insert.Returning.Count > 0);
            break;
         case SqlUpdate update:
            text.Append("UPDATE ");
            TableName(update.Table);
            NewLine();
            text.Append("SET ");
            for (int i = 0; i < update.Assignments.Count; i++)
            {
               if (i > 0) { text.Append(", "); }
               SqlAssignment assignment = update.Assignments[i];
               text.Append(dialect.Identifier(assignment.Column.Column)).Append(" = ");
               Compared(assignment.Value, assignment.Column, parentheses: false);
            }
            NewLine();
            text.Append("WHERE ");
            Expr(update.Where);
            break;
         case SqlDelete delete:
            text.Append("DELETE FROM ");
            TableName(delete.Table);
            NewLine();
            text.Append("WHERE ");
            Expr(delete.Where);
            break;
      }
      if (returning && then != null)
      {
         text.Append(';');
         NewLine();
         Select(then);
      }
   }

   private void Insert(SqlInsert insert, bool returning)
   {
      text.Append("INSERT INTO ");
      TableName(insert.Table);
      if (insert.Columns.Count > 0)
      {
         text.Append(" (");
         for (int i = 0; i < insert.Columns.Count; i++)
         {
            if (i > 0) { text.Append(", "); }
            text.Append(dialect.Identifier(insert.Columns[i].Column));
         }
         text.Append(')');
      }
      bool output = returning && dialect.Returning == ReturningStyle.Output;
      if (output)
      {
         NewLine();
         text.Append("OUTPUT ");
         columnPrefix = "INSERTED";
         List(insert.Returning);
         columnPrefix = null;
      }
      NewLine();
      if (insert.Columns.Count == 0)
      {
         text.Append("DEFAULT VALUES");
      }
      else
      {
         text.Append("VALUES (");
         for (int i = 0; i < insert.Values.Count; i++)
         {
            if (i > 0) { text.Append(", "); }
            Compared(insert.Values[i], insert.Columns[i], parentheses: false);
         }
         text.Append(')');
      }
      if (returning && !output)
      {
         NewLine();
         text.Append("RETURNING ");
         List(insert.Returning);
      }
   }

   private void TableName(SqlTable table)
   {
      if (table.Catalog != null) { text.Append(dialect.Identifier(table.Catalog)).Append('.'); }
      if (table.Schema != null) { text.Append(dialect.Identifier(table.Schema)).Append('.'); }
      text.Append(dialect.Identifier(table.Name));
   }

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
            text.Append(dialect.SetOperator(compound.Operator));
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
         case PagingStyle.LimitOffsetNeedsLimit:
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
            TableName(table);
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
            else if (columnPrefix != null) { text.Append(columnPrefix).Append('.'); }
            text.Append(dialect.Identifier(column.Column));
            break;
         case SqlParameterRef { Slot.Source: null } constant when inlineConstants:
            SqlParameterSlot value = constant.Slot;
            dialect.WriteLiteral(text, value.Pattern != null && value.Constant is string pattern ? value.Pattern.Apply(pattern) : value.Constant, value.Type,
                                 comparedWith?.NativeType);
            break;
         case SqlParameterRef parameter:
            SqlParameterSlot slot = Named(parameter.Slot);
            if (comparedWith != null)
            {
               slot.ComparedWithColumn = true;
               slot.ColumnType = comparedWith.NativeType;
            }
            text.Append(dialect.Placeholder(slot.Name));
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
            for (int i = 0; i < inList.Items.Count; i++)
            {
               if (i > 0) { text.Append(", "); }
               Compared(inList.Items[i], inList.Operand, parentheses: false);
            }
            text.Append(')');
            break;
         case SqlBetween between:
            Operand(between.Operand);
            text.Append(" BETWEEN ");
            Compared(between.Low, between.Operand, between.Low.Precedence <= SqlPrecedence.Comparison);
            text.Append(" AND ");
            Compared(between.High, between.Operand, between.High.Precedence <= SqlPrecedence.Comparison);
            break;
         case SqlLike like:
            Operand(like.Operand);
            text.Append(like.CaseInsensitive ? " ILIKE " : " LIKE ");
            Operand(like.Pattern);
            if (like.Escape is { } escape) { text.Append(dialect.LikeEscape(escape)); }
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
      bool compares = binary.Op is SqlBinaryOp.Equal or SqlBinaryOp.NotEqual or SqlBinaryOp.Less or SqlBinaryOp.LessOrEqual
                                 or SqlBinaryOp.Greater or SqlBinaryOp.GreaterOrEqual;
      if (compares) { Compared(binary.Left, binary.Right, left); }
      else { Wrapped(binary.Left, left); }
      text.Append(' ').Append(Symbol(binary.Op)).Append(' ');
      if (compares) { Compared(binary.Right, binary.Left, right); }
      else { Wrapped(binary.Right, right); }
   }

   /// <summary>Writes a value compared with <paramref name="other"/>, or stored in it: a parameter meeting a column is marked so.</summary>
   private void Compared(SqlExpr value, SqlExpr other, bool parentheses)
   {
      comparedWith = value is SqlParameterRef ? other as SqlColumn : null;
      Wrapped(value, parentheses);
      comparedWith = null;
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
