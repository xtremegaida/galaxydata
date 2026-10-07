using System.Collections.Generic;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sql;

/// <summary>
/// A small SQL syntax tree, just enough for what the planner produces. Dialects decide how each node is written;
/// expressions know their precedence so the writer can add the parentheses they need and no more. Dialects build
/// expressions of the public nodes; the kinds of node are the engine's own.
/// </summary>
public abstract class SqlExpr
{
   private protected SqlExpr() { }

   /// <summary>How tightly the expression binds; <see cref="SqlPrecedence.Atom"/> never needs parentheses.</summary>
   internal abstract int Precedence { get; }

   /// <summary>A search condition rather than a value; matters where booleans are not values (SQL Server).</summary>
   internal virtual bool IsPredicate => false;
}

internal static class SqlPrecedence
{
   public const int Or = 1;
   public const int And = 2;
   public const int Not = 3;
   public const int Comparison = 4;
   public const int Additive = 5;
   public const int Multiplicative = 6;
   public const int Unary = 7;
   public const int Atom = 9;
}

/// <summary>A column of a table (or of no table: one a statement gives back).</summary>
public sealed class SqlColumn : SqlExpr
{
   internal SqlColumn(string? table, string column)
   {
      Table = table;
      Column = column;
   }

   public string? Table { get; }

   public string Column { get; }

   /// <summary>The type of a table's column as its database declares it (<c>datetime</c>), for the values compared with it.</summary>
   public string? NativeType { get; internal init; }

   internal override int Precedence => SqlPrecedence.Atom;
}

/// <summary>A parameter of the statement.</summary>
public sealed class SqlParameterRef : SqlExpr
{
   internal SqlParameterRef(SqlParameterSlot slot) { Slot = slot; }

   public SqlParameterSlot Slot { get; }

   internal override int Precedence => SqlPrecedence.Atom;
}

/// <summary>A constant written into the SQL text: null, booleans, row counts, and fixed text the dialect needs.</summary>
public sealed class SqlLiteral(object? value, ScalarType type) : SqlExpr
{
   public object? Value { get; } = value;

   public ScalarType Type { get; } = type;

   internal override int Precedence => Value is long or int or decimal or double && IsNegative ? SqlPrecedence.Unary : SqlPrecedence.Atom;

   private bool IsNegative => Value switch
   {
      long l => l < 0,
      int i => i < 0,
      decimal m => m < 0,
      double d => d < 0,
      _ => false,
   };
}

/// <summary>Text the dialect writes verbatim: a keyword argument (<c>day</c>) or a fixed expression (<c>INTERVAL '1 day'</c>).</summary>
public sealed class SqlRaw(string text) : SqlExpr
{
   public string Text { get; } = text;

   internal override int Precedence => SqlPrecedence.Atom;
}

public enum SqlUnaryOp : byte
{
   Negate,
   Not,
}

public sealed class SqlUnary(SqlUnaryOp op, SqlExpr operand) : SqlExpr
{
   public SqlUnaryOp Op { get; } = op;

   public SqlExpr Operand { get; } = operand;

   internal override int Precedence => Op == SqlUnaryOp.Not ? SqlPrecedence.Not : SqlPrecedence.Unary;

   internal override bool IsPredicate => Op == SqlUnaryOp.Not;
}

public enum SqlBinaryOp : byte
{
   Add,
   Subtract,
   Multiply,
   Divide,
   Modulo,
   /// <summary><c>||</c>, or <c>+</c> in SQL Server.</summary>
   Concat,
   Equal,
   NotEqual,
   Less,
   LessOrEqual,
   Greater,
   GreaterOrEqual,
   And,
   Or,
   /// <summary>SQLite's case-sensitive pattern match.</summary>
   Glob,
}

public sealed class SqlBinary(SqlBinaryOp op, SqlExpr left, SqlExpr right) : SqlExpr
{
   public SqlBinaryOp Op { get; } = op;

   public SqlExpr Left { get; } = left;

   public SqlExpr Right { get; } = right;

   internal override int Precedence => Op switch
   {
      SqlBinaryOp.Or => SqlPrecedence.Or,
      SqlBinaryOp.And => SqlPrecedence.And,
      SqlBinaryOp.Add or SqlBinaryOp.Subtract or SqlBinaryOp.Concat => SqlPrecedence.Additive,
      SqlBinaryOp.Multiply or SqlBinaryOp.Divide or SqlBinaryOp.Modulo => SqlPrecedence.Multiplicative,
      _ => SqlPrecedence.Comparison,
   };

   internal override bool IsPredicate => Precedence <= SqlPrecedence.Comparison;
}

public sealed class SqlIsNull(SqlExpr operand, bool negated) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public bool Negated { get; } = negated;

   internal override int Precedence => SqlPrecedence.Comparison;

   internal override bool IsPredicate => true;
}

public sealed class SqlIn(SqlExpr operand, IReadOnlyList<SqlExpr> items, bool negated) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public IReadOnlyList<SqlExpr> Items { get; } = items;

   public bool Negated { get; } = negated;

   internal override int Precedence => SqlPrecedence.Comparison;

   internal override bool IsPredicate => true;
}

public sealed class SqlBetween(SqlExpr operand, SqlExpr low, SqlExpr high) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public SqlExpr Low { get; } = low;

   public SqlExpr High { get; } = high;

   internal override int Precedence => SqlPrecedence.Comparison;

   internal override bool IsPredicate => true;
}

/// <summary><c>x LIKE p</c>, optionally <c>ILIKE</c>, with an escape character.</summary>
public sealed class SqlLike(SqlExpr operand, SqlExpr pattern, bool caseInsensitive, char? escape) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public SqlExpr Pattern { get; } = pattern;

   public bool CaseInsensitive { get; } = caseInsensitive;

   public char? Escape { get; } = escape;

   internal override int Precedence => SqlPrecedence.Comparison;

   internal override bool IsPredicate => true;
}

public sealed record SqlWhen(SqlExpr Condition, SqlExpr Result);

public sealed class SqlCase(IReadOnlyList<SqlWhen> whens, SqlExpr? otherwise) : SqlExpr
{
   public IReadOnlyList<SqlWhen> Whens { get; } = whens;

   public SqlExpr? Else { get; } = otherwise;

   internal override int Precedence => SqlPrecedence.Atom;
}

public sealed class SqlCast(SqlExpr operand, string typeName) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public string TypeName { get; } = typeName;

   internal override int Precedence => SqlPrecedence.Atom;
}

/// <summary>An aggregate: <c>count(*)</c> when the argument is null, <c>count(DISTINCT x)</c>, <c>sum(x)</c>.</summary>
public sealed class SqlAggregate(string name, SqlExpr? argument, bool distinct = false) : SqlExpr
{
   public string Name { get; } = name;

   public SqlExpr? Argument { get; } = argument;

   public bool Distinct { get; } = distinct;

   internal override int Precedence => SqlPrecedence.Atom;
}

/// <summary><c>[NOT] EXISTS (select)</c>.</summary>
internal sealed class SqlExists(SqlQuery query, bool negated) : SqlExpr
{
   public SqlQuery Query { get; } = query;

   public bool Negated { get; } = negated;

   internal override int Precedence => Negated ? SqlPrecedence.Not : SqlPrecedence.Atom;

   internal override bool IsPredicate => true;
}

/// <summary>A subquery giving one value.</summary>
internal sealed class SqlScalarSubquery(SqlQuery query) : SqlExpr
{
   public SqlQuery Query { get; } = query;

   internal override int Precedence => SqlPrecedence.Atom;
}

/// <summary><c>x IN (select)</c>.</summary>
internal sealed class SqlInSubquery(SqlExpr operand, SqlQuery query) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public SqlQuery Query { get; } = query;

   internal override int Precedence => SqlPrecedence.Comparison;

   internal override bool IsPredicate => true;
}

public sealed class SqlFunctionCall(string name, IReadOnlyList<SqlExpr> arguments) : SqlExpr
{
   public SqlFunctionCall(string name, params SqlExpr[] arguments) : this(name, (IReadOnlyList<SqlExpr>)arguments) { }

   public string Name { get; } = name;

   public IReadOnlyList<SqlExpr> Arguments { get; } = arguments;

   internal override int Precedence => SqlPrecedence.Atom;
}

/// <summary>
/// Function-like syntax that isn't a plain call, such as <c>EXTRACT(YEAR FROM {0})</c>. <c>{n}</c> is replaced by
/// argument n, parenthesized unless it is atomic.
/// </summary>
public sealed class SqlTemplate(string format, IReadOnlyList<SqlExpr> arguments, bool isPredicate = false) : SqlExpr
{
   public string Format { get; } = format;

   public IReadOnlyList<SqlExpr> Arguments { get; } = arguments;

   internal override int Precedence => SqlPrecedence.Atom;

   internal override bool IsPredicate { get; } = isPredicate;
}

#region Queries

internal abstract class SqlTableSource;

internal sealed class SqlTable(string? schema, string name, string alias) : SqlTableSource
{
   /// <summary>The database the table is in, for sources that share one; see <see cref="Catalog.SourceInfo.Catalog"/>.</summary>
   public string? Catalog { get; init; }

   public string? Schema { get; } = schema;

   public string Name { get; } = name;

   public string Alias { get; } = alias;
}

internal sealed class SqlDerivedTable(SqlQuery query, string alias) : SqlTableSource
{
   public SqlQuery Query { get; } = query;

   public string Alias { get; } = alias;
}

internal enum SqlJoinKind : byte
{
   Inner,
   Left,
   Cross,
}

internal sealed class SqlJoin(SqlJoinKind kind, SqlTableSource left, SqlTableSource right, SqlExpr? condition) : SqlTableSource
{
   public SqlJoinKind Kind { get; } = kind;

   public SqlTableSource Left { get; } = left;

   public SqlTableSource Right { get; } = right;

   public SqlExpr? Condition { get; } = condition;
}

internal sealed record SqlSelectItem(SqlExpr Expr, string? Alias);

/// <summary>An ORDER BY key; <see cref="Nullable"/> tells the dialect whether null placement needs spelling out.</summary>
internal sealed record SqlOrderItem(SqlExpr Expr, bool Descending, bool Nullable);

/// <summary>A query: a SELECT, or a set operation of two.</summary>
internal abstract class SqlQuery;

public enum SqlSetOperator : byte
{
   Union,
   UnionAll,
   Intersect,
   Except,
}

/// <summary><c>left UNION right</c>; the sides are plain SELECTs with no ORDER BY or row limit.</summary>
internal sealed class SqlCompound(SqlSetOperator op, SqlSelect left, SqlSelect right) : SqlQuery
{
   public SqlSetOperator Operator { get; } = op;

   public SqlSelect Left { get; } = left;

   public SqlSelect Right { get; } = right;
}

internal sealed class SqlSelect : SqlQuery
{
   public bool Distinct { get; set; }

   public List<SqlSelectItem> Items { get; } = [];

   public SqlTableSource? From { get; set; }

   public SqlExpr? Where { get; set; }

   public List<SqlExpr> GroupBy { get; } = [];

   public SqlExpr? Having { get; set; }

   public List<SqlOrderItem> OrderBy { get; } = [];

   /// <summary>Row count; a literal or parameter.</summary>
   public SqlExpr? Limit { get; set; }

   public SqlExpr? Offset { get; set; }
}

#endregion

#region Data changes

/// <summary>A statement that changes rows of one table.</summary>
internal abstract class SqlDml(SqlTable table)
{
   public SqlTable Table { get; } = table;
}

/// <summary>
/// <c>INSERT INTO t (columns) VALUES (values)</c>, or <c>DEFAULT VALUES</c> when there are no columns; the row is
/// given back (<c>RETURNING</c>, SQL Server's <c>OUTPUT INSERTED</c>) when <see cref="Returning"/> has items.
/// </summary>
internal sealed class SqlInsert(SqlTable table, IReadOnlyList<SqlColumn> columns, IReadOnlyList<SqlExpr> values) : SqlDml(table)
{
   public IReadOnlyList<SqlColumn> Columns { get; } = columns;

   public IReadOnlyList<SqlExpr> Values { get; } = values;

   /// <summary>The inserted row's values to give back, over columns of no table.</summary>
   public IReadOnlyList<SqlExpr> Returning { get; init; } = [];
}

internal sealed record SqlAssignment(SqlColumn Column, SqlExpr Value);

internal sealed class SqlUpdate(SqlTable table, IReadOnlyList<SqlAssignment> assignments, SqlExpr where) : SqlDml(table)
{
   public IReadOnlyList<SqlAssignment> Assignments { get; } = assignments;

   public SqlExpr Where { get; } = where;
}

internal sealed class SqlDelete(SqlTable table, SqlExpr where) : SqlDml(table)
{
   public SqlExpr Where { get; } = where;
}

#endregion
