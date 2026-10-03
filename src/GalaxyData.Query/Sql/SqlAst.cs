using System.Collections.Generic;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Sql;

/// <summary>
/// A small SQL syntax tree, just enough for what the planner produces. Dialects decide how each node is written;
/// expressions know their precedence so the writer can add the parentheses they need and no more.
/// </summary>
internal abstract class SqlExpr
{
   /// <summary>How tightly the expression binds; <see cref="SqlPrecedence.Atom"/> never needs parentheses.</summary>
   public abstract int Precedence { get; }

   /// <summary>A search condition rather than a value; matters where booleans are not values (SQL Server).</summary>
   public virtual bool IsPredicate => false;
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

internal sealed class SqlColumn(string? table, string column) : SqlExpr
{
   public string? Table { get; } = table;

   public string Column { get; } = column;

   /// <summary>The type of a table's column as its database declares it (<c>datetime</c>), for the values compared with it.</summary>
   public string? NativeType { get; init; }

   public override int Precedence => SqlPrecedence.Atom;
}

internal sealed class SqlParameterRef(SqlParameterSlot slot) : SqlExpr
{
   public SqlParameterSlot Slot { get; } = slot;

   public override int Precedence => SqlPrecedence.Atom;
}

/// <summary>A constant written into the SQL text: null, booleans, row counts, and fixed text the dialect needs.</summary>
internal sealed class SqlLiteral(object? value, ScalarType type) : SqlExpr
{
   public object? Value { get; } = value;

   public ScalarType Type { get; } = type;

   public override int Precedence => Value is long or int or decimal or double && IsNegative ? SqlPrecedence.Unary : SqlPrecedence.Atom;

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
internal sealed class SqlRaw(string text) : SqlExpr
{
   public string Text { get; } = text;

   public override int Precedence => SqlPrecedence.Atom;
}

internal enum SqlUnaryOp : byte
{
   Negate,
   Not,
}

internal sealed class SqlUnary(SqlUnaryOp op, SqlExpr operand) : SqlExpr
{
   public SqlUnaryOp Op { get; } = op;

   public SqlExpr Operand { get; } = operand;

   public override int Precedence => Op == SqlUnaryOp.Not ? SqlPrecedence.Not : SqlPrecedence.Unary;

   public override bool IsPredicate => Op == SqlUnaryOp.Not;
}

internal enum SqlBinaryOp : byte
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

internal sealed class SqlBinary(SqlBinaryOp op, SqlExpr left, SqlExpr right) : SqlExpr
{
   public SqlBinaryOp Op { get; } = op;

   public SqlExpr Left { get; } = left;

   public SqlExpr Right { get; } = right;

   public override int Precedence => Op switch
   {
      SqlBinaryOp.Or => SqlPrecedence.Or,
      SqlBinaryOp.And => SqlPrecedence.And,
      SqlBinaryOp.Add or SqlBinaryOp.Subtract or SqlBinaryOp.Concat => SqlPrecedence.Additive,
      SqlBinaryOp.Multiply or SqlBinaryOp.Divide or SqlBinaryOp.Modulo => SqlPrecedence.Multiplicative,
      _ => SqlPrecedence.Comparison,
   };

   public override bool IsPredicate => Precedence <= SqlPrecedence.Comparison;
}

internal sealed class SqlIsNull(SqlExpr operand, bool negated) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public bool Negated { get; } = negated;

   public override int Precedence => SqlPrecedence.Comparison;

   public override bool IsPredicate => true;
}

internal sealed class SqlIn(SqlExpr operand, IReadOnlyList<SqlExpr> items, bool negated) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public IReadOnlyList<SqlExpr> Items { get; } = items;

   public bool Negated { get; } = negated;

   public override int Precedence => SqlPrecedence.Comparison;

   public override bool IsPredicate => true;
}

internal sealed class SqlBetween(SqlExpr operand, SqlExpr low, SqlExpr high) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public SqlExpr Low { get; } = low;

   public SqlExpr High { get; } = high;

   public override int Precedence => SqlPrecedence.Comparison;

   public override bool IsPredicate => true;
}

/// <summary><c>x LIKE p</c>, optionally <c>ILIKE</c>, with an escape character.</summary>
internal sealed class SqlLike(SqlExpr operand, SqlExpr pattern, bool caseInsensitive, char? escape) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public SqlExpr Pattern { get; } = pattern;

   public bool CaseInsensitive { get; } = caseInsensitive;

   public char? Escape { get; } = escape;

   public override int Precedence => SqlPrecedence.Comparison;

   public override bool IsPredicate => true;
}

internal sealed record SqlWhen(SqlExpr Condition, SqlExpr Result);

internal sealed class SqlCase(IReadOnlyList<SqlWhen> whens, SqlExpr? otherwise) : SqlExpr
{
   public IReadOnlyList<SqlWhen> Whens { get; } = whens;

   public SqlExpr? Else { get; } = otherwise;

   public override int Precedence => SqlPrecedence.Atom;
}

internal sealed class SqlCast(SqlExpr operand, string typeName) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public string TypeName { get; } = typeName;

   public override int Precedence => SqlPrecedence.Atom;
}

/// <summary>An aggregate: <c>count(*)</c> when the argument is null, <c>count(DISTINCT x)</c>, <c>sum(x)</c>.</summary>
internal sealed class SqlAggregate(string name, SqlExpr? argument, bool distinct = false) : SqlExpr
{
   public string Name { get; } = name;

   public SqlExpr? Argument { get; } = argument;

   public bool Distinct { get; } = distinct;

   public override int Precedence => SqlPrecedence.Atom;
}

/// <summary><c>[NOT] EXISTS (select)</c>.</summary>
internal sealed class SqlExists(SqlQuery query, bool negated) : SqlExpr
{
   public SqlQuery Query { get; } = query;

   public bool Negated { get; } = negated;

   public override int Precedence => Negated ? SqlPrecedence.Not : SqlPrecedence.Atom;

   public override bool IsPredicate => true;
}

/// <summary>A subquery giving one value.</summary>
internal sealed class SqlScalarSubquery(SqlQuery query) : SqlExpr
{
   public SqlQuery Query { get; } = query;

   public override int Precedence => SqlPrecedence.Atom;
}

/// <summary><c>x IN (select)</c>.</summary>
internal sealed class SqlInSubquery(SqlExpr operand, SqlQuery query) : SqlExpr
{
   public SqlExpr Operand { get; } = operand;

   public SqlQuery Query { get; } = query;

   public override int Precedence => SqlPrecedence.Comparison;

   public override bool IsPredicate => true;
}

internal sealed class SqlFunctionCall(string name, IReadOnlyList<SqlExpr> arguments) : SqlExpr
{
   public SqlFunctionCall(string name, params SqlExpr[] arguments) : this(name, (IReadOnlyList<SqlExpr>)arguments) { }

   public string Name { get; } = name;

   public IReadOnlyList<SqlExpr> Arguments { get; } = arguments;

   public override int Precedence => SqlPrecedence.Atom;
}

/// <summary>
/// Function-like syntax that isn't a plain call, such as <c>EXTRACT(YEAR FROM {0})</c>. <c>{n}</c> is replaced by
/// argument n, parenthesized unless it is atomic.
/// </summary>
internal sealed class SqlTemplate(string format, IReadOnlyList<SqlExpr> arguments, bool isPredicate = false) : SqlExpr
{
   public string Format { get; } = format;

   public IReadOnlyList<SqlExpr> Arguments { get; } = arguments;

   public override int Precedence => SqlPrecedence.Atom;

   public override bool IsPredicate { get; } = isPredicate;
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

internal enum SqlSetOperator : byte
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
