using System.Collections.Generic;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Functions;
using GalaxyData.Query.Results;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Planning;

/// <summary>
/// A value flowing through a plan. Identity is what matters: an operator that passes a column through keeps the
/// same object, so a column means the same values wherever it is referenced.
/// </summary>
public sealed class PlanColumn
{
   internal PlanColumn(int id, string name, ScalarType type, ColumnLineage lineage)
   {
      Id = id;
      Name = name;
      Type = type;
      Lineage = lineage;
   }

   /// <summary>Unique within a plan; used for printing.</summary>
   public int Id { get; }

   /// <summary>A readable name: the column or member the value came from.</summary>
   public string Name { get; }

   public ScalarType Type { get; }

   public ColumnLineage Lineage { get; }

   public override string ToString() => $"{Name}#{Id}";
}

public enum ParameterSource : byte
{
   /// <summary>A <c>$name</c> parameter supplied with the query.</summary>
   User,

   /// <summary><c>now()</c>: the UTC date-time the query started, the same everywhere in it.</summary>
   Now,

   /// <summary><c>today()</c>: the UTC date the query started.</summary>
   Today,
}

public abstract class PlanExpr
{
   private protected PlanExpr(ScalarType type) { Type = type; }

   public ScalarType Type { get; }
}

/// <summary>A column reference; the type can be more nullable than the column's (a column of an outer-joined row).</summary>
public sealed class PlanColumnRef(PlanColumn column, ScalarType type) : PlanExpr(type)
{
   public PlanColumnRef(PlanColumn column) : this(column, column.Type) { }

   public PlanColumn Column { get; } = column;
}

public sealed class PlanLiteral(object? value, ScalarType type) : PlanExpr(type)
{
   public object? Value { get; } = value;
}

/// <summary>A value known when the query runs rather than when it is planned.</summary>
public sealed class PlanParameter(ParameterSource source, string name, ScalarType type) : PlanExpr(type)
{
   public ParameterSource Source { get; } = source;

   /// <summary>The parameter name without <c>$</c>, or <c>now</c>/<c>today</c>.</summary>
   public string Name { get; } = name;
}

public sealed class PlanUnary(UnaryOp op, PlanExpr operand, ScalarType type) : PlanExpr(type)
{
   public UnaryOp Op { get; } = op;

   public PlanExpr Operand { get; } = operand;
}

public sealed class PlanBinary(BinaryOp op, PlanExpr left, PlanExpr right, ScalarType type) : PlanExpr(type)
{
   public BinaryOp Op { get; } = op;

   public PlanExpr Left { get; } = left;

   public PlanExpr Right { get; } = right;
}

public sealed class PlanIsNull(PlanExpr operand, bool negated) : PlanExpr(ScalarType.Boolean.AsNonNullable())
{
   public PlanExpr Operand { get; } = operand;

   public bool Negated { get; } = negated;
}

public sealed class PlanInList(PlanExpr operand, IReadOnlyList<PlanExpr> items, bool negated, ScalarType type) : PlanExpr(type)
{
   public PlanExpr Operand { get; } = operand;

   public IReadOnlyList<PlanExpr> Items { get; } = items;

   public bool Negated { get; } = negated;
}

public sealed class PlanConditional(PlanExpr condition, PlanExpr whenTrue, PlanExpr whenFalse, ScalarType type) : PlanExpr(type)
{
   public PlanExpr Condition { get; } = condition;

   public PlanExpr WhenTrue { get; } = whenTrue;

   public PlanExpr WhenFalse { get; } = whenFalse;
}

public sealed class PlanFunction(FunctionDef function, IReadOnlyList<PlanExpr> arguments, ScalarType type, SourceSpan? span) : PlanExpr(type)
{
   public FunctionDef Function { get; } = function;

   public IReadOnlyList<PlanExpr> Arguments { get; } = arguments;

   /// <summary>Where the call is in the query text, for messages; null when it comes from a virtual entity's definition.</summary>
   public SourceSpan? Span { get; } = span;
}

/// <summary>A range of the query text: [Start, End).</summary>
public readonly record struct SourceSpan(int Start, int End);
