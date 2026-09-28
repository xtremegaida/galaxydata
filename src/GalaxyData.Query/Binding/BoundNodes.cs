using System;
using System.Collections.Generic;
using GalaxyData.Common.Ast;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Binding;

/// <summary>The type of a bound expression: a scalar, a record (a row or a single navigation) or a collection.</summary>
public abstract record BoundType
{
   public abstract bool Nullable { get; }
}

public sealed record ScalarBoundType(ScalarType Scalar) : BoundType
{
   public override bool Nullable => Scalar.Nullable;

   public override string ToString() => Scalar.ToString();
}

public sealed record RecordBoundType(RowShape Shape, bool IsNullable) : BoundType
{
   public override bool Nullable => IsNullable;

   public override string ToString() => $"record{(Shape.Entity != null ? " " + Shape.Entity.DisplayName : string.Empty)}{(IsNullable ? "?" : string.Empty)}";
}

public sealed record CollectionBoundType(RowShape Element) : BoundType
{
   public override bool Nullable => false;

   public override string ToString() => $"collection{(Element.Entity != null ? " " + Element.Entity.DisplayName : string.Empty)}";
}

public abstract class BoundNode
{
   private protected BoundNode(SyntaxNode? syntax) { Syntax = syntax; }

   /// <summary>The syntax the node was bound from, for spans and expression text; null for synthesized nodes.</summary>
   public SyntaxNode? Syntax { get; }
}

#region Expressions

public abstract class BoundExpr : BoundNode
{
   private protected BoundExpr(SyntaxNode? syntax) : base(syntax) { }

   public abstract BoundType Type { get; }

   public bool IsScalar => Type is ScalarBoundType;

   /// <summary>The scalar type; only valid when <see cref="IsScalar"/>.</summary>
   public ScalarType Scalar => Type is ScalarBoundType scalar ? scalar.Scalar : throw new InvalidOperationException($"The expression is a {Type}, not a scalar");
}

public abstract class BoundScalarExpr : BoundExpr
{
   private readonly ScalarBoundType type;

   private protected BoundScalarExpr(ScalarType type, SyntaxNode? syntax) : base(syntax) { this.type = new ScalarBoundType(type); }

   public override BoundType Type => type;
}

/// <summary>A constant. <see cref="Value"/> is a CLR value of the type's kind (long, decimal, string, DateOnly, ...) or null.</summary>
public sealed class BoundLiteral(object? value, ScalarType type, SyntaxNode? syntax) : BoundScalarExpr(type, syntax)
{
   public object? Value { get; } = value;
}

/// <summary>An external parameter (<c>$name</c>); the value is supplied with the query.</summary>
public sealed class BoundParameter(string name, ScalarType type, SyntaxNode? syntax) : BoundScalarExpr(type, syntax)
{
   public string Name { get; } = name;
}

public sealed class BoundRowRef(RowVariable row, SyntaxNode? syntax) : BoundExpr(syntax)
{
   private readonly RecordBoundType type = new(row.Shape, row.Nullable);

   public RowVariable Row { get; } = row;

   public override BoundType Type => type;
}

/// <summary>A column, navigation or record member of <see cref="Target"/>, which is a record.</summary>
public sealed class BoundMemberAccess(BoundExpr target, ShapeMember member, BoundType type, SyntaxNode? syntax) : BoundExpr(syntax)
{
   public BoundExpr Target { get; } = target;

   public ShapeMember Member { get; } = member;

   public override BoundType Type { get; } = type;
}

public sealed class BoundUnary(UnaryOp op, BoundExpr operand, ScalarType type, SyntaxNode? syntax) : BoundScalarExpr(type, syntax)
{
   public UnaryOp Op { get; } = op;

   public BoundExpr Operand { get; } = operand;
}

public sealed class BoundBinary(BinaryOp op, BoundExpr left, BoundExpr right, ScalarType type, SyntaxNode? syntax) : BoundScalarExpr(type, syntax)
{
   public BinaryOp Op { get; } = op;

   public BoundExpr Left { get; } = left;

   public BoundExpr Right { get; } = right;
}

public sealed class BoundIsNull(BoundExpr operand, bool negated, SyntaxNode? syntax) : BoundScalarExpr(ScalarType.Boolean.AsNonNullable(), syntax)
{
   public BoundExpr Operand { get; } = operand;

   public bool Negated { get; } = negated;
}

public sealed class BoundInList(BoundExpr operand, IReadOnlyList<BoundExpr> items, bool negated, SyntaxNode? syntax)
   : BoundScalarExpr(ScalarType.Boolean.WithNullable(operand.Type.Nullable), syntax)
{
   public BoundExpr Operand { get; } = operand;

   public IReadOnlyList<BoundExpr> Items { get; } = items;

   public bool Negated { get; } = negated;
}

public sealed class BoundConditional(BoundExpr condition, BoundExpr whenTrue, BoundExpr whenFalse, ScalarType type, SyntaxNode? syntax)
   : BoundScalarExpr(type, syntax)
{
   public BoundExpr Condition { get; } = condition;

   public BoundExpr WhenTrue { get; } = whenTrue;

   public BoundExpr WhenFalse { get; } = whenFalse;
}

public sealed class BoundFunctionCall(FunctionDef function, IReadOnlyList<BoundExpr> arguments, ScalarType type, SyntaxNode? syntax)
   : BoundScalarExpr(type, syntax)
{
   public FunctionDef Function { get; } = function;

   public IReadOnlyList<BoundExpr> Arguments { get; } = arguments;
}

public enum AggregateKind : byte
{
   Count,
   CountDistinct,
   Sum,
   Avg,
   Min,
   Max,
   /// <summary>Whether the condition holds for some element; only over the elements of a group.</summary>
   Any,
   /// <summary>Whether the condition holds for every element; only over the elements of a group.</summary>
   All,
}

/// <summary>
/// An aggregate over the rows of the current group: <c>sum(total)</c> after groupBy. The argument is bound against
/// <see cref="Element"/>, the grouped rows' variable; without one, count counts rows.
/// </summary>
public sealed class BoundGroupAggregate(AggregateKind kind, RowVariable element, BoundExpr? argument, ScalarType type, SyntaxNode? syntax)
   : BoundScalarExpr(type, syntax)
{
   public AggregateKind Kind { get; } = kind;

   public RowVariable Element { get; } = element;

   public BoundExpr? Argument { get; } = argument;
}

/// <summary>
/// An aggregate over the rows of a query, as a single value: <c>customer.orders.sum(total)</c>. The query may refer
/// to rows outside it (a correlated subquery); the argument is bound against <see cref="Row"/>.
/// </summary>
public sealed class BoundQueryAggregate(AggregateKind kind, BoundQuery source, RowVariable row, BoundExpr? argument, ScalarType type, SyntaxNode? syntax)
   : BoundScalarExpr(type, syntax)
{
   public AggregateKind Kind { get; } = kind;

   public BoundQuery Source { get; } = source;

   public RowVariable Row { get; } = row;

   public BoundExpr? Argument { get; } = argument;
}

/// <summary>Whether a query has rows (<c>any</c>), or has none when negated (<c>all</c> is "none that fail").</summary>
public sealed class BoundExists(BoundQuery source, bool negated, SyntaxNode? syntax) : BoundScalarExpr(ScalarType.Boolean.AsNonNullable(), syntax)
{
   public BoundQuery Source { get; } = source;

   public bool Negated { get; } = negated;
}

/// <summary>
/// The first row of a query (<c>first()</c>, <c>firstOrDefault()</c>), as a record of its shape. In an expression it is
/// null when the query has no rows; as a program's result, <c>first()</c> of no rows is an error.
/// </summary>
public sealed class BoundFirst(BoundQuery source, bool orDefault, RowShape shape, SyntaxNode? syntax) : BoundExpr(syntax)
{
   private readonly RecordBoundType type = new(shape, IsNullable: true);

   /// <summary>The query, with the condition of <c>first(condition)</c> applied.</summary>
   public BoundQuery Source { get; } = source;

   public bool OrDefault { get; } = orDefault;

   public override BoundType Type => type;
}

/// <summary><c>value in query</c>: whether the query's single column has the value.</summary>
public sealed class BoundInQuery(BoundExpr operand, BoundQuery source, SyntaxNode? syntax)
   : BoundScalarExpr(ScalarType.Boolean.WithNullable(operand.Type.Nullable), syntax)
{
   public BoundExpr Operand { get; } = operand;

   public BoundQuery Source { get; } = source;
}

/// <summary>
/// A member of the grouped rows seen from a group (<c>total</c> after groupBy): one value per element. It only
/// exists while binding: it is aggregated, or it matches a key part and becomes that part.
/// </summary>
internal sealed class BoundGroupCollection(RowVariable groupRow, BoundExpr projection, SyntaxNode? syntax) : BoundExpr(syntax)
{
   private readonly GroupCollectionType type = new(projection.Type);

   public RowVariable GroupRow { get; } = groupRow;

   public GroupInfo Group => GroupRow.Shape.Group!;

   /// <summary>The value for one element, over the group's element row.</summary>
   public BoundExpr Projection { get; } = projection;

   public override BoundType Type => type;
}

internal sealed record GroupCollectionType(BoundType Element) : BoundType
{
   public override bool Nullable => false;

   public override string ToString() => $"group collection of {Element}";
}

/// <summary>A reference to a named scalar value (<c>x := 3 * $rate</c>).</summary>
public sealed class BoundLetValue(BoundLet let, SyntaxNode? syntax) : BoundScalarExpr(((BoundExpr)let.Value).Scalar, syntax)
{
   public BoundLet Let { get; } = let;
}

#endregion

#region Queries

public abstract class BoundQuery : BoundNode
{
   private protected BoundQuery(RowShape shape, SyntaxNode? syntax) : base(syntax) { Shape = shape; }

   /// <summary>The shape of the rows the query produces.</summary>
   public RowShape Shape { get; }
}

public sealed class BoundEntityScan(EntityDef entity, SyntaxNode? syntax) : BoundQuery(RowShape.ForEntity(entity), syntax)
{
   public EntityDef Entity { get; } = entity;
}

public sealed class BoundWhere(BoundQuery input, RowVariable row, BoundExpr predicate, SyntaxNode? syntax) : BoundQuery(input.Shape, syntax)
{
   public BoundQuery Input { get; } = input;

   public RowVariable Row { get; } = row;

   public BoundExpr Predicate { get; } = predicate;
}

public sealed record BoundProjection(string Name, BoundExpr Expr);

public sealed class BoundSelect(BoundQuery input, RowVariable row, IReadOnlyList<BoundProjection> items, RowShape shape, SyntaxNode? syntax)
   : BoundQuery(shape, syntax)
{
   public BoundQuery Input { get; } = input;

   public RowVariable Row { get; } = row;

   public IReadOnlyList<BoundProjection> Items { get; } = items;
}

/// <summary>Adds members to the rows, keeping the existing ones (and the entity identity).</summary>
public sealed class BoundExtend(BoundQuery input, RowVariable row, IReadOnlyList<BoundProjection> items, RowShape shape, SyntaxNode? syntax)
   : BoundQuery(shape, syntax)
{
   public BoundQuery Input { get; } = input;

   public RowVariable Row { get; } = row;

   public IReadOnlyList<BoundProjection> Items { get; } = items;
}

public sealed record BoundSortKey(BoundExpr Expr, bool Descending);

public sealed class BoundOrderBy(BoundQuery input, RowVariable row, IReadOnlyList<BoundSortKey> keys, SyntaxNode? syntax) : BoundQuery(input.Shape, syntax)
{
   public BoundQuery Input { get; } = input;

   public RowVariable Row { get; } = row;

   public IReadOnlyList<BoundSortKey> Keys { get; } = keys;
}

/// <summary>Keeps the first <see cref="Count"/> rows; the count is an integer literal or parameter.</summary>
public sealed class BoundTake(BoundQuery input, BoundExpr count, SyntaxNode? syntax) : BoundQuery(input.Shape, syntax)
{
   public BoundQuery Input { get; } = input;

   public BoundExpr Count { get; } = count;
}

/// <summary>Drops the first <see cref="Count"/> rows; the count is an integer literal or parameter.</summary>
public sealed class BoundSkip(BoundQuery input, BoundExpr count, SyntaxNode? syntax) : BoundQuery(input.Shape, syntax)
{
   public BoundQuery Input { get; } = input;

   public BoundExpr Count { get; } = count;
}

public sealed class BoundDistinct(BoundQuery input, SyntaxNode? syntax) : BoundQuery(input.Shape, syntax)
{
   public BoundQuery Input { get; } = input;
}

/// <summary>The rows a collection navigation leads to from one owner row: <c>customer.orders</c>.</summary>
public sealed class BoundNavigationQuery(BoundExpr owner, NavigationDef navigation, SyntaxNode? syntax)
   : BoundQuery(RowShape.ForEntity(navigation.Target), syntax)
{
   public BoundExpr Owner { get; } = owner;

   public NavigationDef Navigation { get; } = navigation;
}

/// <summary>Groups rows by key parts bound against <see cref="Row"/>; the result's rows are the groups.</summary>
public sealed class BoundGroupBy(BoundQuery input, RowVariable row, IReadOnlyList<BoundProjection> keys, RowShape shape, SyntaxNode? syntax)
   : BoundQuery(shape, syntax)
{
   public BoundQuery Input { get; } = input;

   public RowVariable Row { get; } = row;

   public IReadOnlyList<BoundProjection> Keys { get; } = keys;
}

public enum BoundJoinKind : byte
{
   Inner,
   Left,
}

/// <summary>An explicit join; the items are bound against both rows (<c>outer</c> and <c>inner</c>).</summary>
public sealed class BoundJoin(BoundJoinKind kind, BoundQuery left, RowVariable leftRow, BoundQuery right, RowVariable rightRow,
                              BoundExpr condition, IReadOnlyList<BoundProjection> items, RowShape shape, SyntaxNode? syntax)
   : BoundQuery(shape, syntax)
{
   public BoundJoinKind Kind { get; } = kind;

   public BoundQuery Left { get; } = left;

   public RowVariable LeftRow { get; } = leftRow;

   public BoundQuery Right { get; } = right;

   public RowVariable RightRow { get; } = rightRow;

   public BoundExpr Condition { get; } = condition;

   public IReadOnlyList<BoundProjection> Items { get; } = items;
}

/// <summary>
/// The elements of a collection for each row, flattened: <c>customers.selectMany(orders)</c>. Without items the rows
/// are the elements; with items they are bound against the row (<c>outer</c>) and the element (<c>inner</c>).
/// </summary>
public sealed class BoundSelectMany(BoundQuery input, RowVariable row, BoundQuery collection, RowVariable elementRow,
                                    IReadOnlyList<BoundProjection>? items, RowShape shape, SyntaxNode? syntax)
   : BoundQuery(shape, syntax)
{
   public BoundQuery Input { get; } = input;

   public RowVariable Row { get; } = row;

   public BoundQuery Collection { get; } = collection;

   public RowVariable ElementRow { get; } = elementRow;

   public IReadOnlyList<BoundProjection>? Items { get; } = items;
}

public enum SetOperationKind : byte
{
   /// <summary>Rows of either, without duplicates.</summary>
   Union,

   /// <summary>Rows of both, duplicates kept (<c>concat</c>).</summary>
   UnionAll,

   Intersect,

   Except,
}

/// <summary>A set operation; the right side's columns are matched to the left's by name.</summary>
public sealed class BoundSetOperation(SetOperationKind kind, BoundQuery left, BoundQuery right, RowShape shape, SyntaxNode? syntax)
   : BoundQuery(shape, syntax)
{
   public SetOperationKind Kind { get; } = kind;

   public BoundQuery Left { get; } = left;

   public BoundQuery Right { get; } = right;
}

/// <summary>A use of a named query (<c>x := ...</c>); each use stands for a fresh copy of the definition.</summary>
public sealed class BoundLetQuery(BoundLet let, SyntaxNode? syntax) : BoundQuery(((BoundQuery)let.Value).Shape, syntax)
{
   public BoundLet Let { get; } = let;
}

#endregion

/// <summary>A named subtree: <c>x := expr;</c> or <c>let x = expr;</c>. The value is a query or a scalar expression.</summary>
public sealed class BoundLet(string name, BoundNode value, SyntaxNode syntax)
{
   public string Name { get; } = name;

   public BoundNode Value { get; } = value;

   public SyntaxNode Syntax { get; } = syntax;

   public bool IsQuery => Value is BoundQuery;
}

/// <summary>The bound form of a whole query text: its named subtrees and the final query or value.</summary>
public sealed class BoundProgram(string text, IReadOnlyList<BoundLet> lets, BoundNode? result, IReadOnlyList<QueryDiagnostic> diagnostics)
{
   public string Text { get; } = text;

   public IReadOnlyList<BoundLet> Lets { get; } = lets;

   /// <summary>
   /// A <see cref="BoundQuery"/>, a scalar <see cref="BoundExpr"/>, or a record of a query's first row (<see cref="BoundFirst"/>,
   /// or a member path from one); null when binding failed.
   /// </summary>
   public BoundNode? Result { get; } = result;

   public IReadOnlyList<QueryDiagnostic> Diagnostics { get; } = diagnostics;

   public bool Success => Result != null;

   public BoundQuery? Query => Result as BoundQuery;
}
