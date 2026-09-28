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
   private readonly RecordBoundType type = new(row.Shape, false);

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

   /// <summary>A <see cref="BoundQuery"/> or a scalar <see cref="BoundExpr"/>; null when binding failed.</summary>
   public BoundNode? Result { get; } = result;

   public IReadOnlyList<QueryDiagnostic> Diagnostics { get; } = diagnostics;

   public bool Success => Result != null;

   public BoundQuery? Query => Result as BoundQuery;
}
